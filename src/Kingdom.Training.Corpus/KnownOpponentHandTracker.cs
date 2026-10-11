using System;
using System.Collections.Generic;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Core.Projection;

namespace Kingdom.Training.Corpus
{
    /// <summary>
    /// Reconstructs the definitions a viewer can still prove are in the opponent's hand. The
    /// interface accepts projected public transitions only: hidden authority identities never
    /// enter this module. Counts are a multiset because hidden-zone identity is deliberately lost.
    /// </summary>
    internal sealed class KnownOpponentHandTracker
    {
        private readonly PlayerId _viewer;
        private readonly List<int> _knownDefinitions = new List<int>();

        public KnownOpponentHandTracker(PlayerId viewer) => _viewer = viewer;

        public IReadOnlyList<int> Snapshot()
        {
            var result = new List<int>(_knownDefinitions);
            result.Sort();
            return result;
        }

        public void Observe(
            GameState projectedBefore,
            IReadOnlyList<GameEvent> projectedEvents,
            GameState projectedAfter,
            IReadOnlyList<int> publiclyPlayedDefinitions)
        {
            if (projectedBefore is null) throw new ArgumentNullException(nameof(projectedBefore));
            if (projectedEvents is null) throw new ArgumentNullException(nameof(projectedEvents));
            if (projectedAfter is null) throw new ArgumentNullException(nameof(projectedAfter));
            if (publiclyPlayedDefinitions is null)
                throw new ArgumentNullException(nameof(publiclyPlayedDefinitions));

            int beforeViewerSeat = projectedBefore.SeatOf(_viewer);
            int afterViewerSeat = projectedAfter.SeatOf(_viewer);
            if (beforeViewerSeat != afterViewerSeat)
                throw new ArgumentException("The viewer changed seats across one public transition.");

            PlayerState opponentBefore = projectedBefore.Players[1 - beforeViewerSeat];
            PlayerState opponentAfter = projectedAfter.Players[1 - afterViewerSeat];

            int identifiedDepartures = ForgetPubliclyPlayedDefinitions(publiclyPlayedDefinitions);
            ForgetUnidentifiedDepartures(
                opponentBefore.Hand.Count,
                opponentAfter.Hand.Count,
                projectedEvents,
                identifiedDepartures);
            LearnPublicBounces(opponentBefore, projectedEvents, projectedAfter);
        }

        private int ForgetPubliclyPlayedDefinitions(IReadOnlyList<int> definitions)
        {
            foreach (int definition in definitions) _knownDefinitions.Remove(definition);
            return definitions.Count;
        }

        private void ForgetUnidentifiedDepartures(
            int beforeHandCount,
            int afterHandCount,
            IReadOnlyList<GameEvent> events,
            int identifiedDepartures)
        {
            int hiddenEntries = 0;
            foreach (GameEvent gameEvent in events)
            {
                if ((gameEvent.Kind == GameEventKind.CardDrawn
                        || gameEvent.Kind == GameEventKind.CardCreatedInHand)
                    && gameEvent.Source == GameStateProjector.Sentinel)
                    hiddenEntries++;
                else if (gameEvent.Kind == GameEventKind.CardBounced
                    && gameEvent.Target == GameStateProjector.Sentinel)
                    hiddenEntries++;
            }

            int unidentifiedDepartures = Math.Max(
                0,
                beforeHandCount + hiddenEntries - afterHandCount - identifiedDepartures);
            if (unidentifiedDepartures == 0 || _knownDefinitions.Count == 0) return;

            var counts = new Dictionary<int, int>();
            foreach (int definition in _knownDefinitions)
                counts[definition] = counts.TryGetValue(definition, out int count) ? count + 1 : 1;
            _knownDefinitions.Clear();
            foreach (KeyValuePair<int, int> pair in counts)
                for (int remaining = Math.Max(0, pair.Value - unidentifiedDepartures);
                     remaining > 0;
                     remaining--)
                    _knownDefinitions.Add(pair.Key);
        }

        private void LearnPublicBounces(
            PlayerState opponentBefore,
            IReadOnlyList<GameEvent> events,
            GameState projectedAfter)
        {
            int hiddenBounces = 0;
            foreach (GameEvent gameEvent in events)
                if (gameEvent.Kind == GameEventKind.CardBounced
                    && gameEvent.Target == GameStateProjector.Sentinel)
                    hiddenBounces++;
            if (hiddenBounces == 0) return;

            var vanished = new List<int>();
            foreach (CardInstance card in PublicCards(opponentBefore))
                if (card.Id != GameStateProjector.Sentinel
                    && FindPublicCard(projectedAfter, card.Id) == null)
                    vanished.Add(card.DefinitionId);

            // A redacted bounce proves only how many cards entered the hidden hand. If additional
            // public cards vanished in the same command, their definitions cannot be paired with
            // those events without guessing, so fail closed instead of inventing knowledge.
            if (vanished.Count != hiddenBounces) return;
            _knownDefinitions.AddRange(vanished);
        }

        private static IEnumerable<CardInstance> PublicCards(PlayerState player)
        {
            if (player.Weapon != null) yield return player.Weapon;
            foreach (CardInstance card in player.Board) yield return card;
            foreach (CardInstance card in player.Graveyard) yield return card;
        }

        private static CardInstance? FindPublicCard(GameState state, InstanceId id)
        {
            foreach (PlayerState player in state.Players)
            {
                if (player.Weapon != null && player.Weapon.Id == id) return player.Weapon;
                foreach (CardInstance card in player.Board)
                    if (card.Id == id) return card;
                foreach (CardInstance card in player.Graveyard)
                    if (card.Id == id) return card;
            }
            return null;
        }
    }
}
