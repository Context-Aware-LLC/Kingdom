using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Achievements
{
    /// <summary>One earned achievement: which match earned it, and when.</summary>
    public readonly record struct EarnedAchievement(string AchievementId, string MatchId, DateTimeOffset EarnedAt);

    /// <summary>
    /// Where earned achievements live. <b>Earned once, never changed</b>: the key is
    /// <c>(PlayerId, AchievementId)</c>, a second earning writes nothing, and the first match and
    /// date are the ones kept. A redelivered match is therefore harmless.
    ///
    /// <para>Two implementations, one contract: in-memory for tests and single-box dev, Postgres for
    /// production.</para>
    /// </summary>
    public interface IAchievementStore
    {
        /// <summary>
        /// Record these achievements as earned by this match. Returns the ids this call recorded;
        /// one the player already held is left as it was and is not returned.
        /// </summary>
        Task<IReadOnlyList<string>> EarnAsync(
            string playerId,
            IReadOnlyList<string> achievementIds,
            string matchId,
            DateTimeOffset earnedAt,
            CancellationToken ct = default);

        /// <summary>Everything the player has earned, oldest first.</summary>
        Task<IReadOnlyList<EarnedAchievement>> ReadAsync(string playerId, CancellationToken ct = default);
    }

    /// <summary>The in-memory store — the default for tests and single-box dev.</summary>
    public sealed class InMemoryAchievementStore : IAchievementStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<(string PlayerId, string AchievementId), EarnedAchievement> _rows =
            new Dictionary<(string, string), EarnedAchievement>();

        public Task<IReadOnlyList<string>> EarnAsync(
            string playerId,
            IReadOnlyList<string> achievementIds,
            string matchId,
            DateTimeOffset earnedAt,
            CancellationToken ct = default)
        {
            var recorded = new List<string>();
            lock (_lock)
            {
                foreach (string id in achievementIds)
                {
                    // TryAdd, not an assignment: a later match must not move the date it was earned.
                    if (_rows.TryAdd((playerId, id), new EarnedAchievement(id, matchId, earnedAt)))
                        recorded.Add(id);
                }
            }
            return Task.FromResult<IReadOnlyList<string>>(recorded);
        }

        public Task<IReadOnlyList<EarnedAchievement>> ReadAsync(string playerId, CancellationToken ct = default)
        {
            var earned = new List<EarnedAchievement>();
            lock (_lock)
            {
                foreach (var row in _rows)
                    if (row.Key.PlayerId == playerId) earned.Add(row.Value);
            }
            earned.Sort((a, b) =>
            {
                int byDate = a.EarnedAt.CompareTo(b.EarnedAt);
                return byDate != 0 ? byDate : string.CompareOrdinal(a.AchievementId, b.AchievementId);
            });
            return Task.FromResult<IReadOnlyList<EarnedAchievement>>(earned);
        }
    }
}
