using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Auth;
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
    /// destinations still log their intent. Nothing but the shard plate outlives a departure — every screen rebinds from
    /// <c>ShopRuntime</c>/<c>AuthGateway</c> on arrival rather than being handed state.</para>
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
        private VisualElement _leaderboardModal;
        private ScrollView _leaderboardList;
        private Label _leaderboardStatus;
        private Label _leaderboardUpdated;
        private CancellationTokenSource _leaderboardRequest;

        private Label _shardBalance;
        private VisualElement _shardWallet;
        private VisualElement _vaultArt;
        private Label _vaultPackCount;
        private IPackArtProvider _vaultArtProvider;
        private Rect _lastSafeArea;
        private int _lastSafeAreaScreenWidth = -1;
        private int _lastSafeAreaScreenHeight = -1;

        private const float ShardSafeAreaMarginPercent = 2f;
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

            BindShardBalance();
            RefreshShardBalance();
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
                    await CollectionGateway.SyncAsync(accessToken);
                    await RosterGateway.SyncAsync(accessToken);

                    // The bind may have switched accounts underneath the field captured in OnEnable.
                    _account = PlayerAccount.Local;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MainMenu] could not restore this session's player data " +
                                 $"({ex.GetType().Name}: {ex.Message}); continuing with whatever " +
                                 "this device already had.");
            }

            await BeginFirstRun();
            FinishBootstrap();
        }

        /// <summary>
        /// Safe-area changes have no callback: rotation and Android system-bar changes can move the
        /// usable top/right edge while the scene stays enabled. Poll only the three values that can
        /// affect this read-only plate and write layout when one actually changes.
        /// </summary>
        private void Update()
        {
            RefreshShardSafeArea();
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
        private async Task BeginFirstRun()
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
                ScreenNav.Go(ScreenNav.AccountCreate);
                return;
            }

            if (!AuthGateway.IsSignedIn) return;

            // The ledger balance owns this route. The local marker only decides whether the vault
            // wears its congratulations face, so quitting after one open cannot strand the rest of
            // the starter packs behind a marker that has already been written.
            // Refreshed, for the reason spelled out in ResumeThenFinishBootstrap — and self-sufficient
            // rather than reusing that method's token, because the gift must not depend on the order
            // two boot steps happen to run in. A 401 here reads as "no starter packs" and strands the
            // gift silently; TokenForConnectAsync is a no-op when the token is already fresh.
            var snapshot = await PackGateway.ReadShelfAsync(
                await AuthGateway.TokenForConnectAsync());
            RenderVaultEntry(snapshot);
            if (snapshot.PackCount(StarterPackId) <= 0) return;

            PackVaultController.ReturnScene = ScreenNav.MainMenu;
            PackVaultController.Pending = null;
            PackVaultController.Mode = FirstRunMarkers.Has(FirstRunMarkers.Gift)
                ? PackVaultMode.Vault
                : PackVaultMode.Gift;
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

        // ── Shard balance ──────────────────────────────────────────────────────────────

        private void BindShardBalance()
        {
            _shardWallet = _root.Q<VisualElement>("shard-wallet");
            _shardBalance = _root.Q<Label>("shard-balance");
            if (_shardBalance != null) _shardBalance.text = WalletDisplay.ShardsUnavailable;
            _lastSafeAreaScreenWidth = -1;
            RefreshShardSafeArea();
        }

        /// <summary>
        /// Read the wallet the plate reports. This used to arrive as a side effect of building the
        /// store's in-scene panel; with the store on its own scene (2026-08-20) the menu owns the
        /// read, and re-runs it on every <c>OnEnable</c> — which includes the return from a
        /// purchase, so an opened pack's shards land on the plate with no cross-scene wiring at all.
        ///
        /// <para>Failure is not silent-but-zero: an unreachable account server leaves the plate on
        /// its em dash, the same as having no gateway bound.</para>
        /// </summary>
        private async void RefreshShardBalance()
        {
            if (ShopRuntime.Gateway == null)
            {
                RenderShardBalance(null);
                return;
            }

            try
            {
                var wallet = new ShopFlow(ShopRuntime.Gateway);
                await wallet.RefreshAsync();
                RenderShardBalance(wallet.Snapshot?.Wallet.Shards);
            }
            catch (Exception ex)
            {
                RenderShardBalance(null);
                Debug.LogWarning($"[MainMenu] shard balance unavailable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Adopt the gateway's ledger projection directly. A null snapshot state stays an em dash;
        /// an authoritative missing ledger row arrives from the server as 0 and therefore renders 0.
        /// No shard count is stored by this controller.
        /// </summary>
        private void RenderShardBalance(long? shards)
        {
            if (_shardBalance == null) return;
            _shardBalance.text = shards.HasValue
                ? WalletDisplay.Shards(shards.Value)
                : WalletDisplay.ShardsUnavailable;
        }

        private void RefreshShardSafeArea()
        {
            if (_shardWallet == null) return;

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
            _shardWallet.style.right = Length.Percent(
                Mathf.Min(100f, rightInsetPercent + ShardSafeAreaMarginPercent));
            _shardWallet.style.top = Length.Percent(
                Mathf.Min(100f, topInsetPercent + ShardSafeAreaMarginPercent));
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
            MatchDeckSelection.BeginChoosing();
            CharacterRosterController.ReturnScene = ScreenNav.MainMenu;
            ScreenNav.Go(ScreenNav.CharacterRoster);
        }

        /// <summary>
        /// Collection is the one home for the card wall and deck editing. The deck drawer starts
        /// closed; deck management remains one explicit doorway inside it.
        /// </summary>
        private void OnCollection()
        {
            MatchDeckSelection.Cancel();
            CollectionController.ReturnScene = ScreenNav.MainMenu;
            CollectionController.PendingDeckId = "";
            ScreenNav.Go(ScreenNav.Collection);
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
        /// live unopened-pack total, including zero; the vault already owns an honest empty state.</summary>
        private void BindVaultEntry()
        {
            _vaultArt = _root.Q<VisualElement>(className: "mm-vault-art");
            _vaultPackCount = _root.Q<Label>("vault-pack-count");
            _vaultArtProvider = ShopRuntime.PackArt ?? new PackArtProvider();
            if (_vaultPackCount != null) _vaultPackCount.text = "× 0";
        }

        private void RenderVaultEntry(Emberfall.Net.Client.Collection.CollectionSnapshot snapshot)
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

            _vaultPackCount.text = total > 99 ? "× 99+" : $"× {total}";
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
