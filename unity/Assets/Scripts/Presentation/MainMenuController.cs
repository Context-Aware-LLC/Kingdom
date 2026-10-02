using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Auth;
using Emberfall.Net.Client.Collection;
using Emberfall.Net.Client.Quests;
using Emberfall.Net.Client.Shop;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
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

        private Label _goldBalance;
        private Label _gemBalance;
        private Label _shardBalance;
        private VisualElement _topRail;
        private VisualElement _questList;
        private Label _questStatus;
        private HttpQuestGateway _questGateway;
        private VisualElement _vaultArt;
        private Label _vaultPackCount;
        private IPackArtProvider _vaultArtProvider;
        private Rect _lastSafeArea;
        private int _lastSafeAreaScreenWidth = -1;
        private int _lastSafeAreaScreenHeight = -1;
        private bool _collectionTransitioning;

        /// <summary>True once <see cref="BeginFirstRun"/> has sent this launch to another scene.</summary>
        private bool _leavingForFirstRun;

        private const float TopRailSafeAreaMarginPercent = 2f;
        private static readonly string StarterPackId = OnboardingGrant.StarterPack().Id;


        private SignInGateController _signIn;

        /// <summary>Frame-0 coverage, the same rig GameBootstrap/NetworkedGameBootstrap use
        /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/loading-screen-plate.md §5): the overlay exists before anything renders. The menu's
        /// own content starts hidden (mm-root--hidden in the UXML) so there is no frame where
        /// the unbound menu shows through before <see cref="RevealMenu"/> runs.</summary>
        private void Awake()
        {
            _loading = LoadingOverlay.Create(Camera.main);
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

            Wire("btn-find-game", OnFindGame);
            Wire("btn-collection", OnCollection);
            Wire("btn-store", OnStore);
            Wire("btn-leaderboards", OnLeaderboards);
            Wire("btn-card-vault", OnCardVault);
            Wire("btn-leaderboard-close", CloseLeaderboard);
            BindLeaderboard();

            BindBuildStamp();

            BindWalletBalances();
            BindQuestPanel();
            BindVaultEntry();

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
        /// wait — and nothing below is skipped on a failure, because a bad network must not stop the
        /// menu appearing.</para>
        /// </summary>
        private async void ResumeThenFinishBootstrap()
        {
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

                    // The bind may have switched accounts underneath the field captured in OnEnable.
                    _account = PlayerAccount.Local;
                }
            }
            catch (Exception ex)
            {
                RenderQuestFailure("Quests unavailable");
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
        /// First-run routing: register, name yourself, then surface any unopened starter gift.
        ///
        /// <para><b>Registration comes first and cannot be skipped.</b> The two prompts are chained
        /// rather than shown together — the sign-in gate calls back once a session exists, and only
        /// then does the name prompt open. Opening both would stack two modals on a player who has not
        /// seen the game yet, and the name is the smaller of the two questions.</para>
        ///
        /// <para><b>The starter balance routes; the marker does not.</b> Every launch that still holds
        /// starter packs returns to the vault. <see cref="FirstRunMarkers.Gift"/> only suppresses the
        /// one-time congratulations treatment after the player has actually opened their first pack.</para>
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
            // the player on itself — to CharacterRoster on an empty roster, back here otherwise.
            bool needsAccount = _signIn != null && _signIn.IsRequired;
            bool needsName = _account != null && _account.NeedsDisplayName;
            if (needsAccount || needsName)
            {
                _leavingForFirstRun = true;
                ScreenNav.Go(ScreenNav.AccountCreate);
                return;
            }

            if (!AuthGateway.IsSignedIn) return;

            // The ledger balance owns this route. The local marker only decides whether the vault
            // wears its congratulations face, so quitting after one open cannot strand the rest of
            // the starter packs behind a marker that has already been written.
            // Reuse a successful startup snapshot so the menu does not read /collection twice. Stay
            // self-sufficient when startup had no authoritative answer: a transient failure there
            // must not strand the gift, and TokenForConnectAsync is a no-op when the token is fresh.
            var snapshot = collection != null && collection.HasAuthoritativePacks
                ? collection
                : await PackGateway.ReadShelfAsync(await AuthGateway.TokenForConnectAsync());
            RenderWalletBalances(snapshot);
            RenderVaultEntry(snapshot);
            if (snapshot.PackCount(StarterPackId) <= 0) return;

            PackVaultController.ReturnScene = ScreenNav.MainMenu;
            PackVaultController.Pending = null;
            PackVaultController.Mode = FirstRunMarkers.Has(FirstRunMarkers.Gift)
                ? PackVaultMode.Vault
                : PackVaultMode.Gift;
            _leavingForFirstRun = true;
            ScreenNav.Go(ScreenNav.PackVault);
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
            _root.RemoveFromClassList("mm-root--hidden");
            // Let the revealed tree paint once at its entrance pose before transitioning the
            // command assembly into place. Removing both classes in the same layout pass snaps
            // directly to the final state and makes the transition invisible.
            _root.schedule.Execute(() => _root.RemoveFromClassList("mm-root--entering"))
                .ExecuteLater(16);

            // First look (#403). FinishBootstrap reveals the menu even on a launch BeginFirstRun
            // has already sent elsewhere, and a lesson shown under a pending scene change is one
            // the player glimpses and never reads.
            if (!_leavingForFirstRun) new FirstRunTidbit(_root).Show(TidbitTrigger.MainMenuArrival);
        }

        private void Wire(string name, System.Action onClick)
        {
            var button = _root.Q<Button>(name);
            if (button == null) { Debug.LogWarning($"[MainMenu] Button '{name}' not found in UXML."); return; }
            button.RegisterCallback<ClickEvent>(_ => onClick());
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
        private void RenderWalletBalances(CollectionSnapshot snapshot)
        {
            string fallback = WalletDisplay.IconAmount(0);
            bool available = snapshot != null && snapshot.HasAuthoritativeWallet;
            string gold = available
                ? WalletDisplay.IconAmount(snapshot.Wallet.Gold)
                : fallback;
            string gems = available
                ? WalletDisplay.IconAmount(snapshot.Wallet.Gems)
                : fallback;
            string shards = available
                ? WalletDisplay.IconAmount(snapshot.Wallet.Shards)
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
                _questStatus.text = "Loading quests…";
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
                    ? "Sign in to view quests"
                    : "Quests unavailable";
                RenderQuestFailure(message);
                return;
            }

            if (snapshot.Quests.Count == 0)
            {
                RenderQuestFailure("No current quests");
                return;
            }

            _questStatus.AddToClassList("mm-quest-status--hidden");
            for (int index = 0; index < snapshot.Quests.Count; index++)
                _questList.Add(BuildQuestRow(snapshot.Quests[index], index));
        }

        private void RenderQuestFailure(string message)
        {
            if (_questList != null) _questList.Clear();
            if (_questStatus == null) return;
            _questStatus.text = message;
            _questStatus.RemoveFromClassList("mm-quest-status--hidden");
        }

        private static VisualElement BuildQuestRow(QuestEntry quest, int index)
        {
            var row = new VisualElement();
            row.AddToClassList("mm-quest-row");
            if (quest.Complete) row.AddToClassList("mm-quest-row--complete");

            var heading = new VisualElement();
            heading.AddToClassList("mm-quest-row__heading");

            var title = new Label(quest.Title);
            title.AddToClassList("mm-quest-title");
            heading.Add(title);

            var age = new Label(index switch
            {
                0 => "TODAY",
                1 => "YESTERDAY",
                _ => "2 DAYS AGO",
            });
            age.AddToClassList("mm-quest-age");
            heading.Add(age);
            row.Add(heading);

            var detail = new Label(quest.Complete
                ? "COMPLETE"
                : $"{quest.Progress} / {quest.Goal}");
            detail.AddToClassList("mm-quest-detail");

            var reward = new VisualElement();
            reward.AddToClassList("mm-quest-reward");
            var rewardIcon = new VisualElement();
            rewardIcon.AddToClassList("mm-quest-reward__icon");
            reward.Add(rewardIcon);
            var rewardAmount = new Label($"+{quest.RewardGold}");
            rewardAmount.AddToClassList("mm-quest-reward__amount");
            reward.Add(rewardAmount);

            var summary = new VisualElement();
            summary.AddToClassList("mm-quest-summary");
            summary.Add(detail);
            summary.Add(reward);
            row.Add(summary);

            var track = new VisualElement();
            track.AddToClassList("mm-quest-progress");
            var fill = new VisualElement();
            fill.AddToClassList("mm-quest-progress__fill");
            fill.style.width = Length.Percent(
                Mathf.Clamp01((float)quest.Progress / quest.Goal) * 100f);
            track.Add(fill);
            row.Add(track);
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
            PackVaultController.Mode = PackVaultMode.Vault;
            ScreenNav.Go(ScreenNav.PackVault);
        }

        /// <summary>The vault sigil is a permanent first-class destination. Its badge reports the
        /// live unopened-pack total only when positive; the vault stays accessible when empty.</summary>
        private void BindVaultEntry()
        {
            _vaultArt = _root.Q<VisualElement>(className: "mm-vault-art");
            _vaultPackCount = _root.Q<Label>("vault-pack-count");
            _vaultArtProvider = ShopRuntime.PackArt ?? new PackArtProvider();
            if (_vaultPackCount != null)
            {
                _vaultPackCount.text = "";
                _vaultPackCount.style.display = DisplayStyle.None;
            }
        }

        private void RenderVaultEntry(Emberfall.Net.Client.Collection.CollectionSnapshot snapshot)
        {
            if (_vaultPackCount == null) return;

            long total = 0;
            if (snapshot != null)
            {
                foreach (long count in snapshot.Packs.Values)
                {
                    if (count > 0) total = Math.Min(100, total + Math.Min(100, count));
                }
            }

            _vaultPackCount.style.display = total > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _vaultPackCount.text = total > 99 ? "× 99+" : total > 0 ? $"× {total}" : "";
            if (total > 0) RenderVaultArt(snapshot);
        }

        /// <summary>Use one pack the player actually owns for the socket miniature. The fallback
        /// image remains in USS if that product has no declared art or the shader cannot render;
        /// a missing optional treatment must never turn the navigation target blank.</summary>
        private void RenderVaultArt(
            Emberfall.Net.Client.Collection.CollectionSnapshot snapshot)
        {
            if (_vaultArt == null || _vaultArtProvider == null) return;

            string packId = null;
            foreach (var line in snapshot.Packs)
            {
                if (line.Value <= 0) continue;
                if (packId == null || string.CompareOrdinal(line.Key, packId) < 0)
                    packId = line.Key;
            }
            if (packId == null) return;

            _vaultArtProvider.LoadPackArt(packId, pieces =>
            {
                if (_vaultArt == null || pieces == null || pieces.Count == 0) return;
                PackWrapperBaker.ApplyTo(_vaultArt, pieces[0].Sprite);
            });
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
        }

        private void OnLeaderboards()
        {
            Show(_leaderboardModal);
            _ = RefreshLeaderboardAsync();
        }

        private void CloseLeaderboard()
        {
            _leaderboardRequest?.Cancel();
            Hide(_leaderboardModal);
        }

        private async System.Threading.Tasks.Task RefreshLeaderboardAsync()
        {
            _leaderboardRequest?.Cancel();
            _leaderboardRequest?.Dispose();
            _leaderboardRequest = new CancellationTokenSource();
            CancellationToken token = _leaderboardRequest.Token;

            _leaderboardList?.Clear();
            SetLeaderboardStatus("Calling the keepers…", error: false, visible: true);
            if (_leaderboardUpdated != null)
                _leaderboardUpdated.text = "Ratings update after every eligible match.";

            LeaderboardFetchResult result = await LeaderboardApi.FetchAsync(leaderboardUrl, token);
            if (token.IsCancellationRequested) return;

            if (!result.Ok)
            {
                SetLeaderboardStatus(
                    "The ranked ledger is unavailable right now. Your matches are still safe.",
                    error: true,
                    visible: true);
                return;
            }

            LeaderboardRow[] entries = result.Response.entries ?? Array.Empty<LeaderboardRow>();
            if (entries.Length == 0)
            {
                SetLeaderboardStatus(
                    "No ranked duels have been recorded yet. The first crown is waiting.",
                    error: false,
                    visible: true);
                return;
            }

            SetLeaderboardStatus("", error: false, visible: false);
            foreach (LeaderboardRow entry in entries)
                _leaderboardList?.Add(BuildLeaderboardRow(entry));

            if (_leaderboardUpdated != null
                && DateTimeOffset.TryParse(result.Response.generatedAtUtc, out DateTimeOffset generated))
                _leaderboardUpdated.text = $"Updated {generated.ToLocalTime():h:mm tt} · Elo · top {entries.Length}";
        }

        private static VisualElement BuildLeaderboardRow(LeaderboardRow entry)
        {
            var row = new VisualElement();
            row.AddToClassList("mm-leaderboard__row");
            if (entry.rank % 2 == 0) row.AddToClassList("mm-leaderboard__row--even");
            if (entry.rank <= 3) row.AddToClassList("mm-leaderboard__row--podium");

            row.Add(LabelFor($"#{entry.rank}", "mm-leaderboard__rank"));
            row.Add(LabelFor(entry.displayName ?? "Unnamed Player", "mm-leaderboard__name"));
            row.Add(LabelFor(entry.rating.ToString(), "mm-leaderboard__rating"));
            row.Add(LabelFor($"{entry.wins} — {entry.losses} — {entry.draws}", "mm-leaderboard__record"));
            return row;
        }

        private static Label LabelFor(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            label.pickingMode = PickingMode.Ignore;
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
