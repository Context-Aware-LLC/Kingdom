using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// Every match a player has finished, one row per (player, match). It exists for the first-match
    /// bot policy (launch item 5, Sam 2026-10-26): a newcomer's first two matches are against fixed
    /// bots, so the gate has to know how many matches an account has finished — counting bot
    /// matches, which <c>ladder_player.games_played</c> deliberately does not.
    ///
    /// <para><b>Append-only with a natural key, not a counter</b>, for the reason quest progress and
    /// the ledger are: a redelivered result writes the same row and changes nothing, so the count
    /// cannot be inflated by a retry or by two servers racing the same match.</para>
    /// </summary>
    public interface ICompletedMatchStore
    {
        /// <summary>Record one finished match for one player. Idempotent on (player, match).</summary>
        Task RecordAsync(string playerId, string matchId, DateTimeOffset endedAtUtc, CancellationToken ct = default);

        /// <summary>The ids of this player's earliest finished matches, at most <paramref name="limit"/>.
        /// Ids rather than a count so a caller can union them with matches it has not written yet.</summary>
        Task<IReadOnlyList<string>> FirstMatchIdsAsync(string playerId, int limit, CancellationToken ct = default);
    }

    /// <summary>The in-memory store — the default for tests and single-box dev.</summary>
    public sealed class InMemoryCompletedMatchStore : ICompletedMatchStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, List<(string MatchId, DateTimeOffset EndedAt)>> _rows =
            new Dictionary<string, List<(string, DateTimeOffset)>>(StringComparer.Ordinal);

        public Task RecordAsync(string playerId, string matchId, DateTimeOffset endedAtUtc, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_rows.TryGetValue(playerId, out var list))
                    _rows[playerId] = list = new List<(string, DateTimeOffset)>();
                if (!list.Any(r => r.MatchId == matchId)) list.Add((matchId, endedAtUtc));
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> FirstMatchIdsAsync(string playerId, int limit, CancellationToken ct = default)
        {
            lock (_lock)
            {
                IReadOnlyList<string> ids = _rows.TryGetValue(playerId, out var list)
                    ? list.OrderBy(r => r.EndedAt).ThenBy(r => r.MatchId, StringComparer.Ordinal)
                        .Take(limit).Select(r => r.MatchId).ToArray()
                    : Array.Empty<string>();
                return Task.FromResult(ids);
            }
        }
    }
}
