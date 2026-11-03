using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Net.Server.King
{
    /// <summary>
    /// The King a match is played for, fixed when it starts. A match counts for the reign it began
    /// in, so one running across Tuesday midnight keeps its commission and banks to that reign.
    ///
    /// <para><b>The names travel here, not on <see cref="SeatOutcome"/>,</b> because a seat outcome is
    /// written to the JSONL telemetry and a player's name has no business there. They are the names
    /// the seats played under, already through <c>PlayerName.Check</c> in <c>MatchGate</c>.</para>
    /// </summary>
    /// <param name="BestP0">Seat 0's best score in the reign when the match began, or null.</param>
    public sealed record KingSeating(int Reign, string CommissionId, int? BestP0, int? BestP1, string P0Name, string P1Name)
    {
        /// <summary>
        /// The seating for a match starting at <paramref name="nowMs"/>, or null when it plays for
        /// no King: a friend or soak match, or one before Reign 1. Ladder and bot-filled matches are
        /// seated alike, so the wire cannot tell them apart.
        ///
        /// <para>A best that cannot be read is null and logged. It only feeds the in-match card;
        /// the banked score is decided at match end against the store itself.</para>
        /// </summary>
        public static async Task<KingSeating?> ForMatchAsync(
            IFavorStore store, TelemetrySource source, long reignOneMs, long nowMs,
            AccountId p0, AccountId p1, string p0Name, string p1Name, CancellationToken ct)
        {
            if (store is null) throw new ArgumentNullException(nameof(store));
            if (source != TelemetrySource.Ladder && source != TelemetrySource.BotFill) return null;
            int reign = KingCalendar.ReignAt(nowMs, reignOneMs);
            if (reign < 1) return null;

            return new KingSeating(
                reign,
                KingSchedule.For(reign).Commission,
                await BestAsync(store, reign, p0, ct).ConfigureAwait(false),
                await BestAsync(store, reign, p1, ct).ConfigureAwait(false),
                p0Name,
                p1Name);
        }

        /// <summary>The seat's best this reign when the match began, or null.</summary>
        public int? BestFor(int seat) => seat == 0 ? BestP0 : BestP1;

        /// <summary>
        /// <paramref name="seat"/>'s commission card for its own snapshot, scored off
        /// <paramref name="soFar"/> by the same <see cref="Commission.Score"/> that banks it, so the
        /// live number and the final one cannot disagree. Null for a commission the catalog does
        /// not know, which banks nothing either.
        /// </summary>
        /// <param name="live">The match is still in progress; a win-only score is greyed until it ends.</param>
        public CommissionProgress? ProgressFor(int seat, AchievementMatchFacts soFar, bool live)
        {
            Commission? commission = CommissionCatalog.Find(CommissionId);
            if (commission is null) return null;
            int score = commission.Score(soFar);
            return new CommissionProgress(
                commission.Id,
                commission.Qualifies(score) ? score : (int?)null,
                commission.NeedsWin && live,
                BestFor(seat));
        }

        /// <summary>
        /// <paramref name="seat"/>'s final score for its match end, read off the same facts
        /// <see cref="FavorService"/> banks from. Null when a commission that needs a win was not
        /// won, because then nothing scores.
        /// </summary>
        public CommissionFinal? FinalFor(int seat, AchievementMatchFacts final)
        {
            Commission? commission = CommissionCatalog.Find(CommissionId);
            if (commission is null) return null;
            if (commission.NeedsWin && final.Result != SeatResult.Win) return null;
            int score = commission.Score(final);
            return new CommissionFinal(commission.Id, score,
                commission.Qualifies(score) && commission.Beats(score, BestFor(seat)));
        }

        private static async Task<int?> BestAsync(IFavorStore store, int reign, AccountId account, CancellationToken ct)
        {
            if (!account.IsAttributed) return null;
            try
            {
                return await store.BestAsync(reign, account.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[king] reign {0} best for player_id={1} was not read: {2}",
                    reign, account.Value, ex.Message);
                return null;
            }
        }
    }
}
