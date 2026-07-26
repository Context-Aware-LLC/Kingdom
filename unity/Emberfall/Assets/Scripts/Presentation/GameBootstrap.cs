using Emberfall.AI;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Scene entry point for the M1 vertical slice (ARCHITECTURE §6 LocalMatchHost path).
    /// Wires: JsonCardDatabase → MatchSetup → intent sources → MatchRunner
    ///        → BoardController for visual card tracking.
    ///
    /// Card data: Assets/CardData/M1.cards.json via Addressables address "cards/M1.cards".
    ///
    /// Phase C step 3 scene setup — add all components to one GameObject in a new scene:
    ///   • MatchRunner
    ///   • BoardAnchors   (+ ProceduralBoardSetup for automatic anchor hierarchy)
    ///   • BoardController
    ///   • InputRouter    (assign to enable hot-seat mode; leave null for AI-vs-AI)
    ///   • GameBootstrap  (assign the above in the Inspector)
    ///
    /// Hot-seat mode: P0 = human (HotSeatInput + InputRouter), P1 = PlayingAi.
    /// AI-only mode:  both players = PlayingAi (or swap to RandomAi for perf/replay testing).
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] private MatchRunner matchRunner;

        [Header("Board (Phase C step 2 — optional, null = console-only mode)")]
        [SerializeField] private BoardAnchors boardAnchors;
        [SerializeField] private BoardController boardController;

        [Header("Input (Phase C step 3 — null = AI-only mode)")]
        [SerializeField] private InputRouter inputRouter;
        [SerializeField] private ProceduralBoardSetup proceduralBoardSetup;

        [Header("VFX (Phase C step 5 — null = VFX disabled)")]
        [SerializeField] private VfxDirector vfxDirector;

        private LoadingOverlay loadingOverlay;
        private VersusOverlay versusOverlay;

        private void Awake()
        {
            // Frame-0 coverage (loading-screen-plate.md §5): the overlay exists before
            // anything renders; Start()'s synchronous boot happens behind it.
            loadingOverlay = LoadingOverlay.Create(Camera.main);
        }

        private void Start()
        {
            if (matchRunner == null)
            {
                Debug.LogError("[GameBootstrap] matchRunner is not assigned.");
                if (loadingOverlay != null) loadingOverlay.NotifyBootFailed("Scene misconfigured — matchRunner not assigned");
                return;
            }

            ICardDatabase db;
            try
            {
                db = LoadCardDatabase();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameBootstrap] card database load failed: {ex.Message}");
                if (loadingOverlay != null) loadingOverlay.NotifyBootFailed("Card database failed to load");
                return;
            }
            MatchSetup    setup = Emberfall.Data.PrebuiltDecks.EmberwardVsTidecaller();

            var p0Id = new PlayerId("P0");
            var p1Id = new PlayerId("P1");

            // ── Session pair (WS A4, a4-session-seam-design.md §3) ────────────────
            // LocalMatch.Start is the only blessed local-match door: everything below —
            // sources, presentation, input — sees PROJECTED state only. The networked boot is
            // NOT a swap of this block — it is the sibling NetworkedGameBootstrap (b3 §3.1/F4):
            // the server deals decks/ids/seat, connect+pair is async, and hero classes ride the
            // projected snapshot. This local boot stays hot-seat and must remain green (C1 entry).
            var (p0Session, p1Session) = LocalMatch.Start(db, setup, p0Id, p1Id);

            // ── Versus screen (techart/versus-screen.md §1) ────────────────────────
            // Raised the instant both hero classes are known, which locally is right here — the
            // setup is in hand and LocalMatch.Start has minted the match id the orb variant keys
            // off. The boot plate is released in the same breath and cross-fades out UNDERNEATH
            // this one (VersusOverlay sorts above it), so there is no gap to the board.
            //
            // Left medallion is the local seat, right is the opponent, per the screen's own
            // convention — NOT the board's local-at-bottom rule.
            //
            // Nameplates are PLACEHOLDER: spec §6 wants player display names and no profile/name
            // system exists yet. P1 is always PlayingAi on this path (see below), so "AI Opponent"
            // is accurate rather than a stand-in for a name we failed to fetch.
            versusOverlay = VersusOverlay.Create(Camera.main, VersusOverlay.Data.For(
                setup.P0Class, "You",
                setup.P1Class, "AI Opponent",
                p0Session.Current.Id.Value));
            if (loadingOverlay != null) loadingOverlay.NotifyMatchReady();

            // ── Intent sources ────────────────────────────────────────────────────
            // Hot-seat: P0 is human-controlled via InputRouter; P1 is AI.
            // AI-only:  both players are PlayingAi (leave inputRouter null).
            IIntentSource p0Source;
            HotSeatInput hotSeat = null;
            if (inputRouter != null)
            {
                hotSeat = new HotSeatInput(p0Id);
                // db: target-required detection; validator: courtesy CanExecute affordance
                // over the projected truth state (a4 design §7 — client-side, no authority role).
                inputRouter.Init(p0Id, hotSeat, db, new CommandProcessor(db));
                p0Source = hotSeat;
            }
            else
            {
                // PlayingAi so AI-only mode shows a real game on both sides.
                // Swap to RandomAi for replay/perf testing (never plays, drives to fatigue).
                p0Source = new PlayingAi(p0Id, db);
            }
            // Phase C step 4: use PlayingAi (plays cards + attacks). Swap back to RandomAi for replay/perf testing.
            var p1Ai = new PlayingAi(p1Id, db);

            // ── Board visuals ─────────────────────────────────────────────────────
            if (boardController != null && boardAnchors != null)
            {
                boardAnchors.SetPlayers(p0Id, p1Id);
                boardController.Initialize(db, boardAnchors, p0Id, p1Id, inputRouter, matchRunner);
                matchRunner.OnStateUpdated += boardController.OnStateUpdated;
            }

            // ── Input router subscription ─────────────────────────────────────────
            if (inputRouter != null)
            {
                // TRUTH cadence, not the playback-paced matchRunner.OnStateUpdated (a4 design
                // §7 / §11 finding 7): input gating must not lag the authority while the
                // playback queue drains.
                p0Session.OnSnapshot += inputRouter.OnStateUpdated;
                inputRouter.OnStateUpdated(p0Session.Current);

                // Board slot and hero power clickables — must run after SetPlayers() above.
                if (proceduralBoardSetup != null)
                    proceduralBoardSetup.SetupClickables(p0Id, p1Id, inputRouter);
            }

            // ── Mana trays + deck piles ───────────────────────────────────────────
            if (boardAnchors != null)
            {
                CreateManaTray(p0Id, boardAnchors);
                CreateManaTray(p1Id, boardAnchors);
                CreateDeckPile(p0Id, boardAnchors);
                CreateDeckPile(p1Id, boardAnchors);
            }

            // ── Hero plates (portrait + HP + hero-power disc) ─────────────────────
            if (boardAnchors != null)
            {
                // Same provider split as BoardController: AssetDatabase in the editor,
                // Addressables in player builds.
#if UNITY_EDITOR
                IArtProvider heroArt = new EditorArtProvider();
#else
                IArtProvider heroArt = new AddressableArtProvider();
#endif
                var p0Plate = CreateHeroPlate(p0Id, setup.P0Class, boardAnchors, db, heroArt);
                var p1Plate = CreateHeroPlate(p1Id, setup.P1Class, boardAnchors, db, heroArt);
                if (inputRouter != null && p0Plate != null)
                    inputRouter.SetHeroPlates(p0Plate, p1Plate);
            }

            // ── End-turn button (hot-seat only; ui/match-hud.md §6 item 1) ────────
            if (inputRouter != null)
            {
                var endTurnGo = new GameObject("EndTurnButton");
                endTurnGo.transform.position = new Vector3(6.3f, 0f, 0f);
                var endTurn = endTurnGo.AddComponent<EndTurnButtonView>();
                endTurn.Init(p0Id, inputRouter);
                matchRunner.OnStateUpdated += endTurn.OnStateUpdated;
            }

            // ── Mulligan confirm button (hot-seat; C-G1-2b) ───────────────────────
            // The silent auto-keep in HotSeatInput is gone; the human now tosses cards via
            // InputRouter (red X) and commits here. Visible only during the Mulligan phase.
            // Truth cadence (p0Session.OnSnapshot, like the input router at ~:120) so visibility
            // never lags the authority. HotSeatInput is only created when inputRouter != null, so
            // this button is always present wherever a human seat needs to mulligan.
            if (inputRouter != null)
            {
                var mulliganGo = new GameObject("MulliganConfirmButton");
                mulliganGo.transform.position = new Vector3(0f, -3.0f, 0f);
                var mulligan = mulliganGo.AddComponent<MulliganConfirmButtonView>();
                mulligan.Init(p0Id, inputRouter);
                p0Session.OnSnapshot += mulligan.OnStateUpdated;
                mulligan.OnStateUpdated(p0Session.Current);
            }

            // ── Settings overlay + concede (C-G1-3 / decision 23 §5) ──────────────
            // Same UI Toolkit overlay as the networked boot; here it forfeits P0. Assets load from
            // Resources (no scene wiring); IsBlockingInput gates board clicks (UITK won't block physics).
            if (inputRouter != null)
            {
                var uiGo = new GameObject("MatchSettingsUI");
                var doc  = uiGo.AddComponent<UIDocument>();
                doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
                doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("MatchSettingsScreen");
                var settings = uiGo.AddComponent<MatchSettingsController>();
                settings.Init(inputRouter.OnConcedeClicked);
                inputRouter.UiBlocksInput = () => settings.IsBlockingInput;
            }

            // ── Reject toasts (hot-seat only; design-system.md §6.4/§6.5) ─────────
            // Single hook: local-validator and authority rejects both funnel through
            // HotSeatInput.OnRejected, so one subscription covers both paths.
            if (hotSeat != null)
            {
                var toastGo = new GameObject("RejectToasts");
                toastGo.transform.position = new Vector3(5.6f, -1.5f, 0f);
                var toasts = toastGo.AddComponent<RejectToastView>();
                hotSeat.Rejected += toasts.Show;
            }

            // ── VFX ───────────────────────────────────────────────────────────────
            if (vfxDirector != null)
            {
                matchRunner.OnMatchEvent   += vfxDirector.OnMatchEvent;
                matchRunner.OnStateUpdated += vfxDirector.OnStateUpdated;
            }

            // ── Debug logging ─────────────────────────────────────────────────────
            matchRunner.OnStateUpdated += state =>
            {
                var p0s = state.Players[0];
                var p1s = state.Players[1];
                Debug.Log($"[State] Turn {state.TurnNumber} | Phase={state.Phase} | Active={state.ActivePlayer} | " +
                          $"P0 HP={p0s.Hero.CurrentHealth} mana={p0s.Mana.Available}/{p0s.Mana.Max} | " +
                          $"P1 HP={p1s.Hero.CurrentHealth} mana={p1s.Mana.Available}/{p1s.Mana.Max}");
            };

            matchRunner.OnMatchEvent += events =>
            {
                foreach (var ev in events)
                    Debug.Log($"  [Event] seq={ev.Sequence} {ev.Kind}" +
                              (ev.Source.HasValue ? $" src={ev.Source.Value}" : "") +
                              (ev.Target.HasValue ? $" tgt={ev.Target.Value}" : "") +
                              (ev.Amount.HasValue ? $" amt={ev.Amount.Value}" : ""));
            };

            matchRunner.OnMatchEnded += outcome =>
            {
                Debug.Log($"[Match] OVER — {outcome}");
                MatchEndBanner.Show(outcome, localSeat: 0); // hot-seat: the local view is always seat 0
                                                            // (networked uses localSeat: net.Seat — NetworkedGameBootstrap)
            };

            // ── Loading overlay fade: first released snapshot = the board is drawn. Must be
            // subscribed BEFORE StartMatch — the instant initial entry fires synchronously
            // inside it. The overlay holds the fade until its reveal choreography finishes.
            if (loadingOverlay != null)
                matchRunner.OnStateUpdated += _ => loadingOverlay.NotifyMatchReady();
            // Same signal for the versus screen, which additionally holds its own minimum dwell —
            // the board is ready long before a player has read who they are fighting.
            if (versusOverlay != null)
                matchRunner.OnStateUpdated += _ => versusOverlay.NotifyMatchReady();

            matchRunner.StartMatch(p0Session, p0Source, p1Session, p1Ai);
        }

        private HeroPlateView CreateHeroPlate(PlayerId playerId, Core.Contracts.CardClass heroClass,
                                              BoardAnchors anchors, ICardDatabase db, IArtProvider artProvider)
        {
            Transform heroAnchor = anchors.HeroAnchor(playerId);
            if (heroAnchor == null) return null;

            var go   = new GameObject($"HeroPlate_{playerId}");
            var view = go.AddComponent<HeroPlateView>();
            view.Init(playerId, heroAnchor, artProvider, heroClass);
            matchRunner.OnStateUpdated += view.OnStateUpdated;

            CreateHeroSockets(playerId, anchors, db, artProvider);
            return view;
        }

        /// <summary>The forged hero-power and weapon rings inboard of the portrait. Separate
        /// from the plate because the weapon half comes and goes with the equipped weapon.</summary>
        private void CreateHeroSockets(PlayerId playerId, BoardAnchors anchors,
                                       ICardDatabase db, IArtProvider artProvider)
        {
            Transform power  = anchors.HeroPowerAnchor(playerId);
            Transform weapon = anchors.WeaponAnchor(playerId);
            if (power == null && weapon == null) return;

            var go   = new GameObject($"HeroSockets_{playerId}");
            var view = go.AddComponent<HeroSocketsView>();
            view.Init(playerId, power, weapon, db, artProvider);
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private void CreateManaTray(PlayerId playerId, BoardAnchors anchors)
        {
            Transform anchor = anchors.ManaTrayAnchor(playerId);
            if (anchor == null) return;

            var go   = new GameObject($"ManaTray_{playerId}");
            var view = go.AddComponent<ManaTrayView>();
            view.Init(playerId, anchor);
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private void CreateDeckPile(PlayerId playerId, BoardAnchors anchors)
        {
            Transform anchor = anchors.AnchorFor(playerId, Core.Contracts.Zone.Deck, 0);
            if (anchor == null) return;

            var go   = new GameObject($"DeckPile_{playerId}");
            var view = go.AddComponent<DeckPileView>();
            view.Init(playerId, anchor);
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private static ICardDatabase LoadCardDatabase()
        {
            // CardData Addressables group (M2 D1 — card data no longer lives in Resources/).
            // Synchronous on purpose: this is the boot path, and Start() has no async context.
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<TextAsset>("cards/M1.cards");
            TextAsset asset = handle.WaitForCompletion();
            if (asset == null)
                throw new System.Exception(
                    "cards/M1.cards not found in Addressables — run Emberfall/Addressables/Setup Groups + Migrate Card Data");
            return new JsonCardDatabase(asset.text);
        }
    }
}
