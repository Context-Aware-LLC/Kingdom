using System;

namespace Kingdom.Net.Server.King
{
    /// <summary>Which reign a moment falls in. A reign is one week from Tuesday 00:00 UTC.</summary>
    public static class KingCalendar
    {
        public const long WeekMs = 7L * 24 * 60 * 60 * 1000;

        /// <summary>The reign number at <paramref name="nowMs"/>; 0 before Reign 1 begins.</summary>
        public static int ReignAt(long nowMs, long reignOneMs)
        {
            if (nowMs < reignOneMs) return 0;
            return (int)((nowMs - reignOneMs) / WeekMs) + 1;
        }

        /// <summary>The instant <paramref name="reign"/> begins, in unix ms.</summary>
        public static long StartMs(int reign, long reignOneMs) => reignOneMs + (reign - 1) * WeekMs;

        /// <summary>True when the instant is exactly Tuesday 00:00:00.000 UTC.</summary>
        public static bool IsTuesdayMidnightUtc(long ms)
        {
            DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return at.UtcDateTime.DayOfWeek == DayOfWeek.Tuesday && at.UtcDateTime.TimeOfDay == TimeSpan.Zero;
        }
    }
}
