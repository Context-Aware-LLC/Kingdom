using Emberfall.AI;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Scene entry point for the M1 vertical slice (ARCHITECTURE §6 LocalMatchHost path).
    /// Wires: JsonCardDatabase → MatchSetup → two RandomAi intent sources → MatchRunner
    ///        → BoardController for visual card tracking.
    ///
    /// Card data: Assets/Resources/cards/M1.cards.json (loaded as TextAsset).
    ///
    /// Phase C step 2 setup — add all components to one GameObject in a new scene:
    ///   • MatchRunner
    ///   • BoardAnchors   (+ ProceduralBoardSetup for automatic anchor hierarchy)
    ///   • BoardController
    ///   • GameBootstrap  (assign the above in the Inspector)
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] private MatchRunner matchRunner;

        [Header("Board (Phase C step 2 — optional, null = console-only mode)")]
        [SerializeField] private BoardAnchors boardAnchors;
        [SerializeField] private BoardController boardController;

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

            var p0Ai = new RandomAi(p0Id);
            var p1Ai = new RandomAi(p1Id);

            // Wire board controller if present.
            if (boardController != null && boardAnchors != null)
            {
                boardAnchors.SetPlayers(p0Id, p1Id);
                boardController.Initialize(db, boardAnchors, p0Id, p1Id);
                matchRunner.OnStateUpdated += boardController.OnStateUpdated;
            }

            matchRunner.OnStateUpdated += state =>
            {
                p0Ai.NotifyStateUpdate(state);
                p1Ai.NotifyStateUpdate(state);

                Debug.Log($"[State] Turn {state.TurnNumber} | Phase={state.Phase} | Active={state.ActivePlayer} | " +
                          $"P0 HP={state.Players[0].Hero.CurrentHealth} " +
                          $"mana={state.Players[0].Mana.Available}/{state.Players[0].Mana.Max} | " +
                          $"P1 HP={state.Players[1].Hero.CurrentHealth} " +
                          $"mana={state.Players[1].Mana.Available}/{state.Players[1].Mana.Max}");
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

            matchRunner.StartMatch(db, setup, p0Ai, p1Ai);
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
