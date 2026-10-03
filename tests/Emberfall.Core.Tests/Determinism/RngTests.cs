using NUnit.Framework;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Determinism
{
    /// <summary>L1 — splitmix64 RNG: portability + raw-state exposure (dev-docs/architecture/determinism-and-projection.md:9).</summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class RngTests
    {
        // splitmix64 reference vector: seed 0 → first three outputs (Vigna's reference implementation).
        [Test]
        public void Splitmix64_Matches_Reference_Vector_For_Seed_Zero()
        {
            var rng = new DeterministicRandom(0UL);
            Assert.That(rng.NextUInt64(), Is.EqualTo(0xE220A8397B1DCDAFUL));
            Assert.That(rng.NextUInt64(), Is.EqualTo(0x6E789E6AA1B965F4UL));
            Assert.That(rng.NextUInt64(), Is.EqualTo(0x06C45D188009454FUL));
        }

        [Test]
        public void Same_Seed_Produces_Identical_Sequence()
        {
            var a = new DeterministicRandom(123456789UL);
            var b = new DeterministicRandom(123456789UL);
            for (int i = 0; i < 1000; i++)
                Assert.That(a.NextUInt64(), Is.EqualTo(b.NextUInt64()));
            Assert.That(a.RawState, Is.EqualTo(b.RawState), "Raw cursors must match after identical draws.");
        }

        [Test]
        public void RawState_Advances_And_Round_Trips()
        {
            var rng = new DeterministicRandom(42UL);
            rng.NextUInt64();
            ulong saved = rng.RawState;
            ulong nextFromLive = rng.NextUInt64();

            var resumed = new DeterministicRandom(0);
            resumed.SetRawState(saved);
            Assert.That(resumed.NextUInt64(), Is.EqualTo(nextFromLive),
                "Restoring RawState must reproduce the exact next draw (mid-match resume).");
        }

        [Test]
        public void NextInt_Stays_In_Range_And_Is_Unbiased_Enough()
        {
            var rng = new DeterministicRandom(7UL);
            var counts = new int[6];
            for (int i = 0; i < 60000; i++)
            {
                int v = rng.NextInt(6);
                Assert.That(v, Is.InRange(0, 5));
                counts[v]++;
            }
            foreach (var c in counts)
                Assert.That(c, Is.InRange(9000, 11000), "Distribution should be roughly uniform.");
        }

        /// <summary>
        /// <c>Substream(seed, n)</c> is seeded by the n-th OUTPUT of the base stream, never by its n-th
        /// cursor — still splitmix64 and still O(1), so it stays inside the M1 algorithm lock.
        /// </summary>
        [Test]
        public void Substream_Is_Seeded_By_The_Base_Streams_Nth_Output()
        {
            const ulong Seed = 0xDEADBEEFCAFEF00DUL;
            var walked = new DeterministicRandom(Seed);

            for (int n = 0; n < 64; n++)
            {
                var expected = new DeterministicRandom(walked.NextUInt64());
                var sub = DeterministicRandom.Substream(Seed, n);
                Assert.That(sub.NextUInt64(), Is.EqualTo(expected.NextUInt64()), $"Substream({n}) diverged.");
            }
        }

        /// <summary>
        /// The defect this replaced: seeding by the cursor made index n+1 the same stream one draw
        /// later, so neighbours shared every value but one. No value in one sub-stream's window may
        /// appear in its neighbour's.
        /// </summary>
        [Test]
        public void Neighbouring_Substreams_Share_No_Draws()
        {
            const ulong Seed = 12345UL;
            const int Window = 32;

            for (int n = 0; n < 64; n++)
            {
                var here = new System.Collections.Generic.HashSet<ulong>();
                var a = DeterministicRandom.Substream(Seed, n);
                for (int i = 0; i < Window; i++) here.Add(a.NextUInt64());

                var b = DeterministicRandom.Substream(Seed, n + 1);
                for (int i = 0; i < Window; i++)
                    Assert.That(here.Contains(b.NextUInt64()), Is.False,
                        $"Substream({n + 1}) draw {i} repeats a draw of Substream({n}).");
            }
        }

        [Test]
        public void Shuffle_Is_Deterministic_For_A_Seed()
        {
            var listA = new System.Collections.Generic.List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            var listB = new System.Collections.Generic.List<int>(listA);
            new DeterministicRandom(99UL).Shuffle(listA);
            new DeterministicRandom(99UL).Shuffle(listB);
            Assert.That(listA, Is.EqualTo(listB));
        }
    }
}
