using System.Threading;

namespace Kingdom.Net.Server.Bots
{
    /// <summary>
    /// Every learned bot seat's decisions, summed across the server and reported on
    /// <c>/healthz</c>. This is where <see cref="LearnedBotSeatPolicy"/>'s fallback count goes:
    /// a seat's own count dies with its match, and a learned bot that quietly plays Greedy is the
    /// failure that wrapper exists to make loud. Zero fallbacks is the expected value.
    /// </summary>
    internal sealed class LearnedBotCounters
    {
        private long _decisions;
        private long _fallbacks;

        /// <summary>Moves the model chose.</summary>
        public long Decisions => Interlocked.Read(ref _decisions);

        /// <summary>Moves Greedy chose because the model could not.</summary>
        public long Fallbacks => Interlocked.Read(ref _fallbacks);

        internal void Served() => Interlocked.Increment(ref _decisions);
        internal void FellBack() => Interlocked.Increment(ref _fallbacks);
    }
}
