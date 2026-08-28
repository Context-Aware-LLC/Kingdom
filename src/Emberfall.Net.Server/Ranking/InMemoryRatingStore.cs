using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Telemetry;

namespace Emberfall.Net.Server.Ranking
{
    public sealed class InMemoryRatingStore : IRatingStore, IRankedMatchSink
    {
        private sealed record Standing(
            string DisplayName,
            int Rating,
            long Games,
            long Wins,
            long Losses,
            long Draws,
            DateTimeOffset UpdatedAt);

        private readonly object _lock = new object();
        private readonly Dictionary<AccountId, Standing> _players = new Dictionary<AccountId, Standing>();
        private readonly HashSet<string> _matches = new HashSet<string>(StringComparer.Ordinal);

        public InMemoryRatingStore(int defaultRating = 1_000)
        {
            if (defaultRating <= 0) throw new ArgumentOutOfRangeException(nameof(defaultRating));
            DefaultRating = defaultRating;
        }

        public int DefaultRating { get; }

        public Task<int> GetRatingAsync(AccountId account, CancellationToken ct = default)
        {
            lock (_lock)
                return Task.FromResult(_players.TryGetValue(account, out var standing)
                    ? standing.Rating
                    : DefaultRating);
        }

        public Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(
            int limit,
            CancellationToken ct = default)
        {
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));

            lock (_lock)
            {
                IReadOnlyList<LeaderboardEntry> rows = _players
                    .OrderByDescending(pair => pair.Value.Rating)
                    .ThenByDescending(pair => pair.Value.Wins)
                    .ThenBy(pair => pair.Value.Games)
                    .ThenBy(pair => pair.Key.Value, StringComparer.Ordinal)
                    .Take(limit)
                    .Select((pair, index) => new LeaderboardEntry(
                        index + 1,
                        pair.Value.DisplayName,
                        pair.Value.Rating,
                        pair.Value.Games,
                        pair.Value.Wins,
                        pair.Value.Losses,
                        pair.Value.Draws))
                    .ToArray();
                return Task.FromResult(rows);
            }
        }

        public Task<RatingChange?> ApplyAsync(RankedMatchResult result, CancellationToken ct = default)
        {
            if (!result.IsEligible) return Task.FromResult<RatingChange?>(null);

            lock (_lock)
            {
                if (!_matches.Add(result.Match.Value)) return Task.FromResult<RatingChange?>(null);

                Standing p0 = Find(result.P0, result.P0DisplayName, result.EndedAtUtc);
                Standing p1 = Find(result.P1, result.P1DisplayName, result.EndedAtUtc);
                (int p0Delta, int p1Delta) = EloRating.Calculate(
                    p0.Rating, p0.Games, p1.Rating, p1.Games, result.Outcome);

                _players[result.P0] = Update(p0, result.P0DisplayName, p0Delta, ResultFor(0, result.Outcome), result.EndedAtUtc);
                _players[result.P1] = Update(p1, result.P1DisplayName, p1Delta, ResultFor(1, result.Outcome), result.EndedAtUtc);

                return Task.FromResult<RatingChange?>(new RatingChange(
                    result.Match.Value,
                    p0.Rating,
                    p0.Rating + p0Delta,
                    p1.Rating,
                    p1.Rating + p1Delta));
            }
        }

        public void Record(RankedMatchResult result)
            => ApplyAsync(result).GetAwaiter().GetResult();

        private Standing Find(AccountId account, string displayName, DateTimeOffset at)
            => _players.TryGetValue(account, out var standing)
                ? standing
                : new Standing(NormalizeName(displayName), DefaultRating, 0, 0, 0, 0, at);

        private static Standing Update(
            Standing standing,
            string displayName,
            int delta,
            SeatResult result,
            DateTimeOffset at)
            => standing with
            {
                DisplayName = NormalizeName(displayName),
                Rating = standing.Rating + delta,
                Games = standing.Games + 1,
                Wins = standing.Wins + (result == SeatResult.Win ? 1 : 0),
                Losses = standing.Losses + (result == SeatResult.Loss ? 1 : 0),
                Draws = standing.Draws + (result == SeatResult.Draw ? 1 : 0),
                UpdatedAt = at,
            };

        private static SeatResult ResultFor(int seat, MatchOutcome outcome) => outcome switch
        {
            MatchOutcome.Player0Win => seat == 0 ? SeatResult.Win : SeatResult.Loss,
            MatchOutcome.Player1Win => seat == 1 ? SeatResult.Win : SeatResult.Loss,
            _ => SeatResult.Draw,
        };

        internal static string NormalizeName(string displayName)
            => string.IsNullOrWhiteSpace(displayName) ? "Unnamed Player" : displayName.Trim();
    }
}
