using Emberfall.AI;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Scene entry point for the M1 vertical slice (ARCHITECTURE §6 LocalMatchHost path).
    /// Wires: JsonCardDatabase → MatchSetup → two RandomAi intent sources → MatchRunner.
    /// Logs every state change and event to the console so the match loop is observable
    /// before any board visuals exist. Add a MatchRunner MonoBehaviour to the same GameObject
    /// in the scene and assign it in the Inspector.
    ///
    /// Card data: place M1.cards.json in Assets/Resources/cards/ (loaded as a TextAsset).
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] private MatchRunner matchRunner;

        private void Start()
        {
            if (matchRunner == null)
            {
                Debug.LogError("[GameBootstrap] matchRunner is not assigned. Add a MatchRunner component and assign it.");
                return;
            }

            var db = LoadCardDatabase();
            var setup = PrebuiltDecks.EmberwardVsTidecaller();

            var p0Id = new PlayerId("P0");
            var p1Id = new PlayerId("P1");

            var p0Ai = new RandomAi(p0Id);
            var p1Ai = new RandomAi(p1Id);

            matchRunner.OnStateUpdated += state =>
            {
                // Notify AIs so they can replan before the next loop tick.
                p0Ai.NotifyStateUpdate(state);
                p1Ai.NotifyStateUpdate(state);

                Debug.Log($"[State] Turn {state.TurnNumber} | Phase={state.Phase} | Active={state.ActivePlayer} | " +
                          $"P0 HP={state.Players[0].Hero.CurrentHealth} mana={state.Players[0].Mana.Available}/{state.Players[0].Mana.Max} | " +
                          $"P1 HP={state.Players[1].Hero.CurrentHealth} mana={state.Players[1].Mana.Available}/{state.Players[1].Mana.Max}");
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
            // File must be at: Assets/Resources/cards/M1.cards.json
            // Unity strips the extension, so load as "cards/M1.cards".
            var asset = Resources.Load<TextAsset>("cards/M1.cards");
            if (asset == null)
                throw new System.Exception(
                    "M1.cards.json not found. Place the file at Assets/Resources/cards/M1.cards.json");
            return new JsonCardDatabase(asset.text);
        }
    }
}
