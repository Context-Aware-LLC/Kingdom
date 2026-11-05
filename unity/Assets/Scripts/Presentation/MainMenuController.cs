using System;
using System.Collections;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.Auth;
using Kingdom.Net.Client.Collection;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Client.King;
using Kingdom.Net.Client.Quests;
using Kingdom.Net.Client.Shop;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The front door — the first screen a player sees (dev-docs/ui/client-surfaces.md:7).
    /// A UI Toolkit meta-screen in the deckbuilder's lineage: theme.uss tokens, a UIDocument
    /// host, elements queried by name and wired in <see cref="OnEnable"/>.
    ///
    /// <para>Each destination has a named handler so a scene-loader drops straight in. Store,
    /// Collection, the pack vault and the roster are scenes of their own; the remaining unfinished
    /// destinations still log their intent. Nothing but the top status rail outlives a departure —
    /// every screen rebinds from its server-backed gateways on arrival rather than being handed
    /// cross-scene state.</para>
    ///
    /// <para><b>This screen is also where account setup begins.</b> A device that has never been
    /// named is routed through the one-time account flow before the menu is used. Deck context
    /// lives on the roster and collection surfaces rather than in menu chrome.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// MainMenuScreen.uxml and whose Panel Settings is MainMenuPanelSettings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainMenuController : MonoBehaviour
    {
        private VisualElement _root;
        private PlayerAccount _account;
        private LoadingOverlay _loading;

        [SerializeField] private string leaderboardUrl = "https://emberfall.fly.dev/leaderboard?limit=50";
        [SerializeField] private string questBaseUrl = "https://emberfall.fly.dev";
        private VisualElement _leaderboardModal;
        private ScrollView _leaderboardList;
        private Label _leaderboardStatus;
        private Label _leaderboardUpdated;
        private CancellationTokenSource _leaderboardRequest;
        private Button _ladderTab;
        private Button _favorTab;
        private Button _rollTab;
        private VisualElement _ladderColumns;
        private VisualElement _favorColumns;
        private VisualElement _rollColumns;
        private VisualElement _favorMe;

        private enum Board { Ladder, Favor, Roll }

        /// <summary>Which board the modal shows. The leaderboard button opens it on the ladder; the
        /// commission sheet opens it on the Favor.</summary>
        private Board _board;

        private Label _goldBalance;
        private Label _gemBalance;
        private Label _shardBalance;
        private VisualElement _topRail;
        private VisualElement _questList;
        private Label _questStatus;
        private HttpQuestGateway _questGateway;
        private Label _vaultPackCount;
        private VisualElement _kingDock;
        /// <summary>The King last read, for the commission sheet the crown opens. Null until read.</summary>
        private KingNow _king;
        /// <summary>This launch's read of <c>GET /king</c>, started once the update gate passes.
        /// Null until then, and on every path that never reaches it.</summary>
        private Task<KingNow> _kingRequest;
        private FirstRunTidbit _tidbit;
        private Rect _lastSafeArea;
        private int _lastSafeAreaScreenWidth = -1;
        private int _lastSafeAreaScreenHeight = -1;
        private bool _collectionTransitioning;

        /// <summary>True once <see cref="BeginFirstRun"/> has sent this launch to another scene.</summary>
        private bool _leavingForFirstRun;

        /// <summary>
        /// The one button the first-run guide leaves live, or null when the menu is free (Sam,
        /// 2026-11-04). <see cref="VaultButton"/> while starter packs are unopened,
        /// <see cref="CollectionButton"/> once they are, and the empty string while the starter grant
        /// is being presented, which locks every destination. Decided once per arrival in
        /// <see cref="ApplyGuide"/>.
        /// </summary>
        private string _guideTarget;

        private const string CollectionButton = "btn-collection";
        private const string VaultButton = "btn-card-vault";

        /// <summary>The guided button's soft brightness pulse.</summary>
        private IVisualElementScheduledItem _guidePulse;
        private IVisualElementScheduledItem _questPulse;

        /// <summary>How long the quest row is lit on its own before the store lesson opens over it.</summary>
        private const int QuestHighlightMs = 2400;

        /// <summary>The friends drawer this screen mounted, so the guide can lock its button too.</summary>
        private FriendsDrawerController _friends;

        /// <summary>Half a pulse: the time to swing from dim to bright, and the matching
        /// transition-duration on <c>.mm-button--guide</c> in mainmenu.uss. Change both together.</summary>
        private const int GuidePulseHalfMs = 1100;

        /// <summary>
        /// Everything the guide can dim and disable: every way off this menu. The guided one is
        /// skipped. The settings gear stays live (sound, language, sign-out are not destinations), and
        /// so does the quest strip, which claims rewards in place rather than going anywhere.
        /// Containers are named where a button carries a badge or sits in one, so the whole thing dims
        /// as a unit; <see cref="GuideContainer"/> maps a guided button to its entry here.
        /// </summary>
        private static readonly string[] GuideLockableElements =
        {
            "btn-find-game", CollectionButton, "btn-store", "btn-leaderboards",
            "vault-dock", "btn-ledger", "king-dock", "btn-achievements",
        };

        private static string GuideContainer(string button) => button == VaultButton ? "vault-dock" : button;

        /// <summary>Unopened starter packs on this arrival's shelf read. Drives the vault guide and the
        /// vault's mode. Zero when the shelf could not be read, which leaves the menu free.</summary>
        private long _starterPacks;

        /// <summary>This arrival's shelf read, kept for the starter grant presentation.</summary>
        private CollectionSnapshot _shelf;

        /// <summary>True when this arrival presents the starter grant: a new account's first menu.
        /// See <see cref="BeginFirstRun"/>.</summary>
        private bool _presentGift;

        private const float TopRailSafeAreaMarginPercent = 2f;
        private static readonly string StarterPackId = OnboardingGrant.StarterPack().Id;


        private SignInGateController _signIn;

        /// <summary>Frame-0 coverage, the same rig GameBootstrap/NetworkedGameBootstrap use
        /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/loading-screen-plate.md §5): the overlay exists before anything renders. The menu's
        /// own content starts hidden (mm-root--hidden in the UXML) so there is no frame where
        /// the unbound menu shows through before <see cref="RevealMenu"/> runs.</summary>
        private void Awake()
        {
            _loading = LoadingOverlay.Create(Camera.main, ScreenNav.MainMenuLoadingPlate);
        }

        private void OnEnable()
        {
            // GetComponent<UIDocument>().rootVisualElement is Unity's synthetic per-panel
            // wrapper, NOT the UXML's own top-level element — the "root" element (which carries
            // mm-root--hidden) is one level below it. Grabbing the wrapper here made
            // RevealMenu's RemoveFromClassList a silent no-op: the class it targeted was never
            // on the object it called it on, so the menu stayed visibility:hidden forever.
            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[MainMenu] No rootVisualElement — is the UXML assigned?");
                _loading?.NotifyBootFailed("UI not assigned — see console");
                return;
            }
            UiLocalizer.Attach(_root);

            Wire("btn-find-game", OnFindGame);
            Wire("btn-collection", OnCollection);
            Wire("btn-store", OnStore);
            Wire("btn-leaderboards", OnLeaderboards);
            Wire("btn-card-vault", OnCardVault);
            Wire("btn-ledger", () => LedgerOverlay.Open());
            Wire("btn-king", () => SuccessionRollOverlay.Open(_king, OpenFavorBoard));
            Wire("btn-leaderboard-close", CloseLeaderboard);
            BindAchievements();
            BindLeaderboard();

            BindBuildStamp();

            BindWalletBalances();
            BindQuestPanel();
            BindVaultEntry();
            BindKing();

            MountSettingsOverlay();
            _signIn = new SignInGateController(_root, AuthGateway.Provider);

            // A corrupt collection file must not brick the front door. The stores throw rather than
            // reporting an empty collection (so the next save cannot overwrite something
            // recoverable), which means the failure surfaces HERE without bricking the rest of the
            // menu.
            try
            {
                _account = PlayerAccount.Local;
                Debug.Log($"[MainMenu] collection scope → {_account.ScopeDirectory}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not open this device's collection: {ex.Message}");
                FinishBootstrap();
                return;
            }

            ResumeThenFinishBootstrap();
        }

        /// <summary>
        /// Pull the player's data down for a session that is ALREADY signed in, then carry on with
        /// the boot exactly as before.
        ///
        /// <para><b>The defect this fixes: the syncs only ever ran on the sign-in TRANSITION.</b>
        /// <c>SignInGateController.Succeed</c> was the one caller of the binding and of both
        /// gateways, and it runs only when the gate was shown — that is, on the first launch after
        /// installing, and never again. Every subsequent launch, which is the normal case, restored
        /// nothing: no collection, so the wall rendered the full pool from an absent ownership
        /// record; no roster, so the server's decks stayed on the server. Measured on
        /// a device 2026-09-02 — a launch with a restored session produced no <c>[SignIn]</c>,
        /// <c>[Roster]</c> or <c>[Collection]</c> line at all.</para>
        ///
        /// <para><b>Binding first, then the syncs, then re-reading the account</b> — the same order
        /// and the same reasons as the gate's own path (see <c>SignInGateController.Succeed</c>).
        /// <c>TryBindToIdentity</c> can SWITCH the active local account, which invalidates
        /// <c>PlayerAccount.Local</c>, so the field captured above is re-read afterwards or the rest
        /// of this screen talks to the account the device happened to mint.</para>
        ///
        /// <para><b>async void is correct here and only here</b>, the same rule the gate follows: a
        /// Unity lifecycle method cannot be awaited, and the try/catch is what keeps it honest. The
        /// boot plate is still up for the duration, which is exactly the moment a player expects to
        /// wait.</para>
        ///
        /// <para><b>The update gate comes first and is the only thing here that stops the menu.</b>
        /// A build under the server's minimum, or one that cannot reach the server at all, never
        /// gets past it: there is no offline mode, so a menu shown without a server only prepares a
        /// match that cannot start. Once it has passed, the server was answering a moment ago, and a
        /// sync that fails after that is a transient worth absorbing — which is why the catch below
        /// still carries on.</para>
        /// </summary>
        private async void ResumeThenFinishBootstrap()
        {
            // RETIREMENT TRIGGER: CLAUDE.md ("What does establish it is the update gate") states
            // that the menu never shows unless the server answered at boot. Removing this call, or
            // letting any verdict but Pass through, falsifies that paragraph — rewrite it here.
            UpdateGateResult gate;
            try
            {
                gate = await UpdateGateOverlay.CheckAsync();
            }
            catch (Exception)
            {
                // The funnel's `error` outcome. Rethrown: recording it must not change what a
                // throwing gate does to the boot.
                FirstSessionFunnel.GateError();
                throw;
            }
            FirstSessionFunnel.Gate(gate);
            if (this == null) return;
            if (gate.Verdict != UpdateGateVerdict.Pass)
            {
                UpdateGateOverlay.Show(gate);
                return;
            }

            // Not awaited: the crown appears when the King is read, and nothing below waits on him.
            _kingRequest = FetchKingAsync();
            _ = ShowKingAsync(_kingRequest);

            // A returning player never sees the sign-in gate, so their stored session is this
            // process's sign_in row (once per process; a sign-in through the gate records its own).
            if (_signIn != null && !_signIn.IsRequired && AuthGateway.IsSignedIn)
                FirstSessionFunnel.SignedInFromStoredSession();

            CollectionSnapshot collection = null;
            try
            {
                if (_signIn != null && !_signIn.IsRequired && AuthGateway.IsSignedIn)
                {
                    AuthSession session = AuthGateway.Provider.Current;
                    DeviceAccounts.TryBindToIdentity(session.UserId);

                    // REFRESHED, never the stored token. `session.AccessToken` is whatever was last
                    // written to disk, and a Supabase access token lives an hour — so on the launch
                    // this method exists to serve, a returning player's, it is usually expired.
                    // Both routes then answer 401, both syncs take the "keep whatever this device
                    // already had" branch, and the failure surfaces nowhere near here: the ownership
                    // record stays absent, absent means "do not constrain", the wall and the deck
                    // builder offer the full pool, and the socket closes at the queue with
                    // "deck rejected: You don't own X" — because CONNECT refreshes and this did not.
                    // Measured 2026-09-03: GET /collection and GET /roster both 401 at boot, then
                    // `[auth] authenticated` on the /ws hello eleven minutes later, same identity.
                    // TokenForConnectAsync is the one accessor that answers a token the server will
                    // take right now; PackVaultController already uses it for its HTTP calls.
                    string accessToken = await AuthGateway.TokenForConnectAsync();
                    Task<QuestSnapshot> questRequest = FetchQuestsAsync(accessToken);
                    collection = await CollectionGateway.SyncSnapshotAsync(accessToken);
                    RenderWalletBalances(collection);
                    await RosterGateway.SyncAsync(accessToken);
                    RenderQuests(await questRequest);

                    // Not awaited: nothing on this screen waits on Game Center or Play Games, and
                    // neither throws. Play Games also syncs at each match's end.
                    _ = GameCenterLadder.SyncAsync(accessToken);
                    _ = PlayGamesServices.SyncAchievementsAsync(accessToken);
                    _ = PlayGamesServices.LinkAsync(accessToken);

                    // The bind may have switched accounts underneath the field captured in OnEnable.
                    _account = PlayerAccount.Local;
                }
            }
            catch (Exception ex)
            {
                RenderQuestFailure(Loc.Get("menu_quests_unavailable"));
                Debug.LogWarning($"[MainMenu] could not restore this session's player data " +
                                 $"({ex.GetType().Name}: {ex.Message}); continuing with whatever " +
                                 "this device already had.");
            }

            // Reuse the collection read for first-run routing as well as the wallet. A failed read
            // is retried inside BeginFirstRun so a transient fault cannot strand starter packs.
            await BeginFirstRun(collection);
            FinishBootstrap();
        }

        /// <summary>
        /// Safe-area changes have no callback: rotation and Android system-bar changes can move the
        /// usable top/right edge while the scene stays enabled. Poll only the three values that can
        /// affect this read-only rail and write layout when one actually changes.
        /// </summary>
        private void Update()
        {
            RefreshTopRailSafeArea();
        }

        /// <summary>Drop the sign-in gate's deep-link subscription. <see cref="SignInGateController"/>
        /// listens on the static <see cref="Application.deepLinkActivated"/> event so a tapped email
        /// link can complete sign-in while the game is already running, and a fresh instance is built
        /// on every OnEnable (including the scene reload <see cref="DeviceAccounts.SwitchTo"/>
        /// triggers) — without this, a disabled screen's controller keeps handling links for a modal
        /// that no longer exists.</summary>
        private void OnDisable()
        {
            _leaderboardRequest?.Cancel();
            _leaderboardRequest?.Dispose();
            _leaderboardRequest = null;
            _signIn?.Close();
        }

        /// <summary>
        /// First-run routing: register, name yourself, then the starter gift, on this menu.
        ///
        /// <para><b>Registration comes first and cannot be skipped.</b> The two prompts are chained
        /// rather than shown together — the sign-in gate calls back once a session exists, and only
        /// then does the name prompt open. Opening both would stack two modals on a player who has not
        /// seen the game yet, and the name is the smaller of the two questions.</para>
        ///
        /// <para><b>The gift is presented here, not in another scene</b> (Sam, 2026-11-04). A new
        /// account's first menu shows the royal-grant tidbit, then the shards, gold and packs flying to
        /// where they live (<see cref="PresentGift"/>), then guides the player to the vault.</para>
        ///
        /// <para><b>The starter balance guides; the marker does not.</b> Every arrival that still
        /// holds starter packs holds the player to the vault (<see cref="ApplyGuide"/>).
        /// <see cref="FirstRunMarkers.GiftPresented"/> only stops the presentation playing twice, and
        /// <see cref="FirstRunMarkers.Gift"/> picks the vault's congratulations face.</para>
        /// </summary>
        private async Task BeginFirstRun(CollectionSnapshot collection = null)
        {
            // FIRST RUN IS A SCENE NOW (Sam, 2026-08-25). The sign-in gate and the name step were
            // two .mm-modal overlays on this screen; they are AccountCreate, and this is the only
            // thing left here that knows about them.
            //
            // The check stays HERE rather than in a bootstrap, because this is where the account is
            // already resolved and it is the scene the app boots into. Anything that owes an account
            // or a name leaves immediately; everyone else never sees the scene exist.
            //
            // Sam's 2026-08-08 ruling made this unskippable, so leaving is not optional and there is
            // no path that continues into the menu with the gate outstanding. AccountCreate hands
            // the player back here, where the gift is presented.
            bool needsAccount = _signIn != null && _signIn.IsRequired;
            bool needsName = AuthGateway.NeedsDisplayName;
            if (needsAccount || needsName)
            {
                _leavingForFirstRun = true;
                ScreenNav.Go(ScreenNav.AccountCreate);
                return;
            }

            if (!AuthGateway.IsSignedIn) return;

            // The ledger balance owns the vault guide. The local markers only decide whether the
            // grant is presented and which face the vault wears, so quitting after one open cannot
            // strand the rest of the starter packs behind a marker that has already been written.
            // Reuse a successful startup snapshot so the menu does not read /collection twice. Stay
            // self-sufficient when startup had no authoritative answer: a transient failure there
            // must not strand the gift, and TokenForConnectAsync is a no-op when the token is fresh.
            var snapshot = collection != null && collection.HasAuthoritativePacks
                ? collection
                : await PackGateway.ReadShelfAsync(await AuthGateway.TokenForConnectAsync());
            _shelf = snapshot;
            _starterPacks = snapshot.PackCount(StarterPackId);
            // Only before any starter pack is opened: a player who has opened one has had the gift.
            _presentGift = _starterPacks > 0
                && !FirstRunMarkers.Has(FirstRunMarkers.Gift)
                && !FirstRunMarkers.Has(FirstRunMarkers.GiftPresented);

            if (!_presentGift)
            {
                RenderWalletBalances(snapshot);
                RenderVaultEntry(snapshot);
                return;
            }

            // The menu opens on the balances from before the grant; each one ticks up as its item
            // lands (PresentGift).
            RenderWalletBalances(snapshot, OnboardingGrant.StarterGoldCount, OnboardingGrant.StarterShardCount);
            RenderVaultEntry(snapshot, _starterPacks);
        }

        // ── Starter grant presentation (Sam, 2026-11-04) ───────────────────────────────

        /// <summary>
        /// The royal-grant tidbit, then the grant itself on the menu's empty right side: shards fly
        /// into Collection, gold into the gold total, the packs into the vault. Every destination is
        /// locked meanwhile; afterwards the vault guide takes over.
        /// </summary>
        private void PresentGift()
        {
            _tidbit.Show(TidbitTrigger.StarterGrant, () =>
            {
                if (this == null || _root == null) return;

                Sprite packArt = null;
                new PackArtProvider().LoadPackArt(StarterPackId, pieces =>
                {
                    if (pieces != null && pieces.Count > 0) packArt = pieces[0].Sprite;
                });

                CollectionSnapshot shelf = _shelf;
                bool wallet = shelf != null && shelf.HasAuthoritativeWallet;
                var beats = new System.Collections.Generic.List<StarterGiftPresentation.Beat>
                {
                    new StarterGiftPresentation.Beat(
                        Loc.Get("menu_gift_shards"),
                        Loc.Format("menu_gift_amount", OnboardingGrant.StarterShardCount),
                        1, "mm-gift-icon--shards", null, _root.Q<VisualElement>(CollectionButton),
                        () => { if (wallet && _shardBalance != null) _shardBalance.text = WalletDisplay.IconAmount(shelf.Wallet.Shards); }),
                    new StarterGiftPresentation.Beat(
                        Loc.Get("menu_gift_gold"),
                        Loc.Format("menu_gift_amount", OnboardingGrant.StarterGoldCount),
                        1, "mm-gift-icon--gold", null, _root.Q<VisualElement>("gold-wallet"),
                        () => { if (wallet && _goldBalance != null) _goldBalance.text = WalletDisplay.IconAmount(shelf.Wallet.Gold); }),
                };
                // Before the packs (Sam, 2026-11-04): the cards granted outright, on the multi-pack
                // reveal's rarity plates, into Collection.
                StarterGiftPresentation.Beat cards = StarterCardsBeat();
                if (cards != null) beats.Add(cards);
                beats.Add(
                    new StarterGiftPresentation.Beat(
                        Loc.Get("menu_gift_packs"),
                        Loc.Format("menu_gift_amount", _starterPacks),
                        (int)Math.Min(_starterPacks, OnboardingGrant.StarterPackCount),
                        "mm-gift-icon--pack", packArt, _root.Q<VisualElement>(VaultButton),
                        () => RenderVaultEntry(shelf)));
                StarterGiftPresentation.Play(_root, beats, () =>
                {
                    if (this == null) return;
                    FirstRunMarkers.Mark(FirstRunMarkers.GiftPresented);
                    _presentGift = false;
                    GuideTo(VaultButton);
                });
            });
        }

        /// <summary>
        /// The card half of the grant — one of every Pendingham common and rare
        /// (<see cref="OnboardingGrant.CardGrant"/>) — as one plate per rarity, built from the
        /// multi-pack reveal's own <c>reveal-list</c> classes (packreveal.uss), counted the way that
        /// reveal counts commons. Null when the card data cannot be read: the beat is skipped, the
        /// cards are still owned.
        /// </summary>
        private StarterGiftPresentation.Beat StarterCardsBeat()
        {
            int commons = 0, rares = 0;
            try
            {
                Kingdom.Core.Contracts.ICardDatabase db = CardDatabaseLoader.Load();
                foreach (int id in OnboardingGrant.CardGrant(db))
                {
                    if (!db.TryGet(id, out Kingdom.Core.Cards.CardDefinition card)) continue;
                    if (card.Rarity == Kingdom.Core.Contracts.Rarity.COMMON) commons++;
                    else if (card.Rarity == Kingdom.Core.Contracts.Rarity.RARE) rares++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MainMenu] starter cards not presented ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
            if (commons + rares == 0) return null;

            var plates = new System.Collections.Generic.List<VisualElement>(2);
            if (commons > 0) plates.Add(RarityPlate(Kingdom.Core.Contracts.Rarity.COMMON, commons));
            if (rares > 0) plates.Add(RarityPlate(Kingdom.Core.Contracts.Rarity.RARE, rares));
            return new StarterGiftPresentation.Beat(
                Loc.Get("menu_gift_cards"),
                Loc.Format("menu_gift_cards_amount", commons + rares),
                plates, _root.Q<VisualElement>(CollectionButton), onLanded: null);
        }

        private static VisualElement RarityPlate(Kingdom.Core.Contracts.Rarity rarity, int count)
        {
            var panel = new VisualElement { pickingMode = PickingMode.Ignore };
            panel.AddToClassList("reveal-list__panel");
            panel.AddToClassList(CardClassTokens.RarityToken(rarity, "reveal-list__panel--"));

            var header = new VisualElement { pickingMode = PickingMode.Ignore };
            header.AddToClassList("reveal-list__header");
            var heading = new Label(GameTerms.Rarity(rarity)) { pickingMode = PickingMode.Ignore };
            heading.AddToClassList("reveal-list__rarity");
            header.Add(heading);
            panel.Add(header);

            var body = new VisualElement { pickingMode = PickingMode.Ignore };
            body.AddToClassList("reveal-list__body");
            var total = new Label(Loc.Plural("pack_reveal_card_count", count, count)) { pickingMode = PickingMode.Ignore };
            total.AddToClassList("reveal-list__common-count");
            body.Add(total);
            panel.Add(body);
            return panel;
        }

        /// <summary>Bootstrap (identity/account) is done. Reveal the menu once the boot plate has
        /// actually finished fading — not on this signal directly.
        ///
        /// <para>Screen Space Overlay always paints over LoadingOverlay's camera-parented rig,
        /// regardless of the rig's own fade-out timing — revealing on this signal alone would
        /// race the overlay off the screen the instant identity work finishes, which is close
        /// enough to instant that the plate would never be seen at all. <see cref="RevealMenu"/>
        /// only runs once <see cref="LoadingOverlay.OnFadedOut"/> fires.</para></summary>
        private void FinishBootstrap()
        {
            // LoadingOverlay's player path uses blocking Addressables completion. Pay that cost for
            // the next Collection plate now, while this screen's own boot plate is still covering
            // the menu, rather than on the later button press the player expects to answer at once.
            CollectionController.PrewarmNextLoadingPlate();

            if (_loading != null)
            {
                _loading.OnFadedOut += RevealMenu;
                _loading.NotifyReady();
            }
            else
            {
                RevealMenu();
            }
        }

        /// <summary>Show the menu (and whatever modal OnEnable decided to open under it). Called
        /// from <see cref="FinishBootstrap"/> — directly when there is no boot plate, otherwise
        /// once the plate has finished fading.</summary>
        private void RevealMenu()
        {
            // Before the menu is shown, so a guided player never sees a frame of live buttons.
            if (!_leavingForFirstRun) ApplyGuide();

            _root.RemoveFromClassList("mm-root--hidden");
            // Let the revealed tree paint once at its entrance pose before transitioning the
            // command assembly into place. Removing both classes in the same layout pass snaps
            // directly to the final state and makes the transition invisible.
            _root.schedule.Execute(() => _root.RemoveFromClassList("mm-root--entering"))
                .ExecuteLater(16);

            // First look (#403). FinishBootstrap reveals the menu even on a launch BeginFirstRun
            // has already sent elsewhere, and a lesson shown under a pending scene change is one
            // the player glimpses and never reads.
            if (_leavingForFirstRun) return;
            _tidbit = new FirstRunTidbit(_root);
            if (_presentGift)
            {
                PresentGift();
                // Nothing else opens over the grant: the Herald records the reign silently and a
                // prize choice waits for the next arrival.
                _ = AnnounceKingAsync(firstLook: true);
                return;
            }

            // Back from the starter packs: the menu's lesson, the quest row, the store lesson, and
            // only then the Collection guide (Sam, 2026-11-05).
            if (_guideTarget == CollectionButton)
            {
                PlayReturnFromPacks();
                _ = AnnounceKingAsync(firstLook: true);
                return;
            }

            // The vault guide follows the grant's own tidbit, so the menu's lesson waits until the
            // packs are open.
            bool firstLook = _guideTarget != VaultButton && _tidbit.Show(TidbitTrigger.MainMenuArrival);
            // While the guide holds the menu nothing else opens over it: the Herald records the
            // reign silently and a prize choice waits for the next arrival, exactly as they do under
            // the first-run lesson.
            _ = AnnounceKingAsync(firstLook || _guideTarget != null);
        }

        // ── First-run guide (Sam, 2026-11-04) ──────────────────────────────────────────

        /// <summary>
        /// The menu a new player comes back to after the starter packs: "the quintessential joys"
        /// first, then the quest row lit while "you don't have to be rich to become king" (which is
        /// about questing) is read, then the Collection guide. The whole menu is locked until then.
        /// A lesson already seen is skipped, and the row is lit only for the store lesson it
        /// illustrates.
        /// </summary>
        private void PlayReturnFromPacks()
        {
            GuideTo("");
            _tidbit.Show(TidbitTrigger.MainMenuArrival, () =>
            {
                if (FirstRunMarkers.Has(FirstRunMarkers.Tidbit("store")))
                {
                    GuideTo(CollectionButton);
                    return;
                }
                VisualElement strip = _root.Q<VisualElement>("quest-strip");
                LightQuests(strip, true);
                _root.schedule.Execute(() => _tidbit.Show(TidbitTrigger.QuestsHighlighted, () =>
                {
                    LightQuests(strip, false);
                    GuideTo(CollectionButton);
                })).ExecuteLater(QuestHighlightMs);
            });
        }

        /// <summary>The quest row's guide pulse: the same breath as a guided button, on the cards.</summary>
        private void LightQuests(VisualElement strip, bool on)
        {
            _questPulse?.Pause();
            _questPulse = null;
            if (strip == null) return;
            strip.EnableInClassList("mm-quest-strip--guide", on);
            strip.RemoveFromClassList("mm-quest-strip--guide-bright");
            if (on)
                _questPulse = strip.schedule
                    .Execute(() => strip.ToggleInClassList("mm-quest-strip--guide-bright"))
                    .Every(GuidePulseHalfMs);
        }

        /// <summary>
        /// Hold a new player to one destination: dim and disable every other one and pulse the
        /// guided button's brightness, until they tap it. Unopened starter packs guide to the vault;
        /// then the collection guide guides to Collection. While the grant is being presented,
        /// everything is locked.
        ///
        /// <para><b>The vault guide is the starter balance</b>, read this arrival; nothing on disk
        /// arms it, so it ends the moment the last starter pack is opened.</para>
        ///
        /// <para><b>The collection guide is armed by the vault, lifted here.</b>
        /// <c>PackVaultController</c> writes <see cref="FirstRunMarkers.CollectionGuide"/> when the
        /// last starter pack opens, and <see cref="OnCollection"/> writes
        /// <see cref="FirstRunMarkers.CollectionGuideDone"/>. A returning player has neither marker
        /// and is never guided. The markers are read here, after the boot's identity bind, because
        /// that bind can switch the account they are scoped to. They are on disk, so a player who
        /// quits on the guided menu comes back to it.</para>
        /// </summary>
        private void ApplyGuide()
        {
            string target = null;
            try
            {
                if (AuthGateway.IsSignedIn)
                {
                    if (_presentGift) target = "";
                    else if (_starterPacks > 0) target = VaultButton;
                    else if (FirstRunMarkers.CollectionGuidePending()) target = CollectionButton;
                }
            }
            catch (Exception ex)
            {
                // A guide that cannot be read must not be able to lock the front door.
                Debug.LogWarning($"[MainMenu] could not read the first-run guide " +
                                 $"({ex.GetType().Name}: {ex.Message}); showing the full menu.");
                target = null;
            }

            GuideTo(target);
        }

        /// <summary>Lock every destination but <paramref name="target"/> and pulse it. Null frees the
        /// menu; the empty string locks all of it.</summary>
        private void GuideTo(string target)
        {
            ReleaseGuide();
            _guideTarget = target;
            if (target == null) return;

            string live = GuideContainer(target);
            foreach (string name in GuideLockableElements)
            {
                if (name == live) continue;
                VisualElement element = _root.Q<VisualElement>(name);
                if (element == null) continue;
                element.SetEnabled(false);
                element.AddToClassList("mm-guide-locked");
            }
            _friends?.SetGuideLocked(true);

            if (target.Length == 0) return;
            var button = _root.Q<Button>(target);
            if (button == null) return;
            button.AddToClassList("mm-button--guide");
            // USS does the easing (transition on .mm-button--guide); this only flips the bright class
            // each half period, the same pattern the vault's entrance uses. The schedule is owned by
            // the element, so a scene change drops it with the panel.
            _guidePulse = button.schedule
                .Execute(() => button.ToggleInClassList("mm-button--guide-bright"))
                .Every(GuidePulseHalfMs);
        }

        /// <summary>Undo whatever <see cref="GuideTo"/> last did.</summary>
        private void ReleaseGuide()
        {
            _guidePulse?.Pause();
            _guidePulse = null;
            if (_guideTarget == null) return;

            foreach (string name in GuideLockableElements)
            {
                VisualElement element = _root.Q<VisualElement>(name);
                if (element == null || !element.ClassListContains("mm-guide-locked")) continue;
                element.SetEnabled(true);
                element.RemoveFromClassList("mm-guide-locked");
            }
            _friends?.SetGuideLocked(false);

            if (_guideTarget.Length > 0)
            {
                var button = _root.Q<Button>(_guideTarget);
                button?.RemoveFromClassList("mm-button--guide");
                button?.RemoveFromClassList("mm-button--guide-bright");
            }
            _guideTarget = null;
        }

        /// <summary>The player went to Collection: the guide is done for good.</summary>
        private void CompleteCollectionGuide()
        {
            if (_guideTarget != CollectionButton) return;
            _guideTarget = null;
            _guidePulse?.Pause();
            try
            {
                FirstRunMarkers.Mark(FirstRunMarkers.CollectionGuideDone);
            }
            catch (Exception ex)
            {
                // Costs one more guided menu on the next arrival, never the trip to Collection.
                Debug.LogWarning($"[MainMenu] could not record the collection guide as done " +
                                 $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // ── The King of the Week (#421) ────────────────────────────────────────────────

        /// <summary>Last reign the Herald announced on this device. On the device, not the account
        /// (spec, "On the main menu"): a new King is news once per handset.</summary>
        private const string LastHeardReignKey = "king_last_heard_reign";
        private const int KingTimeoutSeconds = 10;

        /// <summary>The crown, hidden until a King is read. With no King the dock is exactly the
        /// vault and the book.</summary>
        private void BindKing()
        {
            _kingDock = _root.Q<VisualElement>("king-dock");
            if (_kingDock != null) _kingDock.style.display = DisplayStyle.None;
        }

        /// <summary>This week's King, or null when there is none or he could not be read. The route
        /// is public, so no token: it answers the same for everyone.</summary>
        private static async Task<KingNow> FetchKingAsync()
        {
            try
            {
                return await new HttpKingGateway(
                    CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(KingTimeoutSeconds)).FetchKingAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[King] read threw ({ex.GetType().Name}: {ex.Message}); no crown this launch.");
                return null;
            }
        }

        private async Task ShowKingAsync(Task<KingNow> request)
        {
            KingNow king = await request;
            if (this == null) return;   // the menu left while the read was in flight
            // The Favor board is this reign's and the Roll is every reign's, so with no King there is
            // no tab for either. Before the dock check: the tabs live in the leaderboard modal and do
            // not need the dock.
            DisplayStyle kingTabs = king == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (_favorTab != null) _favorTab.style.display = kingTabs;
            if (_rollTab != null) _rollTab.style.display = kingTabs;
            if (_kingDock == null) return;
            if (king == null)
            {
                _kingDock.style.display = DisplayStyle.None;
                return;
            }

            // His words are on the commission sheet the crown opens: beside the crown, over
            // the stage art, they could not be read (Sam, 2026-11-03).
            _king = king;
            _kingDock.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// The Herald, on the first launch of a new reign: <i>"The King has 'ost his wits! Long 'ive
        /// the King!"</i> Only for a reign later than the last one heard on this device, and never
        /// on top of the first-run lesson — that launch records the reign silently, so the next
        /// announcement is the next King's.
        ///
        /// <para><b>The tidbit is built here, not registered in <see cref="TidbitCatalog"/></b>,
        /// because its id is per reign and FirstRunTidbit shows an id once. The reign counts as heard when the scroll
        /// rolls shut, the same moment the tidbit marker is written: a player who quits mid-card has
        /// not heard it.</para>
        ///
        /// <para><b>This is also the arrival sequence.</b> Arrival shows one scroll at a time: the
        /// first-run lesson, else the Herald, then a prize choice (<see cref="OfferPrizeAsync"/>)
        /// once the Herald rolls shut, or at once when he has nothing to say.</para>
        /// </summary>
        private async Task AnnounceKingAsync(bool firstLook)
        {
            if (_kingRequest == null) return;
            KingNow king = await _kingRequest;
            if (this == null || _tidbit == null) return;

            // No King, or none read: no Herald, but a choice opened under an earlier one still waits.
            if (king == null) { _ = OfferPrizeAsync(firstLook); return; }

            int heard = PlayerPrefs.GetInt(LastHeardReignKey, 0);
            if (king.Reign <= heard) { _ = OfferPrizeAsync(firstLook); return; }

            int reign = king.Reign;
            if (firstLook || _tidbit.IsShowing)
            {
                RememberReign(reign);
                return;
            }

            var herald = new Tidbit(
                "king_reign_" + reign.ToString(CultureInfo.InvariantCulture),
                TidbitTrigger.MainMenuArrival,
                "king_herald_title",
                "king_herald_body",
                artKey: null,
                voiceAddress: "vo/vo_herald_new_king");
            _tidbit.Show(new[] { herald }, () =>
            {
                RememberReign(reign);
                _ = OfferPrizeAsync(firstLook: false);
            });
        }

        /// <summary>
        /// A card back to choose (#421), when a top-three place the player held before has opened
        /// one. Only into a quiet menu: never over the first-run lesson, a tidbit, or the Roll or
        /// Ledger the player opened while the read was in flight. Skipped, the choice waits on the
        /// server for the next arrival; nothing expires.
        /// </summary>
        private async Task OfferPrizeAsync(bool firstLook)
        {
            if (firstLook || !AuthGateway.IsSignedIn) return;

            System.Collections.Generic.IReadOnlyList<PrizeChoiceOffer> offers;
            try
            {
                string token = await AuthGateway.TokenForConnectAsync();
                offers = await new HttpKingGateway(
                    CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(KingTimeoutSeconds)).FetchPrizeChoicesAsync(token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[King] prize choices not read ({ex.GetType().Name}: {ex.Message}); offered next arrival.");
                return;
            }

            if (this == null || offers == null || offers.Count == 0) return;   // left, unread, or none
            // Each overlay is found by its ObjectName, the name its Open method gives its GameObject.
            if (_tidbit?.IsShowing == true ||
                _root.Query(className: "mm-modal").Where(m => !m.ClassListContains("mm-modal--hidden")).First() != null ||
                GameObject.Find(SuccessionRollOverlay.ObjectName) != null || GameObject.Find(LedgerOverlay.ObjectName) != null ||
                GameObject.Find(PrizeChoiceOverlay.ObjectName) != null)
                return;
            PrizeChoiceOverlay.Open(offers);
        }

        private static void RememberReign(int reign)
        {
            if (reign <= PlayerPrefs.GetInt(LastHeardReignKey, 0)) return;
            PlayerPrefs.SetInt(LastHeardReignKey, reign);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// The Play Games achievements chip (#418), Android only. It is also the way back into
        /// Play Games when the automatic sign-in failed, which Play's quality checklist requires.
        /// </summary>
        private void BindAchievements()
        {
            var button = _root.Q<Button>("btn-achievements");
            if (button == null || !PlayGamesServices.IsSupported) return;
            button.style.display = DisplayStyle.Flex;
            button.RegisterCallback<ClickEvent>(async _ =>
            {
                button.SetEnabled(false);
                try { await PlayGamesServices.ShowAchievementsAsync(); }
                finally { button.SetEnabled(true); }
            });
        }

        private void Wire(string name, System.Action onClick)
        {
            var button = _root.Q<Button>(name);
            if (button == null) { Debug.LogWarning($"[MainMenu] Button '{name}' not found in UXML."); return; }
            // Belt and braces: the guide disables these elements, and this keeps a click that
            // slips past (a stale event, a future control) from leaving the guided menu.
            button.RegisterCallback<ClickEvent>(_ =>
            {
                if (_guideTarget != null && name != _guideTarget) return;
                onClick();
            });
        }

        // ── Build stamp ────────────────────────────────────────────────────────────────

        /// <summary>
        /// #195 — replace the hand-written "M1 · vertical slice" with the build a tester is
        /// actually holding. The label's UXML text is now only what shows if this never runs.
        /// </summary>
        private void BindBuildStamp()
        {
            var label = _root.Q<Label>("version-label");
            if (label == null) return;

            if (Debug.isDebugBuild)
            {
                label.text = BuildStamp.Display;
                return;
            }

            // Useful evidence in Editor and development players; presentation noise everywhere
            // else. Release builds retain BuildStamp in logs without painting it over the artwork.
            label.style.display = DisplayStyle.None;
        }

        // ── Wallet balances ────────────────────────────────────────────────────────────

        private void BindWalletBalances()
        {
            _topRail = _root.Q<VisualElement>("top-rail");
            _goldBalance = _root.Q<Label>("gold-balance");
            _gemBalance = _root.Q<Label>("gem-balance");
            _shardBalance = _root.Q<Label>("shard-balance");
            RenderWalletBalances(null);
            _lastSafeAreaScreenWidth = -1;
            RefreshTopRailSafeArea();
        }

        /// <summary>
        /// Adopt the collection endpoint's ledger projection directly. A failed or absent snapshot
        /// renders visual zeroes; authoritative missing ledger rows also arrive as zero. Adjacent
        /// icons carry the currency names, so the labels render amounts only. The fallback is never
        /// persisted or spent.
        /// </summary>
        /// <param name="withheldGold">Gold not shown yet, because it is still to land in the starter
        /// grant presentation. The same for <paramref name="withheldShards"/>.</param>
        private void RenderWalletBalances(CollectionSnapshot snapshot, long withheldGold = 0, long withheldShards = 0)
        {
            string fallback = WalletDisplay.IconAmount(0);
            bool available = snapshot != null && snapshot.HasAuthoritativeWallet;
            string gold = available
                ? WalletDisplay.IconAmount(Math.Max(0, snapshot.Wallet.Gold - withheldGold))
                : fallback;
            string gems = available
                ? WalletDisplay.IconAmount(snapshot.Wallet.Gems)
                : fallback;
            string shards = available
                ? WalletDisplay.IconAmount(Math.Max(0, snapshot.Wallet.Shards - withheldShards))
                : fallback;

            if (_goldBalance != null) _goldBalance.text = gold;
            if (_gemBalance != null) _gemBalance.text = gems;
            if (_shardBalance != null) _shardBalance.text = shards;
        }

        // ── Daily quests ──────────────────────────────────────────────────────────────

        private void BindQuestPanel()
        {
            _questList = _root.Q<VisualElement>("quest-list");
            _questStatus = _root.Q<Label>("quest-status");
            _questGateway = new HttpQuestGateway(
                questBaseUrl,
                new UnityWebRequestAuthHttp(15));
            if (_questList != null) _questList.Clear();
            if (_questStatus != null)
            {
                _questStatus.text = Loc.Get("common_loading_quests");
                _questStatus.RemoveFromClassList("mm-quest-status--hidden");
            }
        }

        private async Task<QuestSnapshot> FetchQuestsAsync(string accessToken)
        {
            try
            {
                return await _questGateway.FetchAsync(accessToken);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Quests] fetch threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "reporting it as offline.");
                return QuestSnapshot.Failed(
                    QuestFetchStatus.Offline,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Paint the server's newest-first three-day quest window. This is a projection only:
        /// progress still moves exclusively from server-observed match completion, and there is no
        /// claim control because rewards auto-grant on the transition into completion.
        /// </summary>
        private void RenderQuests(QuestSnapshot snapshot)
        {
            if (_questList == null || _questStatus == null) return;
            _questList.Clear();

            if (snapshot == null || snapshot.Status != QuestFetchStatus.Known)
            {
                string message = snapshot?.Status == QuestFetchStatus.Unauthenticated
                    ? Loc.Get("menu_quests_signed_out")
                    : Loc.Get("menu_quests_unavailable");
                RenderQuestFailure(message);
                return;
            }

            if (snapshot.Quests.Count == 0)
            {
                RenderQuestFailure(Loc.Get("menu_quests_none"));
                return;
            }

            _questStatus.AddToClassList("mm-quest-status--hidden");
            for (int index = 0; index < snapshot.Quests.Count; index++)
                _questList.Add(BuildQuestRow(snapshot.Quests[index]));
        }

        private void RenderQuestFailure(string message)
        {
            if (_questList != null) _questList.Clear();
            if (_questStatus == null) return;
            _questStatus.text = message;
            _questStatus.RemoveFromClassList("mm-quest-status--hidden");
        }

        private static VisualElement BuildQuestRow(QuestEntry quest)
        {
            var row = new VisualElement();
            row.AddToClassList("mm-quest-row");
            if (quest.Complete) row.AddToClassList("mm-quest-row--complete");

            var heading = new VisualElement();
            heading.AddToClassList("mm-quest-row__heading");

            var title = new Label(quest.Title);
            title.AddToClassList("mm-quest-title");
            heading.Add(title);

            var reward = new VisualElement();
            reward.AddToClassList("mm-quest-reward");
            var rewardIcon = new VisualElement();
            rewardIcon.AddToClassList("mm-quest-reward__icon");
            reward.Add(rewardIcon);
            var rewardAmount = new Label(Loc.Format("menu_quest_reward", quest.RewardGold));
            rewardAmount.AddToClassList("mm-quest-reward__amount");
            reward.Add(rewardAmount);
            heading.Add(reward);
            row.Add(heading);

            if (!string.IsNullOrEmpty(quest.Quip))
            {
                // USS has no line-height, so the quip's two lines are tightened with the rich-text tag.
                var quip = new Label("<line-height=85%>" + quest.Quip);
                quip.AddToClassList("mm-quest-quip");
                row.Add(quip);
            }

            var track = new VisualElement();
            track.AddToClassList("mm-quest-progress");
            var fill = new VisualElement();
            fill.AddToClassList("mm-quest-progress__fill");
            fill.style.width = Length.Percent(
                Mathf.Clamp01((float)quest.Progress / quest.Goal) * 100f);
            track.Add(fill);

            var detail = new Label(quest.Complete
                ? Loc.Get("menu_quest_complete")
                : Loc.Format("common_1_d_2_d", quest.Progress, quest.Goal));
            detail.AddToClassList("mm-quest-detail");

            var summary = new VisualElement();
            summary.AddToClassList("mm-quest-summary");
            summary.Add(track);
            summary.Add(detail);
            row.Add(summary);
            return row;
        }

        private void RefreshTopRailSafeArea()
        {
            if (_topRail == null) return;

            int screenWidth = Screen.width;
            int screenHeight = Screen.height;
            Rect safeArea = Screen.safeArea;
            if (screenWidth == _lastSafeAreaScreenWidth
                && screenHeight == _lastSafeAreaScreenHeight
                && safeArea == _lastSafeArea)
                return;

            _lastSafeAreaScreenWidth = screenWidth;
            _lastSafeAreaScreenHeight = screenHeight;
            _lastSafeArea = safeArea;

            if (screenWidth <= 0 || screenHeight <= 0) return;
            if (safeArea.width <= 1f || safeArea.height <= 1f)
                safeArea = new Rect(0f, 0f, screenWidth, screenHeight);

            float rightInsetPercent =
                Mathf.Clamp(screenWidth - safeArea.xMax, 0f, screenWidth) / screenWidth * 100f;
            float topInsetPercent =
                Mathf.Clamp(screenHeight - safeArea.yMax, 0f, screenHeight) / screenHeight * 100f;

            // Percentages keep the breathing room proportional across phone and desktop canvases;
            // the normalized device inset moves the anchor from the display edge to the safe edge.
            _topRail.style.right = Length.Percent(
                Mathf.Min(100f, rightInsetPercent + TopRailSafeAreaMarginPercent));
            _topRail.style.top = Length.Percent(
                Mathf.Min(100f, topInsetPercent + TopRailSafeAreaMarginPercent));
        }

        // ── Settings overlay (C-G1-3 / decision 23 §5, mounted here too since Sam 2026-08-09) ──

        /// <summary>
        /// Mount the same settings overlay a match uses — same UXML, same gear button, same
        /// bottom-right corner (mainmenu.uss moved the version footer to the left to make room).
        /// Switch account (dev/testing only) lives inside it now; Concede does not apply here, so
        /// <see cref="MatchSettingsController.Init"/> is told to hide that row.
        /// </summary>
        private void MountSettingsOverlay()
        {
            MatchSettingsController.Mount(onConcede: null, showConcede: false);
            // #409: the friends button sits directly above the gear, on every top-level screen
            // except the match.
            _friends = FriendsDrawerController.Mount();
        }

        // ── Shared modal plumbing ───────────────────────────────────────────────────────

        private static void Show(VisualElement modal) => modal?.RemoveFromClassList("mm-modal--hidden");
        private static void Hide(VisualElement modal) => modal?.AddToClassList("mm-modal--hidden");

        /// <summary>A space, not an empty string, so the error line keeps its height and the buttons
        /// below do not jump when a refusal appears or clears.</summary>
        private static void SetError(Label label, string message)
        {
            if (label != null) label.text = string.IsNullOrEmpty(message) ? " " : message;
        }

        // ---- Destinations (stubbed — navigation is a later pass) ----
        // Find Game → CharacterRoster → BoardNetworked (pick first, then enter server matchmaking).
        /// <summary>
        /// Find Game → <c>CharacterRoster</c> → <c>BoardNetworked</c>.
        ///
        /// <para>The deck roster IS the deck-selection step: it already shows every deck and its
        /// queue eligibility. A queue-ready choice then loads the board, whose network bootstrap
        /// submits that exact deck before pairing.</para>
        ///
        /// <para>It was a two-step pick until ADR-0009 — a character, then one of its decks. The
        /// first step is gone with the container.</para>
        /// </summary>
        private void OnFindGame()
        {
            MatchDeckSelection.OpenPicker();
        }

        /// <summary>
        /// Collection is the one home for the card wall and deck editing. The deck drawer starts
        /// closed; deck management remains one explicit doorway inside it.
        /// </summary>
        private void OnCollection()
        {
            if (_collectionTransitioning) return;
            CompleteCollectionGuide();
            MatchDeckSelection.Cancel();
            CollectionController.ReturnScene = ScreenNav.MainMenu;
            CollectionController.PendingDeckId = "";
            StartCoroutine(OpenCollection());
        }

        private IEnumerator OpenCollection()
        {
            _collectionTransitioning = true;
            LoadingOverlay transition = CollectionController.BeginLoadingTransition(Camera.main);
            if (transition == null)
            {
                _collectionTransitioning = false;
                ScreenNav.Go(ScreenNav.Collection);
                yield break;
            }

            // The plate is world-space and the menu is Screen Space Overlay, so the source UI must
            // stop painting before the plate can be seen. Yielding one frame is load-bearing: it
            // gives Unity a real render/present boundary before scene activation starts doing work.
            _root.SetEnabled(false);
            _root.style.opacity = 0f;
            yield return null;

            AsyncOperation operation = ScreenNav.GoAsync(ScreenNav.Collection);
            if (operation != null) yield break;

            // Build-list validation failed. Do not strand a persistent plate over a menu that never
            // left; return the source screen to its exact interactive state.
            CollectionController.CancelLoadingTransition(transition);
            _root.style.opacity = 1f;
            _root.SetEnabled(true);
            _collectionTransitioning = false;
        }

        /// <summary>Store → <c>Shop</c>. It rendered as an overlay on this screen until
        /// 2026-08-20; it is a destination like Collection and the roster, and its plate was
        /// already full-screen and opaque — the overlay was hosting, not design.</summary>
        private void OnStore()
        {
            ShopScreenController.ReturnScene = ScreenNav.MainMenu;
            ScreenNav.Go(ScreenNav.Shop);
        }

        /// <summary>Open the player's unopened packs as a first-class destination. The round menu
        /// socket is intentionally outside the row-button group, but navigation uses the same
        /// caller-writes-its-way-home contract as the store and collection.</summary>
        private void OnCardVault()
        {
            PackVaultController.ReturnScene = ScreenNav.MainMenu;
            PackVaultController.Pending = null;
            // Starter packs still held and none opened yet: the vault wears its gift face.
            PackVaultController.Mode = _starterPacks > 0 && !FirstRunMarkers.Has(FirstRunMarkers.Gift)
                ? PackVaultMode.Gift
                : PackVaultMode.Vault;
            ScreenNav.Go(ScreenNav.PackVault);
        }

        /// <summary>The pack fan is a permanent first-class destination. Its badge reports the
        /// live unopened-pack total only when positive; the vault stays accessible when empty.</summary>
        private void BindVaultEntry()
        {
            _vaultPackCount = _root.Q<Label>("vault-pack-count");
            if (_vaultPackCount != null)
            {
                _vaultPackCount.text = "";
                _vaultPackCount.style.display = DisplayStyle.None;
            }
        }

        /// <param name="withheld">Packs not counted yet, because they are still to land in the starter
        /// grant presentation.</param>
        private void RenderVaultEntry(Kingdom.Net.Client.Collection.CollectionSnapshot snapshot, long withheld = 0)
        {
            if (_vaultPackCount == null) return;

            long total = 0;
            if (snapshot != null)
            {
                foreach (long count in snapshot.Packs.Values)
                {
                    if (count > 0) total += count;
                }
            }
            total = Math.Min(100, Math.Max(0, total - withheld));

            _vaultPackCount.style.display = total > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _vaultPackCount.text = total > 99
                ? Loc.Get("menu_vault_pack_count_max")
                : total > 0 ? Loc.Format("menu_vault_pack_count", total) : "";
        }

        private void BindLeaderboard()
        {
            _leaderboardModal = _root.Q<VisualElement>("leaderboard-modal");
            _leaderboardList = _root.Q<ScrollView>("leaderboard-list");
            _leaderboardStatus = _root.Q<Label>("leaderboard-status");
            _leaderboardUpdated = _root.Q<Label>("leaderboard-updated");

            // Outside tap closes it (2026-09-08). `#leaderboard-modal` (mm-modal) is BOTH the
            // full-bleed backdrop and the flex container centring `.mm-modal__panel` inside it —
            // there is no separate scrim element the way Collection's deck drawer has one. A click
            // on the panel or any of its children bubbles up through this same handler, so the
            // target check is what keeps it from closing on a click that landed on real content.
            _leaderboardModal?.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target == _leaderboardModal) CloseLeaderboard();
            });

            // Three boards, one list (#421). The tabs are ShopPanelController's: each names its board
            // and selects it, and ShowBoard is the one place the tabs and columns change together.
            _ladderTab = _root.Q<Button>("leaderboard-tab-ladder");
            _favorTab = _root.Q<Button>("leaderboard-tab-favor");
            _rollTab = _root.Q<Button>("leaderboard-tab-roll");
            _ladderColumns = _root.Q<VisualElement>("leaderboard-columns");
            _favorColumns = _root.Q<VisualElement>("favor-columns");
            _rollColumns = _root.Q<VisualElement>("roll-columns");
            _favorMe = _root.Q<VisualElement>("favor-me");
            if (_ladderTab != null) _ladderTab.clicked += () => ShowBoard(Board.Ladder);
            if (_favorTab != null) _favorTab.clicked += () => ShowBoard(Board.Favor);
            if (_rollTab != null) _rollTab.clicked += () => ShowBoard(Board.Roll);
        }

        private void OnLeaderboards()
        {
            Show(_leaderboardModal);
            ShowBoard(Board.Ladder);
        }

        /// <summary>The commission sheet's way to this reign's board. It calls once it has rolled up,
        /// which may be after the menu has gone.</summary>
        private void OpenFavorBoard()
        {
            if (this == null) return;
            Show(_leaderboardModal);
            ShowBoard(Board.Favor);
        }

        private void CloseLeaderboard()
        {
            _leaderboardRequest?.Cancel();
            Hide(_leaderboardModal);
        }

        /// <summary>Marks the tab, swaps the column heads and reads that board. A board is read every
        /// time it is shown: the Favor moves with every match, and a tab is cheaper than a stale one.</summary>
        private void ShowBoard(Board board)
        {
            _board = board;
            Toggle(_ladderTab, "mm-leaderboard__tab--on", board == Board.Ladder);
            Toggle(_favorTab, "mm-leaderboard__tab--on", board == Board.Favor);
            Toggle(_rollTab, "mm-leaderboard__tab--on", board == Board.Roll);
            Toggle(_ladderColumns, "mm-leaderboard__columns--hidden", board != Board.Ladder);
            Toggle(_favorColumns, "mm-leaderboard__columns--hidden", board != Board.Favor);
            Toggle(_rollColumns, "mm-leaderboard__columns--hidden", board != Board.Roll);
            _favorMe?.Clear();
            Toggle(_favorMe, "mm-leaderboard__me--hidden", true);
            _ = board switch
            {
                Board.Favor => RefreshFavorAsync(),
                Board.Roll => RefreshRollAsync(),
                _ => RefreshLeaderboardAsync(),
            };
        }

        private static void Toggle(VisualElement element, string className, bool on)
        {
            if (element == null) return;
            if (on) element.AddToClassList(className);
            else element.RemoveFromClassList(className);
        }

        /// <summary>Cancels the read the other board (or an earlier tap) still has in flight, so
        /// a slow answer can never draw over the board now showing.</summary>
        private CancellationToken BeginBoardRead()
        {
            _leaderboardRequest?.Cancel();
            _leaderboardRequest?.Dispose();
            _leaderboardRequest = new CancellationTokenSource();
            return _leaderboardRequest.Token;
        }

        private async System.Threading.Tasks.Task RefreshLeaderboardAsync()
        {
            CancellationToken token = BeginBoardRead();

            _leaderboardList?.Clear();
            SetLeaderboardStatus(Loc.Get("common_calling_the_keepers"), error: false, visible: true);
            if (_leaderboardUpdated != null)
                _leaderboardUpdated.text = Loc.Get("main_menu_leaderboard_updated");

            LeaderboardFetchResult result = await LeaderboardApi.FetchAsync(leaderboardUrl, token);
            if (token.IsCancellationRequested) return;

            if (!result.Ok)
            {
                SetLeaderboardStatus(
                    Loc.Get("leaderboard_error"),
                    error: true,
                    visible: true);
                return;
            }

            LeaderboardRow[] entries = result.Response.entries ?? Array.Empty<LeaderboardRow>();
            if (entries.Length == 0)
            {
                SetLeaderboardStatus(
                    Loc.Get("leaderboard_empty"),
                    error: false,
                    visible: true);
                return;
            }

            SetLeaderboardStatus("", error: false, visible: false);
            foreach (LeaderboardRow entry in entries)
                _leaderboardList?.Add(BuildLeaderboardRow(entry));

            if (_leaderboardUpdated != null
                && DateTimeOffset.TryParse(result.Response.generatedAtUtc, out DateTimeOffset generated))
                _leaderboardUpdated.text = Loc.Format("leaderboard_footer_updated",
                    $"{generated.ToLocalTime():h:mm tt}", entries.Length);
        }

        /// <summary>
        /// The King's Favor board for this reign: rank, player, score, best first in the
        /// commission's own direction (the server sorts). Your own place is pinned under the list
        /// when you are off it, since the board answers only its top hundred.
        /// </summary>
        private async System.Threading.Tasks.Task RefreshFavorAsync()
        {
            CancellationToken token = BeginBoardRead();

            _leaderboardList?.Clear();
            SetLeaderboardStatus(Loc.Get("common_calling_the_keepers"), error: false, visible: true);
            if (_leaderboardUpdated != null) _leaderboardUpdated.text = " ";

            var gateway = new HttpKingGateway(CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(KingTimeoutSeconds));
            Task<FavorStanding> standing = FetchStandingAsync(gateway, token);
            FavorBoard board;
            try
            {
                board = await gateway.FetchFavorAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                board = null;   // a tab switch or a close cancelled it; nothing went wrong
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[King] Favor board read threw ({ex.GetType().Name}: {ex.Message}).");
                board = null;
            }
            FavorStanding me = await standing;
            if (this == null || token.IsCancellationRequested || _board != Board.Favor) return;

            // No King and an unread board are one null (HttpKingGateway); the tab only shows while a
            // King is known, so here it is a failed read.
            if (board == null)
            {
                SetLeaderboardStatus(Loc.Get("king_favor_error"), error: true, visible: true);
                return;
            }

            if (_leaderboardUpdated != null)
            {
                // A commission this build has no words for is left out, never printed by id.
                string scored = KingText.Commission(board.Commission, "scored");
                _leaderboardUpdated.text = scored == null
                    ? Loc.Format("king_roll_reign", board.Reign)
                    : Loc.Format("king_favor_footer", board.Reign, scored);
            }

            if (board.Rows.Count == 0)
                SetLeaderboardStatus(Loc.Get("king_favor_empty"), error: false, visible: true);
            else
            {
                SetLeaderboardStatus("", error: false, visible: false);
                foreach (FavorBoardRow entry in board.Rows)
                    _leaderboardList?.Add(BuildBoardRow(entry.Place, KingText.SubjectName(entry.Name),
                        entry.Score.ToString(CultureInfo.InvariantCulture), "mm-leaderboard__score", record: null));
            }

            if (_favorMe != null && me?.Place is int place && place > board.Rows.Count)
            {
                // "You" is reserved by PlayerName, so no player can be shown under it.
                _favorMe.Add(BuildBoardRow(place, Loc.Get("common_you"),
                    (me.Score ?? 0).ToString(CultureInfo.InvariantCulture), "mm-leaderboard__score", record: null));
                _favorMe.RemoveFromClassList("mm-leaderboard__me--hidden");
            }
        }

        /// <summary>
        /// The Succession Roll: every reign that has ended, newest first, with its Favorite and the
        /// King who named them (Sam, 2026-11-03: past winners get a leaderboard of their own). The
        /// reign on the throne is left off, since it has no Favorite until it ends.
        /// </summary>
        private async System.Threading.Tasks.Task RefreshRollAsync()
        {
            CancellationToken token = BeginBoardRead();

            _leaderboardList?.Clear();
            SetLeaderboardStatus(Loc.Get("common_calling_the_keepers"), error: false, visible: true);
            if (_leaderboardUpdated != null) _leaderboardUpdated.text = " ";

            System.Collections.Generic.IReadOnlyList<RollEntry> roll;
            try
            {
                roll = await new HttpKingGateway(CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(KingTimeoutSeconds))
                    .FetchRollAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;   // a tab switch or a close cancelled it; nothing went wrong
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[King] Succession Roll read threw ({ex.GetType().Name}: {ex.Message}).");
                roll = null;
            }
            if (this == null || token.IsCancellationRequested || _board != Board.Roll) return;

            // The tab shows only while a King is known, and his reign is always the Roll's first line,
            // so an empty Roll is a failed read (HttpKingGateway answers one for both).
            if (roll == null || roll.Count == 0 || _king == null)
            {
                SetLeaderboardStatus(Loc.Get("king_roll_error"), error: true, visible: true);
                return;
            }

            if (_leaderboardUpdated != null) _leaderboardUpdated.text = Loc.Get("king_roll_footer");
            int shown = 0;
            foreach (RollEntry entry in roll)
            {
                if (entry.Reign >= _king.Reign) continue;
                _leaderboardList?.Add(BuildRollRow(entry));
                shown++;
            }
            if (shown == 0) SetLeaderboardStatus(Loc.Get("king_roll_empty"), error: false, visible: true);
            else SetLeaderboardStatus("", error: false, visible: false);
        }

        /// <summary>One closed reign: its number, its Favorite, and the King, by epithet and fate.</summary>
        private static VisualElement BuildRollRow(RollEntry entry)
        {
            var row = new VisualElement();
            row.AddToClassList("mm-leaderboard__row");
            row.AddToClassList("mm-leaderboard__row--roll");
            if (entry.Reign % 2 == 0) row.AddToClassList("mm-leaderboard__row--even");

            row.Add(LabelFor(Loc.Format("king_roll_reign", entry.Reign), "mm-leaderboard__reign"));
            // Null is no Favorite; "" is a Favorite with no name to show (KingText.SubjectName).
            if (entry.Favorite == null)
            {
                Label none = LabelFor(Loc.Get("king_roll_no_favorite"), "mm-leaderboard__name");
                none.AddToClassList("mm-leaderboard__name--none");
                row.Add(none);
            }
            else row.Add(NameLabelFor(KingText.SubjectName(entry.Favorite)));

            var king = new VisualElement { pickingMode = PickingMode.Ignore };
            king.AddToClassList("mm-leaderboard__king");
            king.Add(LabelFor(KingText.Epithet(entry.Epithet), "mm-leaderboard__epithet"));
            king.Add(LabelFor(KingText.Fate(entry.Fate), "mm-leaderboard__fate"));
            row.Add(king);
            return row;
        }

        /// <summary>The signed-in player's standing, or null when signed out or unread. Never throws:
        /// the board shows without it.</summary>
        private static async Task<FavorStanding> FetchStandingAsync(HttpKingGateway gateway, CancellationToken token)
        {
            try
            {
                if (!AuthGateway.IsSignedIn) return null;
                string accessToken = await AuthGateway.TokenForConnectAsync();
                return await gateway.FetchStandingAsync(accessToken, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;   // a tab switch or a close cancelled it; nothing went wrong
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[King] Favor standing read threw ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        private static VisualElement BuildLeaderboardRow(LeaderboardRow entry)
            => BuildBoardRow(entry.rank, entry.displayName ?? Loc.Get("leaderboard_unnamed_player"),
                entry.rating.ToString(), "mm-leaderboard__rating",
                Loc.Format("leaderboard_record", entry.wins, entry.losses, entry.draws));

        /// <summary>One line of either board. <paramref name="record"/> is the ladder's W-L-D, and
        /// null leaves the column out.</summary>
        private static VisualElement BuildBoardRow(int rank, string name, string number, string numberClass, string record)
        {
            var row = new VisualElement();
            row.AddToClassList("mm-leaderboard__row");
            if (rank % 2 == 0) row.AddToClassList("mm-leaderboard__row--even");
            if (rank <= 3) row.AddToClassList("mm-leaderboard__row--podium");

            row.Add(LabelFor(Loc.Format("leaderboard_rank", rank), "mm-leaderboard__rank"));
            row.Add(NameLabelFor(name));
            row.Add(LabelFor(number, numberClass));
            if (record != null) row.Add(LabelFor(record, "mm-leaderboard__record"));
            return row;
        }

        private static Label LabelFor(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            label.pickingMode = PickingMode.Ignore;
            return label;
        }

        /// <summary>
        /// A player's name on either board. It is PEER-AUTHORED, and this is the renderer's half of
        /// the obligation security.md sets for every such name, the same pair as
        /// <c>FriendsDrawerController.NameLabel</c>: <b>rich text off</b>, here, and <b>a clip in
        /// pixels</b>, in <c>.mm-leaderboard__name</c> (mainmenu.uss).
        /// </summary>
        private static Label NameLabelFor(string name)
        {
            Label label = LabelFor(name, "mm-leaderboard__name");
            label.enableRichText = false;
            label.parseEscapeSequences = false;
            return label;
        }

        private void SetLeaderboardStatus(string message, bool error, bool visible)
        {
            if (_leaderboardStatus == null) return;
            _leaderboardStatus.text = message;
            _leaderboardStatus.EnableInClassList("mm-leaderboard__status--error", error);
            _leaderboardStatus.EnableInClassList("mm-leaderboard__status--hidden", !visible);
        }
    }
}
