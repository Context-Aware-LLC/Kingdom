using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// Proves the harness produces a debuggable <see cref="ReplayDivergenceReport"/> on mismatch
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §6) — a bare "hash mismatch" is useless for fixing a desync. We corrupt the
    /// expected block of a real fixture IN MEMORY (never on disk) and assert the report points at the
    /// first divergence and at the RNG-cursor-bearing hash.
    /// </summary>
    [TestFixture]
    [Category("GoldenReplay")]
    public sealed class DivergenceReportTests
    {
        [SetUp]
        public void SkipDuringRebaseline()
        {
            // In rebaseline mode the fixtures are mid-bake (expected block may be empty), so these
            // corrupt-and-assert tests are not meaningful. They run normally in assert mode.
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("EMBERFALL_REBASELINE")))
                Assert.Ignore("Skipped during EMBERFALL_REBASELINE bake.");
        }

        private static ReplayFixture LoadOpening()
            => ReplayFixture.Load(Path.Combine(RepoPaths.FixturesDir, "opening_pyra_maren.replay.json"));

        [Test]
        public void Event_Stream_Divergence_Names_First_Bad_Sequence()
        {
            var fx = LoadOpening();
            Assert.That(fx.ExpectedEvents, Is.Not.Null);

            // Corrupt the first expected event's Kind so the stream diverges at sequence 0.
            var bad = new List<ExpectedEvent>(fx.ExpectedEvents!);
            var first = bad[0];
            bad[0] = new ExpectedEvent(first.Seq, VfxEvent.onDeath, first.Src, first.Tgt, first.Amt, first.Flags);
            fx.ExpectedEvents = bad;

            var res = ReplayHarness.Run(fx);

            Assert.That(res.Passed, Is.False);
            Assert.That(res.Report, Is.Not.Null);
            Assert.That(res.Report!.EventStreamDiverged, Is.True);
            Assert.That(res.Report.ToString(), Does.Contain("First divergent event"));
        }

        [Test]
        public void State_Hash_Divergence_Is_Reported_When_Stream_Matches()
        {
            var fx = LoadOpening();
            // Keep the event stream correct; corrupt only the hash → isolates the §6.2 path.
            fx.ExpectedStateHash = "deadbeef" + new string('0', 56);

            var res = ReplayHarness.Run(fx);

            Assert.That(res.Passed, Is.False);
            Assert.That(res.Report, Is.Not.Null);
            Assert.That(res.Report!.StateHashDiverged, Is.True);
            Assert.That(res.Report.EventStreamDiverged, Is.False, "Only the hash was corrupted; the stream should still match.");
            Assert.That(res.Report.ActualHash, Is.Not.EqualTo(res.Report.ExpectedHash));
        }
    }
}
