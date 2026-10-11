using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Quests
{
    /// <summary>
    /// One match's contribution to one quest (dev-docs/specs/daily-quests.md §6). The key
    /// <c>(PlayerId, QuestId, Day, MatchId)</c> is natural and complete, which is the whole design:
    /// a redelivered match writes the same row and changes nothing.
    /// </summary>
    public readonly record struct QuestCredit(
        string PlayerId,
        string QuestId,
        DateOnly Day,
        string MatchId,
        int Credit,
        DateTimeOffset CreditedAt);

    /// <summary>
    /// What a <see cref="IQuestProgressStore.CreditAsync"/> did.
    ///
    /// <para><b><see cref="Applied"/> is what keeps a completed quest from paying twice</b>, and it
    /// is not obvious why a total alone will not do. The Gold faucet scopes its transaction id by
    /// the CURRENT UTC day, while a stored quest can be credited by matches on later days — so a
    /// quest that completed yesterday and is credited again today would mint a NEW transaction id
    /// and pay a second time. The caller grants only on the transition into completion, and a
    /// replayed match is not a transition because it applied nothing.</para>
    /// </summary>
    public readonly record struct QuestCreditResult(bool Applied, int Total);

    /// <summary>
    /// Where quest progress lives. <b>Append-only, and progress is a projection</b> — the sum of a
    /// quest's credits — for the same reason the entitlement ledger next door works that way: a
    /// counter would need an idempotency story of its own, and it would get one subtly different
    /// from the ledger's.
    ///
    /// <para>Two implementations, one contract: an in-memory one for tests and single-box dev, and
    /// a Postgres one for production.</para>
    /// </summary>
    public interface IQuestProgressStore
    {
        /// <summary>
        /// Record one match's credit toward one quest, idempotently on the natural key. Returns the
        /// quest's total AFTER the write and whether this call is what wrote it.
        /// </summary>
        Task<QuestCreditResult> CreditAsync(QuestCredit credit, CancellationToken ct = default);

        /// <summary>Total credit for each of these quest-days. Absent keys are zero.</summary>
        Task<IReadOnlyDictionary<(string QuestId, DateOnly Day), int>> ProgressAsync(
            string playerId,
            IReadOnlyList<(string QuestId, DateOnly Day)> quests,
            CancellationToken ct = default);
    }

    /// <summary>The in-memory store — the default for tests and single-box dev.</summary>
    public sealed class InMemoryQuestProgressStore : IQuestProgressStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<(string, string, DateOnly, string), int> _rows =
            new Dictionary<(string, string, DateOnly, string), int>();

        public Task<QuestCreditResult> CreditAsync(QuestCredit credit, CancellationToken ct = default)
        {
            lock (_lock)
            {
                // TryAdd, not an assignment: a replayed match must not restate its credit, because a
                // credit that could be rewritten is a credit that could be rewritten larger.
                bool applied = _rows.TryAdd(
                    (credit.PlayerId, credit.QuestId, credit.Day, credit.MatchId), credit.Credit);
                return Task.FromResult(new QuestCreditResult(
                    applied, Total(credit.PlayerId, credit.QuestId, credit.Day)));
            }
        }

        public Task<IReadOnlyDictionary<(string QuestId, DateOnly Day), int>> ProgressAsync(
            string playerId,
            IReadOnlyList<(string QuestId, DateOnly Day)> quests,
            CancellationToken ct = default)
        {
            var acc = new Dictionary<(string, DateOnly), int>();
            lock (_lock)
            {
                foreach ((string questId, DateOnly day) in quests)
                    acc[(questId, day)] = Total(playerId, questId, day);
            }
            return Task.FromResult((IReadOnlyDictionary<(string, DateOnly), int>)acc);
        }

        private int Total(string playerId, string questId, DateOnly day)
        {
            int sum = 0;
            foreach (var row in _rows)
            {
                if (row.Key.Item1 == playerId && row.Key.Item2 == questId && row.Key.Item3 == day)
                    sum += row.Value;
            }
            return sum;
        }
    }
}
