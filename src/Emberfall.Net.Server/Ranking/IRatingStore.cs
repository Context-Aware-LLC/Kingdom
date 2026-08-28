using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Ranking
{
    public interface IRatingStore
    {
        int DefaultRating { get; }

        Task<int> GetRatingAsync(AccountId account, CancellationToken ct = default);

        Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(
            int limit,
            CancellationToken ct = default);

        /// <summary>
        /// Applies one eligible result exactly once. Returns null when the match was already applied
        /// or is not a real human ladder match.
        /// </summary>
        Task<RatingChange?> ApplyAsync(RankedMatchResult result, CancellationToken ct = default);
    }

    public interface IRankedMatchSink
    {
        void Record(RankedMatchResult result);
    }

    internal sealed class NullRankedMatchSink : IRankedMatchSink
    {
        public static NullRankedMatchSink Instance { get; } = new NullRankedMatchSink();
        private NullRankedMatchSink() { }
        public void Record(RankedMatchResult result) { }
    }
}
