using System;
using System.Collections.Generic;
using System.Threading;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
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
    /// <para>Each destination has a named handler so a scene-loader drops straight in. Store, Collection
    /// and the roster are scenes of their own; the remaining unfinished destinations still log their
    /// intent. Nothing but the shard plate outlives a departure — every screen rebinds from
    /// <c>ShopRuntime</c>/<c>AuthGateway</c> on arrival rather than being handed state.</para>
    ///
    /// <para><b>This screen is also where identity begins.</b> A device that has never been named is
    /// asked once here — <b>once</b> being the whole of it: a display name is permanent (NOR-287), so the
    /// nameplate top-left only reports it and the client carries no rename flow at all.
    /// Character creation lives on the roster screen, never on this menu.</para>
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

        private Label _identityName;
        private VisualElement _identityShowcase;
        private Label _identityDeck;

        private ICardDatabase _db;
        private IArtProvider _art;
        private IUiSpriteProvider _uiSprites;

        private Label _shardBalance;
        private Rect _lastSafeArea;
        private int _lastSafeAreaScreenWidth = -1;
        private int _lastSafeAreaScreenHeight = -1;

        private const float ShardSafeAreaMarginPercent = 2f;


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
            Wire("btn-leaderboard-close", CloseLeaderboard);
            BindLeaderboard();

            BindBuildStamp();

            BindShardBalance();
            RefreshShardBalance();

            BindIdentity();
            MountSettingsOverlay();
            _signIn = new SignInGateController(_root, AuthGateway.Provider);

            // A corrupt collection file must not brick the front door. The stores throw rather than
            // reporting an empty collection (so the next save cannot overwrite something
            // recoverable), which means the failure surfaces HERE, on a screen whose other four
            // buttons still work.
            try
            {
                _account = PlayerAccount.Local;
                Debug.Log($"[MainMenu] collection scope → {_account.ScopeDirectory}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not open this device's collection: {ex.Message}");
                SetIdentity("Collection unreadable");
                FinishBootstrap();
                return;
            }

            try { _db = CardDatabaseLoader.Load(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MainMenu] card database unavailable — showcase art will not load: {ex.Message}");
            }
            _art = new AddressableArtProvider();
            _uiSprites = CardBackCatalog.NewProvider();

            RefreshIdentity();
            BeginFirstRun();
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
        /// First-run prompts, in the order Sam ruled on 2026-08-08: register, then name yourself.
        ///
        /// <para><b>Registration comes first and cannot be skipped.</b> The two prompts are chained
        /// rather than shown together — the sign-in gate calls back once a session exists, and only
        /// then does the name prompt open. Opening both would stack two modals on a player who has not
        /// seen the game yet, and the name is the smaller of the two questions.</para>
        ///
        /// <para><b>The menu is revealed underneath either way.</b> Sam asked for the registration
        /// prompt as an overlay ON the main menu, not instead of it, and the gate's own scrim is what
        /// stops the buttons behind it being reachable.</para>
        /// </summary>
        private void BeginFirstRun()
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
            if (needsAccount || needsName) Go("AccountCreate");
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
        }

        // System.Action explicitly: Emberfall.Core.Contracts has an Action type of its own (a card
        // effect), and both namespaces are in scope here.
        private void Wire(string name, System.Action onClick)
        {
            var button = _root.Q<Button>(name);
            if (button == null) { Debug.LogWarning($"[MainMenu] Button '{name}' not found in UXML."); return; }
            button.RegisterCallback<ClickEvent>(_ => onClick());
        }

        // ── Identity plate ──────────────────────────────────────────────────────────────

        /// <summary>The plate reports; it does not act (NOR-287). Nothing is wired to it.</summary>
        private void BindIdentity()
        {
            _identityName = _root.Q<Label>("identity-name");
            _identityShowcase = _root.Q<VisualElement>("identity-showcase");
            _identityDeck = _root.Q<Label>("identity-deck");
            // Hidden until a character+deck is resolved; shown in RefreshIdentity when one exists.
            SetVisible(_identityShowcase, false);
        }

        private void SetIdentity(string name)
        {
            if (_identityName != null) _identityName.text = name;
        }

        // ── Build stamp ────────────────────────────────────────────────────────────────

        /// <summary>
        /// #195 — replace the hand-written "M1 · vertical slice" with the build a tester is
        /// actually holding. The label's UXML text is now only what shows if this never runs.
        /// </summary>
        private void BindBuildStamp()
        {
            var label = _root.Q<Label>("version-label");
            if (label != null) label.text = BuildStamp.Display;
        }

        // ── Shard balance ──────────────────────────────────────────────────────────────

        private void BindShardBalance()
        {
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
            if (_shardBalance == null) return;

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
            _shardBalance.style.right = Length.Percent(
                Mathf.Min(100f, rightInsetPercent + ShardSafeAreaMarginPercent));
            _shardBalance.style.top = Length.Percent(
                Mathf.Min(100f, topInsetPercent + ShardSafeAreaMarginPercent));
        }

        /// <summary>
        /// Redraw the plate from disk. The name comes off the profile; the character and deck sub-line
        /// resolves from last-played, falling back to the first roster character and its default deck
        /// (NOR-289, Sam 2026-08-23).
        /// </summary>
        private void RefreshIdentity()
        {
            if (_account == null) return;

            // "Unnamed player" rather than an invented default: a saved default is indistinguishable
            // from a chosen name, and the prompt would never fire again (PlayerAccount.DisplayNameOr).
            SetIdentity(_account.DisplayNameOr("Unnamed player"));

            IReadOnlyList<Character> roster = _account.Roster();
            if (roster.Count == 0)
            {
                SetDeckLine("No characters yet — open Collection");
                SetVisible(_identityShowcase, false);
                return;
            }

            IReadOnlyList<DeckList> decks = _account.Decks();

            // Primary: last character and deck actually submitted into a match.
            Character character = CharacterQuery.ById(roster, _account.LastPlayedCharacterId);
            DeckList deck = FindDeckById(decks, _account.LastPlayedDeckId);

            // Fallback: first roster character and its default deck (or any deck it owns).
            if (character == null || deck == null)
            {
                character = roster[0];
                deck = CharacterQuery.DefaultDeckOf(character, decks)
                    ?? FindFirstDeckFor(character.Id, decks);
            }

            SetDeckLine(deck != null
                ? $"{character.Name} · {deck.Name}"
                : character.Name);

            if (deck != null)
            {
                SetVisible(_identityShowcase, true);
                LoadShowcaseArt(deck);
            }
            else
            {
                SetVisible(_identityShowcase, false);
            }
        }

        private void SetDeckLine(string text)
        {
            if (_identityDeck != null) _identityDeck.text = text;
        }

        /// <summary>
        /// Paint the deck's showcase card art onto the identity plate. Applies the card back
        /// immediately as a synchronous fallback, then overwrites with the card art once the
        /// Addressables load completes. Pattern mirrors CharacterRosterController.ShowcaseArt.
        /// </summary>
        private void LoadShowcaseArt(DeckList deck)
        {
            if (_identityShowcase == null) return;

            if (_uiSprites != null)
            {
                Sprite back = CardBackCatalog.SpriteFor(deck.CardBackId, _uiSprites);
                if (back != null)
                    _identityShowcase.style.backgroundImage = new StyleBackground(back);
            }

            if (_db == null || _art == null || deck.ShowcaseCardId <= 0) return;
            if (!_db.TryGet(deck.ShowcaseCardId, out CardDefinition showcase)) return;
            if (string.IsNullOrEmpty(showcase.ArtId)) return;

            _art.LoadArt(showcase.ArtId, sprite =>
            {
                if (sprite != null && _identityShowcase != null)
                    _identityShowcase.style.backgroundImage = new StyleBackground(sprite);
            });
        }

        private static DeckList FindDeckById(IReadOnlyList<DeckList> decks, string id)
        {
            if (string.IsNullOrEmpty(id) || decks == null) return null;
            foreach (DeckList d in decks)
                if (d?.Id == id) return d;
            return null;
        }

        private static DeckList FindFirstDeckFor(string characterId, IReadOnlyList<DeckList> decks)
        {
            if (string.IsNullOrEmpty(characterId) || decks == null) return null;
            foreach (DeckList d in decks)
                if (d?.CharacterId == characterId) return d;
            return null;
        }

        /// <summary>Show or hide by display, not visibility: a hidden element must not keep spending
        /// its layout box. Lived in the name-modal region until that moved to AccountCreate
        /// (2026-08-25); four callers here still need it.</summary>
        private static void SetVisible(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
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
            var uiGo = new GameObject("MatchSettingsUI");
            var doc  = uiGo.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("MatchSettingsScreen");
            var settings = uiGo.AddComponent<MatchSettingsController>();
            settings.Init(onConcede: null, showConcede: false);
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
        /// <para>The existing character roster is the deck-selection step: it already shows each
        /// character's default deck and queue eligibility. A queue-ready choice then loads the board,
        /// whose network bootstrap submits that exact deck before pairing.</para>
        /// </summary>
        private void OnFindGame()
        {
            MatchDeckSelection.BeginChoosing();
            CharacterRosterController.ReturnScene = "MainMenu";
            Go("CharacterRoster");
        }

        /// <summary>
        /// Load a scene by name, reporting the one failure mode that is otherwise silent.
        ///
        /// <para><b>A scene missing from the build list does not throw a useful error</b> — in a player
        /// it is simply not there, and in the Editor <c>LoadScene</c> needs it in
        /// <c>EditorBuildSettings</c> or nothing happens. Both present as a dead button. The build list
        /// is kept in sync by <c>Emberfall/Build/Sync Scene List</c>; this check is what tells you it
        /// wasn't.</para>
        /// </summary>
        private static void Go(string sceneName)
        {
            // CanStreamedLevelBeLoaded, not SceneUtility.GetBuildIndexByScenePath — the latter wants a
            // full asset path and answers -1 for a bare scene name, which would make this guard reject
            // every load it was written to protect.
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[MainMenu] Scene '{sceneName}' is not in the build list, so this button " +
                               "cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            Debug.Log($"[MainMenu] → {sceneName}");
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }

        /// <summary>
        /// Collection is the one home for the card wall and deck editing. The deck drawer starts
        /// closed; character/deck management remains one explicit doorway inside it.
        /// </summary>
        private void OnCollection()
        {
            MatchDeckSelection.Cancel();
            CollectionController.ReturnScene = "MainMenu";
            CollectionController.PendingCharacterId = "";
            CollectionController.PendingDeckId = "";
            Go("Collection");
        }

        /// <summary>Store → <c>Shop</c>. It rendered as an overlay on this screen until
        /// 2026-08-20; it is a destination like Collection and the roster, and its plate was
        /// already full-screen and opaque — the overlay was hosting, not design.</summary>
        private void OnStore()
        {
            ShopScreenController.ReturnScene = "MainMenu";
            Go("Shop");
        }

        private void BindLeaderboard()
        {
            _leaderboardModal = _root.Q<VisualElement>("leaderboard-modal");
            _leaderboardList = _root.Q<ScrollView>("leaderboard-list");
            _leaderboardStatus = _root.Q<Label>("leaderboard-status");
            _leaderboardUpdated = _root.Q<Label>("leaderboard-updated");
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
