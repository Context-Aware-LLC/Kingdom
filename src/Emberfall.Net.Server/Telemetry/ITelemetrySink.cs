using System.Collections.Generic;

namespace Emberfall.Net.Server.Telemetry
{
    /// <summary>
    /// Where match telemetry goes (C-G1-8 step 2). Deliberately a seam and not a file writer: step 2
    /// makes the events EXIST at the right moments, step 3 gives them somewhere durable to live.
    /// Splitting them means the emit points get a real test today without waiting on storage.
    ///
    /// <para><b>Implementations must not throw and must not block.</b> This is called from inside
    /// <c>MatchSession</c>'s single-threaded dispatch (the threading contract on that class): an
    /// exception here would take down a match that has already finished correctly, and a slow write
    /// would stall the pump for every other match on the box. A sink that cannot write should
    /// swallow and drop.</para>
    /// </summary>
    public interface ITelemetrySink
    {
        void MatchComplete(MatchCompleteEvent e);
        void MatchVoid(MatchVoidEvent e);

        /// <summary>
        /// The whole match's per-card sample in one call (telemetry-spec §2). Batched rather than
        /// one call per card precisely because of the "must not block" contract above: ~40–60 rows
        /// arrive at once, and delivering them singly would multiply this seam's cost by the size of
        /// two decks at the exact moment a match is tearing down.
        /// </summary>
        void CardsSeen(CardSeenEvent e);
    }

    /// <summary>The default. Emit points still run and are still tested; nothing is recorded.</summary>
    public sealed class NullTelemetrySink : ITelemetrySink
    {
        public static NullTelemetrySink Instance { get; } = new NullTelemetrySink();
        private NullTelemetrySink() { }
        public void MatchComplete(MatchCompleteEvent e) { }
        public void MatchVoid(MatchVoidEvent e) { }
        public void CardsSeen(CardSeenEvent e) { }
    }

    /// <summary>Test double: keeps every event in order so a test can assert on the emitted row.</summary>
    public sealed class InMemoryTelemetrySink : ITelemetrySink
    {
        private readonly List<MatchCompleteEvent> _completes = new List<MatchCompleteEvent>();
        private readonly List<MatchVoidEvent> _voids = new List<MatchVoidEvent>();
        private readonly List<CardSeenEvent> _cardsSeen = new List<CardSeenEvent>();

        public IReadOnlyList<MatchCompleteEvent> Completes => _completes;
        public IReadOnlyList<MatchVoidEvent> Voids => _voids;
        public IReadOnlyList<CardSeenEvent> CardSeenBatches => _cardsSeen;

        public void MatchComplete(MatchCompleteEvent e) => _completes.Add(e);
        public void MatchVoid(MatchVoidEvent e) => _voids.Add(e);
        public void CardsSeen(CardSeenEvent e) => _cardsSeen.Add(e);
    }
}
