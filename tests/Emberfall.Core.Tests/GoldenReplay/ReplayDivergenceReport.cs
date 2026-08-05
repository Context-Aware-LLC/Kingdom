using System.Collections.Generic;
using System.Text;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// Makes a desync debuggable (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §6). Points at the FIRST point of divergence — a bare
    /// "hash mismatch" is useless for fixing a desync. Both checks always run (§6.3) so a single CI
    /// failure shows the complete picture rather than fixing one and reunearthing the other next push.
    /// </summary>
    public sealed class ReplayDivergenceReport
    {
        public string FixtureName { get; }
        public bool EventStreamDiverged { get; private set; }
        public bool StateHashDiverged { get; private set; }
        public string? ExpectedHash { get; private set; }
        public string? ActualHash { get; private set; }

        private readonly List<string> _lines = new();

        public ReplayDivergenceReport(string fixtureName) => FixtureName = fixtureName;

        public bool HasDivergence => EventStreamDiverged || StateHashDiverged;

        /// <summary>§6.1 — event-stream divergence, checked first (usually the more informative).</summary>
        public void RecordEventDivergence(IReadOnlyList<ExpectedEvent> expected, IReadOnlyList<GameEvent> actual)
        {
            int n = expected.Count < actual.Count ? expected.Count : actual.Count;
            int firstBad = -1;
            for (int i = 0; i < n; i++)
            {
                if (!expected[i].Matches(actual[i])) { firstBad = i; break; }
            }
            if (firstBad < 0 && expected.Count != actual.Count) firstBad = n; // length mismatch

            if (firstBad < 0) return;
            EventStreamDiverged = true;

            _lines.Add($"GOLDEN REPLAY FAILED: {FixtureName}");
            if (firstBad < expected.Count && firstBad < actual.Count)
            {
                _lines.Add($"  First divergent event at Sequence {actual[firstBad].Sequence}:");
                _lines.Add($"    expected: {expected[firstBad]}");
                _lines.Add($"    actual:   {ExpectedEvent.From(actual[firstBad])}");
            }
            else
            {
                _lines.Add($"  Event-stream LENGTH mismatch: expected {expected.Count}, actual {actual.Count} (diverges at index {firstBad}).");
                if (firstBad < expected.Count) _lines.Add($"    next expected: {expected[firstBad]}");
                if (firstBad < actual.Count) _lines.Add($"    next actual:   {ExpectedEvent.From(actual[firstBad])}");
            }

            // Context: last up-to-3 matching events.
            int ctxStart = firstBad - 3 < 0 ? 0 : firstBad - 3;
            var ctx = new StringBuilder("  Context (last matching events):");
            for (int i = ctxStart; i < firstBad && i < actual.Count; i++)
                ctx.Append(' ').Append(ExpectedEvent.From(actual[i]));
            _lines.Add(ctx.ToString());
        }

        /// <summary>§6.2 — state-hash divergence (the field-level diff lives in the harness which has both states).</summary>
        public void RecordStateHashDivergence(string expectedHash, string actualHash, IReadOnlyList<string> fieldDiffs)
        {
            StateHashDiverged = true;
            ExpectedHash = expectedHash;
            ActualHash = actualHash;
            _lines.Add($"  State hash mismatch.  expected={expectedHash}");
            _lines.Add($"                        actual  ={actualHash}");
            if (fieldDiffs.Count > 0)
            {
                _lines.Add("  Field diffs (canonical order):");
                foreach (var d in fieldDiffs) _lines.Add("    " + d);
            }
        }

        public override string ToString() => string.Join("\n", _lines);
    }
}
