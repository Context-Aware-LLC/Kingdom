using System;
using System.Threading;
using System.Threading.Tasks;
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
