namespace Emberfall.Net.Server
{
    /// <summary>
    /// The session layer's only clock (integer Unix-epoch milliseconds). The CORE stays timeless
    /// (dev-docs/architecture/system-overview.md:18) — this feeds MatchClock deadlines and the token bucket,
    /// never the rules. Injectable so every timer path is deterministic under test.
    /// </summary>
    public interface ITimeSource
    {
        long NowMs { get; }
    }

    /// <summary>Wall clock for the real host (commit 2 wires this in).</summary>
    public sealed class SystemTimeSource : ITimeSource
    {
        public long NowMs => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
