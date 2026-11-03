using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.King
{
    /// <summary>A player's best score in one reign, and when they first reached it.</summary>
    public sealed record FavorScore(int Reign, string PlayerId, string DisplayName, int Score, DateTimeOffset ReachedAt);

    /// <summary>One line of a reign's board.</summary>
    public sealed record FavorRow(int Place, string DisplayName, int Score);

    /// <summary>One closed reign and its Favorite. <see cref="FavoriteName"/> is null when nobody
    /// played, or the Favorite was struck, or their account was deleted.</summary>
    public sealed record RollRow(int Reign, string? FavoriteName);

    /// <summary>
    /// Where the King's Favor scores live. One row per <c>(reign, player)</c> holding that player's
    /// <b>best</b> score; a worse or equal score writes nothing, so the time they first got there
    /// is the one kept. Ties on score go to whoever reached it first.
    ///
    /// <para>Two implementations, one contract: in-memory for tests and single-box dev, Postgres for
    /// production.</para>
    /// </summary>
    public interface IFavorStore
    {
        /// <summary>
        /// Keep this score if it beats the player's best for the reign. Returns the best they held
        /// before the call, or null when they had none.
        /// </summary>
        Task<int?> RecordAsync(FavorScore score, bool lowerIsBetter, CancellationToken ct = default);

        /// <summary>The player's best score in the reign, or null.</summary>
        Task<int?> BestAsync(int reign, string playerId, CancellationToken ct = default);

        /// <summary>The board, best first, places numbered from 1.</summary>
        Task<IReadOnlyList<FavorRow>> TopAsync(int reign, bool lowerIsBetter, int limit, CancellationToken ct = default);

        /// <summary>The player's place on the board, or null when they have no score in the reign.</summary>
        Task<int?> PlaceAsync(int reign, string playerId, bool lowerIsBetter, CancellationToken ct = default);

        /// <summary>The closed reigns that have a record, newest first.</summary>
        Task<IReadOnlyList<RollRow>> RollAsync(int limit, CancellationToken ct = default);
    }

    /// <summary>The in-memory store — the default for tests and single-box dev.</summary>
    public sealed class InMemoryFavorStore : IFavorStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<(int Reign, string PlayerId), FavorScore> _scores =
            new Dictionary<(int, string), FavorScore>();
        private readonly Dictionary<int, (string? Name, bool Struck)> _reigns =
            new Dictionary<int, (string?, bool)>();

        /// <summary>Writes a closed reign's record. Not on <see cref="IFavorStore"/>: the close-out
        /// owns that write in production; this is the seam tests use.</summary>
        public void SeedReign(int reign, string? favoriteId, string? favoriteName, bool struck)
        {
            lock (_lock) _reigns[reign] = (favoriteName, struck);
        }

        public Task<int?> RecordAsync(FavorScore score, bool lowerIsBetter, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var key = (score.Reign, score.PlayerId);
                if (!_scores.TryGetValue(key, out FavorScore? held))
                {
                    _scores[key] = score;
                    return Task.FromResult<int?>(null);
                }
                // Strictly better only: an equal score must not move reached_at.
                bool better = lowerIsBetter ? score.Score < held.Score : score.Score > held.Score;
                if (better) _scores[key] = score;
                return Task.FromResult<int?>(held.Score);
            }
        }

        public Task<int?> BestAsync(int reign, string playerId, CancellationToken ct = default)
        {
            lock (_lock)
                return Task.FromResult<int?>(_scores.TryGetValue((reign, playerId), out FavorScore? s) ? s.Score : (int?)null);
        }

        public Task<IReadOnlyList<FavorRow>> TopAsync(int reign, bool lowerIsBetter, int limit, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var rows = Ranked(reign, lowerIsBetter)
                    .Take(Math.Max(0, limit))
                    .Select((s, i) => new FavorRow(i + 1, s.DisplayName, s.Score))
                    .ToList();
                return Task.FromResult<IReadOnlyList<FavorRow>>(rows);
            }
        }

        public Task<int?> PlaceAsync(int reign, string playerId, bool lowerIsBetter, CancellationToken ct = default)
        {
            lock (_lock)
            {
                int i = 0;
                foreach (FavorScore s in Ranked(reign, lowerIsBetter))
                {
                    i++;
                    if (s.PlayerId == playerId) return Task.FromResult<int?>(i);
                }
                return Task.FromResult<int?>(null);
            }
        }

        public Task<IReadOnlyList<RollRow>> RollAsync(int limit, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var rows = _reigns
                    .OrderByDescending(r => r.Key)
                    .Take(Math.Max(0, limit))
                    .Select(r => new RollRow(r.Key, r.Value.Struck ? null : r.Value.Name))
                    .ToList();
                return Task.FromResult<IReadOnlyList<RollRow>>(rows);
            }
        }

        /// <summary>Best first; ties to the earlier arrival, then the id so the order is total.</summary>
        private IEnumerable<FavorScore> Ranked(int reign, bool lowerIsBetter)
        {
            IEnumerable<FavorScore> inReign = _scores.Values.Where(s => s.Reign == reign);
            IOrderedEnumerable<FavorScore> ordered = lowerIsBetter
                ? inReign.OrderBy(s => s.Score)
                : inReign.OrderByDescending(s => s.Score);
            return ordered.ThenBy(s => s.ReachedAt).ThenBy(s => s.PlayerId, StringComparer.Ordinal);
        }
    }
}
