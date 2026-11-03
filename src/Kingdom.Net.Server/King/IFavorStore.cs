using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.King
{
    /// <summary>A player's best score in one reign, and when they first reached it.</summary>
    public sealed record FavorScore(int Reign, string PlayerId, string DisplayName, int Score, DateTimeOffset ReachedAt);

    /// <summary>One line of a reign's board. <see cref="PlayerId"/> is for the payout sweep and
    /// never goes to a client: GET /king/favor sends place, name and score only.</summary>
    public sealed record FavorRow(int Place, string DisplayName, int Score, string PlayerId);

    /// <summary>
    /// What one placed player was paid for a closed reign. Written once, by the gold pass, with
    /// <see cref="BackId"/> null and <see cref="ChoiceOpen"/> false; the back pass then settles a
    /// top-three row, once, with the back it granted or the choice it opened.
    /// </summary>
    public sealed record FavorPrize(int Reign, string PlayerId, int Place, long Gold, int? BackId, bool ChoiceOpen);

    /// <summary>A closed reign whose gold is not paid yet, and whether the operator struck its Favorite.</summary>
    public sealed record UnpaidReign(int Reign, bool Struck);

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

        /// <summary>Every reign the player has a score in, oldest first, each once. The current
        /// reign counts: qualifying earns his seal at once, not at the close.</summary>
        Task<IReadOnlyList<int>> ReignsPlayedAsync(string playerId, CancellationToken ct = default);

        // ── The Tuesday sweep (KingPayoutSweep) ──────────────────────────────────────────────

        /// <summary>Every reign from 1 to <paramref name="beforeReign"/> - 1 whose gold is not paid,
        /// oldest first. A reign nobody played is listed too: it still has to close.</summary>
        Task<IReadOnlyList<UnpaidReign>> UnpaidReignsAsync(int beforeReign, CancellationToken ct = default);

        /// <summary>True when the player has a prize row at <paramref name="place"/> in a reign
        /// before <paramref name="beforeReign"/>, settled or not.</summary>
        Task<bool> HasHeldPlaceAsync(string playerId, int place, int beforeReign, CancellationToken ct = default);

        /// <summary>Writes the prize row. A row already there for the reign and player is kept as it is.</summary>
        Task RecordPrizeAsync(FavorPrize prize, CancellationToken ct = default);

        /// <summary>Marks the reign paid and names its Favorite. Once: a reign already paid keeps
        /// its record. A strike set before the close survives it.</summary>
        Task ClosePaidAsync(int reign, string? favoriteId, string? favoriteName, CancellationToken ct = default);

        /// <summary>The player's prize for the reign, or null.</summary>
        Task<FavorPrize?> PrizeAsync(int reign, string playerId, CancellationToken ct = default);

        /// <summary>Paid reigns' prize rows at <paramref name="throughPlace"/> or better whose back
        /// is not settled, oldest reign first, then by place. A struck reign's 1st is left out.</summary>
        Task<IReadOnlyList<FavorPrize>> UnsettledBacksAsync(int throughPlace, CancellationToken ct = default);

        /// <summary>Settles a prize row's back, once: a row already settled is not changed.</summary>
        Task SettleBackAsync(int reign, string playerId, int? backId, bool choiceOpen, CancellationToken ct = default);

        // ── Choosing a back (PrizeChoiceEndpoint) ─────────────────────────────────────────────

        /// <summary>The player's prize rows whose back choice is open, oldest reign first.</summary>
        Task<IReadOnlyList<FavorPrize>> OpenChoicesAsync(string playerId, CancellationToken ct = default);

        /// <summary>
        /// Closes an open choice, recording the back chosen, or null when it was paid in gold
        /// instead. True only for the call that closed it: a closed choice is not changed, so of
        /// two racing closes exactly one returns true. A choice never reopens. Closing on a back
        /// leaves it undelivered (<see cref="UndeliveredChoicesAsync"/>) until
        /// <see cref="MarkChoiceDeliveredAsync"/>.
        /// </summary>
        Task<bool> CloseChoiceAsync(int reign, string playerId, int? backId, CancellationToken ct = default);

        /// <summary>Choices closed on a back whose grant is not confirmed yet, oldest reign first, then by place:
        /// the player's own, or every player's when <paramref name="playerId"/> is null.</summary>
        Task<IReadOnlyList<FavorPrize>> UndeliveredChoicesAsync(string? playerId, CancellationToken ct = default);

        /// <summary>The chosen back's grant landed, or has nobody left to go to.</summary>
        Task MarkChoiceDeliveredAsync(int reign, string playerId, CancellationToken ct = default);
    }

    /// <summary>The in-memory store — the default for tests and single-box dev.</summary>
    public sealed class InMemoryFavorStore : IFavorStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<(int Reign, string PlayerId), FavorScore> _scores =
            new Dictionary<(int, string), FavorScore>();
        private readonly Dictionary<int, ReignRecord> _reigns = new Dictionary<int, ReignRecord>();
        private readonly HashSet<(int Reign, string PlayerId)> _undelivered = new HashSet<(int, string)>();
        private readonly Dictionary<(int Reign, string PlayerId), (FavorPrize Prize, bool Settled)> _prizes =
            new Dictionary<(int, string), (FavorPrize, bool)>();

        private sealed class ReignRecord
        {
            public string? FavoriteId;
            public string? FavoriteName;
            public bool Struck;
            public bool Paid;
        }

        /// <summary>Writes a reign's record, unpaid, as an operator script would. Not on
        /// <see cref="IFavorStore"/>: this is the seam tests use.</summary>
        public void SeedReign(int reign, string? favoriteId, string? favoriteName, bool struck)
        {
            lock (_lock) _reigns[reign] = new ReignRecord { FavoriteId = favoriteId, FavoriteName = favoriteName, Struck = struck };
        }

        /// <summary>What strike-favorite.sql does: flags the reign struck, paid or not.</summary>
        public void Strike(int reign)
        {
            lock (_lock)
            {
                if (!_reigns.TryGetValue(reign, out ReignRecord? r)) _reigns[reign] = r = new ReignRecord();
                r.Struck = true;
            }
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
                    .Select((s, i) => new FavorRow(i + 1, s.DisplayName, s.Score, s.PlayerId))
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
                    .Select(r => new RollRow(r.Key, r.Value.Struck ? null : r.Value.FavoriteName))
                    .ToList();
                return Task.FromResult<IReadOnlyList<RollRow>>(rows);
            }
        }

        public Task<IReadOnlyList<int>> ReignsPlayedAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var reigns = _scores.Values
                    .Where(s => s.PlayerId == playerId)
                    .Select(s => s.Reign)
                    .Distinct()
                    .OrderBy(r => r)
                    .ToList();
                return Task.FromResult<IReadOnlyList<int>>(reigns);
            }
        }

        public Task<IReadOnlyList<UnpaidReign>> UnpaidReignsAsync(int beforeReign, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var unpaid = new List<UnpaidReign>();
                for (int reign = 1; reign < beforeReign; reign++)
                {
                    _reigns.TryGetValue(reign, out ReignRecord? r);
                    if (r is null || !r.Paid) unpaid.Add(new UnpaidReign(reign, r?.Struck ?? false));
                }
                return Task.FromResult<IReadOnlyList<UnpaidReign>>(unpaid);
            }
        }

        public Task<bool> HasHeldPlaceAsync(string playerId, int place, int beforeReign, CancellationToken ct = default)
        {
            lock (_lock)
                return Task.FromResult(_prizes.Values.Any(p =>
                    p.Prize.PlayerId == playerId && p.Prize.Place == place && p.Prize.Reign < beforeReign));
        }

        public Task RecordPrizeAsync(FavorPrize prize, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var key = (prize.Reign, prize.PlayerId);
                if (!_prizes.ContainsKey(key)) _prizes[key] = (prize, false);
            }
            return Task.CompletedTask;
        }

        public Task ClosePaidAsync(int reign, string? favoriteId, string? favoriteName, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_reigns.TryGetValue(reign, out ReignRecord? r)) _reigns[reign] = r = new ReignRecord();
                if (!r.Paid)
                {
                    r.FavoriteId = favoriteId;
                    r.FavoriteName = favoriteName;
                    r.Paid = true;
                }
            }
            return Task.CompletedTask;
        }

        public Task<FavorPrize?> PrizeAsync(int reign, string playerId, CancellationToken ct = default)
        {
            lock (_lock)
                return Task.FromResult(_prizes.TryGetValue((reign, playerId), out var p) ? p.Prize : null);
        }

        public Task<IReadOnlyList<FavorPrize>> UnsettledBacksAsync(int throughPlace, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var rows = _prizes.Values
                    .Where(p => !p.Settled && p.Prize.Place <= throughPlace)
                    .Where(p => _reigns.TryGetValue(p.Prize.Reign, out ReignRecord? r) && r.Paid &&
                                !(r.Struck && p.Prize.Place == 1))
                    .Select(p => p.Prize)
                    .OrderBy(p => p.Reign).ThenBy(p => p.Place)
                    .ToList();
                return Task.FromResult<IReadOnlyList<FavorPrize>>(rows);
            }
        }

        public Task SettleBackAsync(int reign, string playerId, int? backId, bool choiceOpen, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var key = (reign, playerId);
                if (_prizes.TryGetValue(key, out var p) && !p.Settled)
                    _prizes[key] = (p.Prize with { BackId = backId, ChoiceOpen = choiceOpen }, true);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FavorPrize>> OpenChoicesAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var rows = _prizes.Values
                    .Where(p => p.Prize.PlayerId == playerId && p.Prize.ChoiceOpen)
                    .Select(p => p.Prize)
                    .OrderBy(p => p.Reign)
                    .ToList();
                return Task.FromResult<IReadOnlyList<FavorPrize>>(rows);
            }
        }

        public Task<bool> CloseChoiceAsync(int reign, string playerId, int? backId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var key = (reign, playerId);
                if (!_prizes.TryGetValue(key, out var p) || !p.Prize.ChoiceOpen) return Task.FromResult(false);
                _prizes[key] = (p.Prize with { BackId = backId, ChoiceOpen = false }, p.Settled);
                if (backId != null) _undelivered.Add(key);
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<FavorPrize>> UndeliveredChoicesAsync(string? playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var rows = _undelivered
                    .Where(k => playerId == null || k.PlayerId == playerId)
                    .Select(k => _prizes[k].Prize)
                    .OrderBy(p => p.Reign).ThenBy(p => p.Place).ThenBy(p => p.PlayerId, StringComparer.Ordinal)
                    .ToList();
                return Task.FromResult<IReadOnlyList<FavorPrize>>(rows);
            }
        }

        public Task MarkChoiceDeliveredAsync(int reign, string playerId, CancellationToken ct = default)
        {
            lock (_lock) _undelivered.Remove((reign, playerId));
            return Task.CompletedTask;
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
