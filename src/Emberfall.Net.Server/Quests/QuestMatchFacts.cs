using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Telemetry;

namespace Emberfall.Net.Server.Quests
{
    /// <summary>
    /// One seat's view of one finished match — the ONLY thing a quest predicate is allowed to read
    /// (dev-docs/specs/daily-quests.md §3). Everything on it comes from
    /// <see cref="MatchCompleteEvent"/> and its paired <see cref="CardSeenEvent"/>; there is
    /// deliberately no route from a predicate back to the rules engine, so a quest can never ask a
    /// question the match-end events do not already answer.
    ///
    /// <para><b>Damage and healing are absent because the events do not carry them.</b> That is the
    /// constraint the spec's §3 exists to state, and this type is where it is enforced. Minion
    /// deaths were absent for the same reason until <see cref="SeatOutcome.EnemyMinionsDestroyed"/>
    /// landed (Sam, 2026-10-10), which is the route any further counter has to take.</para>
    /// </summary>
    public sealed record QuestMatchFacts(
        string MatchId,
        string PlayerId,
        DateTimeOffset EndedAtUtc,
        TelemetrySource Source,
        CardClass Class,
        SeatResult Result,
        int TurnCount,
        int DistinctCardsPlayed,
        int EnemyMinionsDestroyed,
        IReadOnlyDictionary<CardClass, int> DistinctCardsPlayedByClass)
    {
        public bool Won => Result == SeatResult.Win;

        public int DistinctCardsPlayedOf(CardClass @class)
            => DistinctCardsPlayedByClass.TryGetValue(@class, out int n) ? n : 0;

        /// <summary>
        /// Both seats of one finished match, as facts. Returns nothing when the two events do not
        /// describe the same match — the pairing is the caller's job and a mismatch is a bug, not a
        /// case to absorb.
        /// </summary>
        /// <param name="cards">
        /// The card database, for the class of a played card. A card id it does not know contributes
        /// to <see cref="DistinctCardsPlayed"/> and to no class bucket: an unknown id is a data
        /// problem, and silently dropping the card from the total as well would understate a quest a
        /// player has legitimately advanced.
        /// </param>
        public static IReadOnlyList<QuestMatchFacts> From(
            MatchCompleteEvent complete,
            CardSeenEvent cards,
            ICardDatabase cardDb)
        {
            if (complete is null) throw new ArgumentNullException(nameof(complete));
            if (cards is null) throw new ArgumentNullException(nameof(cards));
            if (cardDb is null) throw new ArgumentNullException(nameof(cardDb));
            if (!complete.Match.Equals(cards.Match)) return Array.Empty<QuestMatchFacts>();

            var byAccount = new Dictionary<string, (int Total, Dictionary<CardClass, int> ByClass)>(
                StringComparer.Ordinal);

            foreach (CardSeenRow row in cards.Rows)
            {
                // Null means the card never left the deck. A quest counts what was PLAYED.
                if (row.FirstPlayedTurn is null) continue;
                if (!row.Account.IsAttributed) continue;

                if (!byAccount.TryGetValue(row.Account.Value, out var acc))
                    byAccount[row.Account.Value] = acc =
                        (0, new Dictionary<CardClass, int>());

                acc.Total++;
                if (cardDb.TryGet(row.CardDefId, out CardDefinition def))
                    acc.ByClass[def.Class] = acc.ByClass.TryGetValue(def.Class, out int n) ? n + 1 : 1;
                byAccount[row.Account.Value] = acc;
            }

            var facts = new List<QuestMatchFacts>(2);
            AddSeat(facts, complete, complete.P0, byAccount);
            AddSeat(facts, complete, complete.P1, byAccount);
            return facts;
        }

        private static void AddSeat(
            List<QuestMatchFacts> into,
            MatchCompleteEvent complete,
            SeatOutcome seat,
            Dictionary<string, (int Total, Dictionary<CardClass, int> ByClass)> byAccount)
        {
            if (!seat.Account.IsAttributed) return;

            byAccount.TryGetValue(seat.Account.Value, out var played);
            into.Add(new QuestMatchFacts(
                complete.Match.Value,
                seat.Account.Value,
                complete.EndedAtUtc,
                complete.Source,
                seat.Class,
                seat.Result,
                complete.TurnCount,
                played.Total,
                seat.EnemyMinionsDestroyed,
                (IReadOnlyDictionary<CardClass, int>?)played.ByClass
                    ?? new Dictionary<CardClass, int>()));
        }
    }
}
