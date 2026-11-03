using System;
using System.Globalization;

namespace Kingdom.Net.Server.King
{
    /// <summary>Reads the King's server settings.</summary>
    public static class KingEnv
    {
        public const string ReignOneVar = "KINGDOM_REIGN_ONE";

        /// <summary>Reign 1's start from <c>KINGDOM_REIGN_ONE</c>, or null (no King).</summary>
        public static long? ReignOneFromEnvironment() => ParseReignOne(KingdomEnv.Get(ReignOneVar));

        /// <summary>
        /// Unset is null: no King. A value that is not an ISO-8601 UTC instant on a Tuesday midnight
        /// is ALSO null, and says so, since a misaligned date would put every reign boundary on the
        /// wrong day for everyone.
        /// </summary>
        public static long? ParseReignOne(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
            {
                Console.Error.WriteLine("[king] {0}='{1}' is not a date; there is no King.", ReignOneVar, raw);
                return null;
            }
            long ms = at.ToUnixTimeMilliseconds();
            if (!KingCalendar.IsTuesdayMidnightUtc(ms))
            {
                Console.Error.WriteLine("[king] {0}='{1}' is not a Tuesday 00:00 UTC; there is no King.",
                    ReignOneVar, raw);
                return null;
            }
            return ms;
        }
    }
}
