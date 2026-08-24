using System;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// NOR-109 §3 + §7 — the rarity distribution, over a large seeded sample.
    ///
    /// <para><b>The published odds are the source of truth and this fixture is their witness.</b> It
    /// prints its observed counts rather than only asserting them, so a run that goes red says what it
    /// actually saw; and it asserts against the numbers in the brief, not against
    /// <see cref="PackRarityOdds.Default"/>, so retuning the table cannot silently retune the test with
    /// it.</para>
    ///
    /// <para><b>Tolerance.</b> One million draws puts the standard error on Legendary (p = 0.01) at
    /// about 0.1 permille, so the ±3 permille band below is ~30σ on the tightest band and wider still
    /// on the others: it cannot flake, and it still catches a real mistuning — a swapped Epic/Legendary
    /// weight moves 35 permille.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class BoosterPackOddsTests
    {
        private const int Draws = 1_000_000;
        private const int TolerancePermille = 3;

        // The ratified numbers, transcribed independently of the shipped table so that re-pricing
        // the economy cannot happen silently — every other assertion here reads PackRarityOdds and
        // would follow a retune without complaint.
        //
        // RETUNED by Sam 2026-08-20, from the NOR-109 brief's 715/230/45/10, and confirmed
        // 2026-08-24 as the canonical baseline. Epic was the lever: at 4.5% a player broadened too
        // slowly across a 27-card epic tier while epic pity kept forcing slots onto the handful
        // they already held. The 7.5pp it and Rare gained came off Common.
        private const int BriefCommon = 640;      // 64%
        private const int BriefRare = 265;        // 26.5%
        private const int BriefEpic = 80;         // 8%
        private const int BriefLegendary = 15;    // 1.5%

        [Test]
        public void Rarity_Draw_Matches_The_Published_Odds_Within_Tolerance()
        {
            var rng = new DeterministicRandom(0xB0057E12345UL);
            var counts = new Dictionary<Rarity, int>
            {
                [Rarity.COMMON] = 0,
                [Rarity.RARE] = 0,
                [Rarity.EPIC] = 0,
                [Rarity.LEGENDARY] = 0,
            };

            for (int i = 0; i < Draws; i++) counts[PackRarityOdds.Default.Roll(rng)]++;

            var expected = new Dictionary<Rarity, int>
            {
                [Rarity.COMMON] = BriefCommon,
                [Rarity.RARE] = BriefRare,
                [Rarity.EPIC] = BriefEpic,
                [Rarity.LEGENDARY] = BriefLegendary,
            };

            TestContext.Out.WriteLine($"rarity draw distribution over {Draws:N0} seeded draws:");
            foreach (var rarity in expected.Keys)
            {
                long permille = (long)counts[rarity] * PackRarityOdds.Scale / Draws;
                TestContext.Out.WriteLine(
                    $"  {rarity,-9} {counts[rarity],9:N0}  {permille,4}‰  (expected {expected[rarity]}‰)");
            }

            foreach (var rarity in expected.Keys)
            {
                long permille = (long)counts[rarity] * PackRarityOdds.Scale / Draws;
                Assert.That(permille, Is.EqualTo(expected[rarity]).Within(TolerancePermille),
                    $"{rarity} drew {permille}‰, expected {expected[rarity]}‰.");
            }
        }

        [Test]
        public void Shipped_Table_Is_The_Brief_Table()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PackRarityOdds.Default.CommonPermille, Is.EqualTo(BriefCommon));
                Assert.That(PackRarityOdds.Default.RarePermille, Is.EqualTo(BriefRare));
                Assert.That(PackRarityOdds.Default.EpicPermille, Is.EqualTo(BriefEpic));
                Assert.That(PackRarityOdds.Default.LegendaryPermille, Is.EqualTo(BriefLegendary));
            });
        }

        /// <summary>A table that does not sum is an authoring mistake, and must not be silently rescaled.</summary>
        [Test]
        public void Weights_That_Do_Not_Sum_To_Scale_Are_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new PackRarityOdds(700, 230, 45, 10));
            Assert.Throws<ArgumentException>(() => new PackRarityOdds(800, 230, 45, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackRarityOdds(1010, -10, 0, 0));
        }

        /// <summary>
        /// A zero-weight rarity must be <b>unreachable</b>, not merely unlikely. This is the band-walk's
        /// off-by-one guard: a <c>&lt;=</c> anywhere in <see cref="PackRarityOdds.Roll"/> would let a
        /// weightless rarity absorb its band's boundary value and drop out of a pack once every
        /// thousand slots, which no distribution assertion at this tolerance would catch.
        /// </summary>
        [Test]
        public void A_Zero_Weight_Rarity_Is_Never_Drawn()
        {
            var commonsOnly = new PackRarityOdds(PackRarityOdds.Scale, 0, 0, 0);
            var rng = new DeterministicRandom(4242UL);
            for (int i = 0; i < 200_000; i++)
                Assert.That(commonsOnly.Roll(rng), Is.EqualTo(Rarity.COMMON));

            var legendariesOnly = new PackRarityOdds(0, 0, 0, PackRarityOdds.Scale);
            var rng2 = new DeterministicRandom(4242UL);
            for (int i = 0; i < 200_000; i++)
                Assert.That(legendariesOnly.Roll(rng2), Is.EqualTo(Rarity.LEGENDARY));
        }

        /// <summary>
        /// One <see cref="DeterministicRandom.NextInt"/> per slot — the RNG call count is part of the
        /// determinism contract, so a second draw hidden in a branch would shift every later slot and
        /// invalidate the fixtures. Compared against <c>NextInt</c> rather than <c>NextUInt64</c>
        /// because rejection sampling inside <c>NextInt</c> may legitimately consume more than one
        /// 64-bit value — deterministically, and identically for both cursors.
        /// </summary>
        [Test]
        public void Roll_Consumes_Exactly_One_NextInt()
        {
            var rng = new DeterministicRandom(31337UL);
            var twin = new DeterministicRandom(31337UL);

            for (int i = 0; i < 10_000; i++)
            {
                PackRarityOdds.Default.Roll(rng);
                twin.NextInt(PackRarityOdds.Scale);
                Assert.That(rng.RawState, Is.EqualTo(twin.RawState),
                    $"Roll #{i} moved the cursor by something other than one NextInt.");
            }
        }
    }
}
