using System;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Net.Server.Ranking
{
    public sealed record LeaderboardEntry(
        int Rank,
        string DisplayName,
        int Rating,
        long GamesPlayed,
        long Wins,
        long Losses,
        long Draws);

    /// <summary>One player's own place on the ladder. Only exists once they have a rated match
    /// behind them — a player who has never played one has no standing, not a default one.</summary>
    public sealed record PlayerStanding(int Rating, long GamesPlayed);

    public sealed record RankedMatchResult(
        MatchId Match,
        DateTimeOffset EndedAtUtc,
        AccountId P0,
        string P0DisplayName,
        AccountId P1,
        string P1DisplayName,
        MatchOutcome Outcome,
        TelemetrySource Source,
        MatchEndReason Reason = MatchEndReason.Win)
    {
        /// <summary>The match ended because the losing seat conceded, or was conceded for after
        /// sitting out its turns. That seat is paid nothing (Sam, 2026-10-20).</summary>
        public bool LoserForfeited => Reason is MatchEndReason.Concede or MatchEndReason.Timeout;

        public bool IsEligible =>
            Source == TelemetrySource.Ladder
            && P0.IsAttributed
            && P1.IsAttributed
            && P0 != P1
            && Outcome != MatchOutcome.InProgress;
    }

    public sealed record RatingChange(
        string MatchId,
        int P0Before,
        int P0After,
        int P1Before,
        int P1After)
    {
        public int P0Delta => P0After - P0Before;
        public int P1Delta => P1After - P1Before;
    }

    internal static class EloRating
    {
        internal const int ProvisionalGames = 20;
        internal const int ProvisionalK = 40;
        internal const int EstablishedK = 24;
        private const double RatingScale = 400d;

        public static (int p0Delta, int p1Delta) Calculate(
            int p0Rating,
            long p0Games,
            int p1Rating,
            long p1Games,
            MatchOutcome outcome)
        {
            if (outcome == MatchOutcome.InProgress)
                throw new ArgumentException("An in-progress match has no rating result.", nameof(outcome));

            double p0Score = outcome switch
            {
                MatchOutcome.Player0Win => 1d,
                MatchOutcome.Player1Win => 0d,
                _ => 0.5d,
            };
            double expectedP0 = 1d / (1d + Math.Pow(10d, (p1Rating - p0Rating) / RatingScale));
            int k = p0Games < ProvisionalGames || p1Games < ProvisionalGames
                ? ProvisionalK
                : EstablishedK;
            int p0Delta = (int)Math.Round(k * (p0Score - expectedP0), MidpointRounding.AwayFromZero);

            // Keep every match zero-sum. Computing the second delta independently can lose a point
            // to rounding, which slowly inflates or deflates the entire ladder.
            return (p0Delta, -p0Delta);
        }
    }
}
