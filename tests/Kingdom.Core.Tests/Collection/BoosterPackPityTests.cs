using System;
using System.Collections.Generic;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// NOR-109 §4 + §7 — every guarantee fires <b>exactly</b> at its boundary and not one pack early.
    ///
    /// <para>Each rule is pinned twice: once on <see cref="PackPityState"/>, where the boundary is
    /// arithmetic, and once through a real <see cref="BoosterPackOpener.Open"/>, where it has to
    /// survive the slot-selection rules as well. The first tells you which side of the boundary broke;
    /// the second tells you it actually reaches the player.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class BoosterPackPityTests
    {
        private const ulong Seed = 0x5EED_0000_0000_0001UL;

        private static BoosterPack Pack => new BoosterPack(SetId.PENDINGHAM);
        private static ICardDatabase Db => BoosterPackFixture.Db(SetId.PENDINGHAM, perRarity: 40);

        // ── the Rare floor ───────────────────────────────────────────────────────────────────

        /// <summary>§4: every pack contains at least one Rare-or-better. Not "almost every".</summary>
        [Test]
        public void Every_Pack_Contains_A_Rare_Or_Better()
        {
            var pool = Pack.Pool(Db);
            var pity = PackPityState.Fresh;
            int floored = 0;

            for (int i = 0; i < 5000; i++)
            {
                var result = BoosterPackOpener.Open(Pack, pool, new OwnedCardCounts(), pity, Seed, i);

                bool rareOrBetter = false;
                foreach (var a in result.Awards) if (a.Rarity >= Rarity.RARE) rareOrBetter = true;
                Assert.That(rareOrBetter, Is.True, $"Pack {i} was all Commons: {Describe(result)}");

                foreach (var a in result.Awards) if (a.Upgrade == PackUpgrade.RareFloor) floored++;
            }

            // 0.715^5 ≈ 18.7% of packs roll all-Common, so roughly 900 of 5000 need the floor. Zero
            // would mean the fixture never exercised the branch it exists to cover.
            TestContext.Out.WriteLine($"rare-floor upgrades over 5000 fresh packs: {floored}");
            Assert.That(floored, Is.GreaterThan(0), "Fixture never hit an all-Common pack — it proves nothing.");
        }

        [Test]
        public void The_Rare_Floor_Upgrades_Exactly_One_Slot()
        {
            var pool = Pack.Pool(Db);
            int inspected = 0;

            for (int i = 0; i < 5000 && inspected < 20; i++)
            {
                var result = BoosterPackOpener.Open(Pack, pool, new OwnedCardCounts(), PackPityState.Fresh, Seed, i);

                int upgraded = 0, rareOrBetter = 0;
                foreach (var a in result.Awards)
                {
                    if (a.Upgrade == PackUpgrade.RareFloor) upgraded++;
                    if (a.Rarity >= Rarity.RARE) rareOrBetter++;
                }
                if (upgraded == 0) continue;

                inspected++;
                Assert.That(upgraded, Is.EqualTo(1), $"Pack {i} floored more than one slot: {Describe(result)}");
                Assert.That(rareOrBetter, Is.EqualTo(1),
                    $"Pack {i} was floored despite already holding a Rare+: {Describe(result)}");
            }

            Assert.That(inspected, Is.GreaterThan(0), "No floored pack found to inspect.");
        }

        // ── Epic pity ────────────────────────────────────────────────────────────────────────

        [Test]
        public void Epic_Pity_Is_Due_On_The_Tenth_Pack_And_Not_The_Ninth()
        {
            // Legendary clocks parked well clear so only the Epic rule is under test.
            Assert.That(new PackPityState(30, 8, 5).EpicDueNextPack, Is.False, "Due one pack early.");
            Assert.That(new PackPityState(30, 9, 5).EpicDueNextPack, Is.True, "Not due at the boundary.");
            Assert.That(PackPityState.EpicPityPacks, Is.EqualTo(10), "The brief's window is 10 packs.");
        }

        [Test]
        public void The_Tenth_Epicless_Pack_Contains_An_Epic()
        {
            var pool = Pack.Pool(Db);
            var due = new PackPityState(30, PackPityState.EpicPityPacks - 1, 5);
            var notDue = new PackPityState(30, PackPityState.EpicPityPacks - 2, 5);

            int forced = 0;
            for (int i = 0; i < 200; i++)
            {
                var result = BoosterPackOpener.Open(Pack, pool, new OwnedCardCounts(), due, Seed, i);
                Assert.That(HasRarity(result, Rarity.EPIC), Is.True,
                    $"Pity pack {i} had no Epic: {Describe(result)}");
                if (HasUpgrade(result, PackUpgrade.EpicPity)) forced++;
            }
            Assert.That(forced, Is.GreaterThan(0), "Every pity pack rolled an Epic naturally — nothing was proved.");

            // One pack earlier the guarantee must be off: some packs are Epicless.
            int epicless = 0;
            for (int i = 0; i < 200; i++)
                if (!HasRarity(BoosterPackOpener.Open(Pack, pool, new OwnedCardCounts(), notDue, Seed, i), Rarity.EPIC))
                    epicless++;

            TestContext.Out.WriteLine($"epic-pity forced {forced}/200 at the boundary; {epicless}/200 packs Epicless one pack earlier");
            Assert.That(epicless, Is.GreaterThan(0), "The guarantee fired a pack early.");
        }

        // ── Legendary: the first window ──────────────────────────────────────────────────────

        [Test]
        public void A_First_Legendary_Is_Due_By_The_Tenth_Pack_Ever_Opened()
        {
            Assert.That(new PackPityState(8, 3, 8).LegendaryDueNextPack, Is.False, "Due one pack early.");
            Assert.That(new PackPityState(9, 3, 9).LegendaryDueNextPack, Is.True, "Not due at the boundary.");
            Assert.That(PackPityState.LegendaryFirstWindowPacks, Is.EqualTo(10), "The brief's window is 10 packs.");
        }

        /// <summary>
        /// The end-to-end statement of the first-window rule: walk a brand-new player through ten packs,
        /// advancing the real counters between each, and a Legendary must have landed by the tenth.
        /// Run over many independent seeds so it is the <i>rule</i> being asserted and not one lucky
        /// stream.
        /// </summary>
        [Test]
        public void A_New_Player_Sees_A_Legendary_Within_Their_First_Ten_Packs()
        {
            var pool = Pack.Pool(Db);

            for (ulong seed = 1; seed <= 40; seed++)
            {
                var pity = PackPityState.Fresh;
                var owned = new OwnedCardCounts();
                int firstLegendaryPack = -1;

                for (int i = 0; i < PackPityState.LegendaryFirstWindowPacks; i++)
                {
                    var result = BoosterPackOpener.Open(Pack, pool, owned, pity, seed, pity.PacksOpened);
                    if (firstLegendaryPack < 0 && HasRarity(result, Rarity.LEGENDARY)) firstLegendaryPack = i + 1;
                    Apply(owned, result);
                    pity = result.PityAfter;
                }

                Assert.That(firstLegendaryPack, Is.InRange(1, PackPityState.LegendaryFirstWindowPacks),
                    $"Seed {seed}: no Legendary in the first {PackPityState.LegendaryFirstWindowPacks} packs.");
            }
        }

        // ── Legendary: the standing pity ─────────────────────────────────────────────────────

        [Test]
        public void After_The_First_Legendary_Pity_Moves_To_Forty()
        {
            Assert.That(new PackPityState(100, 2, 38).LegendaryDueNextPack, Is.False, "Due one pack early.");
            Assert.That(new PackPityState(100, 2, 39).LegendaryDueNextPack, Is.True, "Not due at the boundary.");
            Assert.That(PackPityState.LegendaryPityPacks, Is.EqualTo(40), "The brief's window is 40 packs.");
        }

        [Test]
        public void The_Fortieth_Legendaryless_Pack_Contains_A_Legendary()
        {
            var pool = Pack.Pool(Db);
            var due = new PackPityState(100, 2, PackPityState.LegendaryPityPacks - 1);

            int forced = 0;
            for (int i = 0; i < 200; i++)
            {
                var result = BoosterPackOpener.Open(Pack, pool, new OwnedCardCounts(), due, Seed, i);
                Assert.That(HasRarity(result, Rarity.LEGENDARY), Is.True,
                    $"Pity pack {i} had no Legendary: {Describe(result)}");
                if (HasUpgrade(result, PackUpgrade.LegendaryPity)) forced++;
            }
            TestContext.Out.WriteLine($"legendary-pity forced {forced}/200 at the 40-pack boundary");
            Assert.That(forced, Is.GreaterThan(0));
        }

        /// <summary>Both clocks maturing on the same pack must deliver both, not collapse into one slot.</summary>
        [Test]
        public void A_Pack_That_Owes_Both_Delivers_Both()
        {
            var pool = Pack.Pool(Db);
            var due = new PackPityState(45, PackPityState.EpicPityPacks - 1, PackPityState.LegendaryPityPacks - 1);
            Assert.That(due.EpicDueNextPack && due.LegendaryDueNextPack, Is.True, "Fixture state does not owe both.");

            for (int i = 0; i < 200; i++)
            {
                var result = BoosterPackOpener.Open(Pack, pool, new OwnedCardCounts(), due, Seed, i);
                Assert.That(HasRarity(result, Rarity.LEGENDARY), Is.True, $"Pack {i}: {Describe(result)}");
                Assert.That(HasRarity(result, Rarity.EPIC), Is.True, $"Pack {i}: {Describe(result)}");
                Assert.That(CountUpgrade(result, PackUpgrade.LegendaryPity), Is.LessThanOrEqualTo(1));
                Assert.That(CountUpgrade(result, PackUpgrade.EpicPity), Is.LessThanOrEqualTo(1));
            }
        }

        // ── counter arithmetic ───────────────────────────────────────────────────────────────

        [Test]
        public void Counters_Reset_Only_On_The_Rarity_That_Fired()
        {
            var start = new PackPityState(20, 5, 7);

            var epicOnly = start.AfterPack(new List<Rarity> { Rarity.COMMON, Rarity.EPIC });
            Assert.Multiple(() =>
            {
                Assert.That(epicOnly.PacksOpened, Is.EqualTo(21));
                Assert.That(epicOnly.PacksSinceEpic, Is.EqualTo(0));
                Assert.That(epicOnly.PacksSinceLegendary, Is.EqualTo(8), "A Legendary clock must not move on an Epic.");
            });

            // The two clocks are deliberately independent: a Legendary does NOT satisfy the Epic rule.
            var legendaryOnly = start.AfterPack(new List<Rarity> { Rarity.COMMON, Rarity.LEGENDARY });
            Assert.Multiple(() =>
            {
                Assert.That(legendaryOnly.PacksSinceEpic, Is.EqualTo(6));
                Assert.That(legendaryOnly.PacksSinceLegendary, Is.EqualTo(0));
            });
        }

        /// <summary>
        /// The derived "have they ever pulled one" fact must hold at every step of a long walk — it is
        /// what selects between the two Legendary regimes, and nothing stores it.
        /// </summary>
        [Test]
        public void HasEverPulledLegendary_Is_Exact_Across_A_Long_Walk()
        {
            var pool = Pack.Pool(Db);
            var pity = PackPityState.Fresh;
            var owned = new OwnedCardCounts();
            bool sawOne = false;

            for (int i = 0; i < 300; i++)
            {
                Assert.That(pity.HasEverPulledLegendary, Is.EqualTo(sawOne), $"Diverged at pack {i}: {pity}");
                var result = BoosterPackOpener.Open(Pack, pool, owned, pity, Seed, pity.PacksOpened);
                if (HasRarity(result, Rarity.LEGENDARY)) sawOne = true;
                Apply(owned, result);
                pity = result.PityAfter;
            }
            Assert.That(sawOne, Is.True, "300 packs with no Legendary — pity is not firing at all.");
        }

        [Test]
        public void A_Corrupt_Counter_Row_Is_Rejected_Rather_Than_Silently_Accepted()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackPityState(3, 4, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackPityState(3, 0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PackPityState(-1, 0, 0));
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────

        private static bool HasRarity(PackOpenResult r, Rarity rarity)
        {
            foreach (var a in r.Awards) if (a.Rarity == rarity) return true;
            return false;
        }

        private static bool HasUpgrade(PackOpenResult r, PackUpgrade upgrade) => CountUpgrade(r, upgrade) > 0;

        private static int CountUpgrade(PackOpenResult r, PackUpgrade upgrade)
        {
            int n = 0;
            foreach (var a in r.Awards) if (a.Upgrade == upgrade) n++;
            return n;
        }

        private static void Apply(OwnedCardCounts owned, PackOpenResult r)
        {
            foreach (var a in r.Awards)
                owned.Set(a.CardId, owned.CopiesOwned(a.CardId) + 1);
        }

        private static string Describe(PackOpenResult r) => string.Join(", ", r.Awards);
    }
}
