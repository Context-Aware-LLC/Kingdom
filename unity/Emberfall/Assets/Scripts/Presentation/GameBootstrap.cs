using Emberfall.AI;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Scene entry point for the M1 vertical slice (ARCHITECTURE §6 LocalMatchHost path).
    /// Wires: JsonCardDatabase → MatchSetup → intent sources → MatchRunner
    ///        → BoardController for visual card tracking.
    ///
    /// Card data: Assets/Resources/cards/M1.cards.json (loaded as TextAsset).
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

        private void Start()
        {
            if (matchRunner == null)
            {
                Debug.LogError("[GameBootstrap] matchRunner is not assigned.");
                return;
            }

            ICardDatabase db    = LoadCardDatabase();
            MatchSetup    setup = PrebuiltDecks.EmberwardVsTidecaller();

            var p0Id = new PlayerId("P0");
            var p1Id = new PlayerId("P1");

            // ── Intent sources ────────────────────────────────────────────────────
            // Hot-seat: P0 is human-controlled via InputRouter; P1 is AI.
            // AI-only:  both players are RandomAi (leave inputRouter null).
            IIntentSource p0Source;
            if (inputRouter != null)
            {
                var hotSeat = new HotSeatInput(p0Id);
                inputRouter.Init(p0Id, hotSeat);
                p0Source = hotSeat;
            }
            else
            {
                p0Source = new RandomAi(p0Id);
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
                matchRunner.OnStateUpdated += inputRouter.OnStateUpdated;

                // Board slot and hero power clickables — must run after SetPlayers() above.
                if (proceduralBoardSetup != null)
                    proceduralBoardSetup.SetupClickables(p0Id, p1Id, inputRouter);
            }

            // ── Mana trays ────────────────────────────────────────────────────────
            if (boardAnchors != null)
            {
                CreateManaTray(p0Id, 0, boardAnchors);
                CreateManaTray(p1Id, 1, boardAnchors);
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
                Debug.Log($"[Match] OVER — {outcome}");

            matchRunner.StartMatch(db, setup, p0Source, p1Ai);
        }

        private void CreateManaTray(PlayerId playerId, int seatIndex, BoardAnchors anchors)
        {
            Transform anchor = anchors.ManaTrayAnchor(playerId);
            if (anchor == null) return;

            var go   = new GameObject($"ManaTray_{playerId}");
            var view = go.AddComponent<ManaTrayView>();
            view.Init(seatIndex, anchor);
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private static ICardDatabase LoadCardDatabase()
        {
            var asset = Resources.Load<TextAsset>("cards/M1.cards");
            if (asset == null)
                throw new System.Exception(
                    "M1.cards.json not found — place it at Assets/Resources/cards/M1.cards.json");
            return new JsonCardDatabase(asset.text);
        }
    }
}
