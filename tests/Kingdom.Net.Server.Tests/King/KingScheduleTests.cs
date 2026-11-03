using System.Collections.Generic;
using System.Linq;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.King;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    [TestFixture]
    public sealed class KingScheduleTests
    {
        /// <summary>The schedule as it stood when Reign 1 was crowned (2026-11-03): one epoch, the
        /// launch nine, 30 epithets and 30 fates. Literal, so a change to a constant cannot move it.</summary>
        private static readonly KingEpoch[] AsCrowned =
        {
            new KingEpoch(1, new[]
            {
                "overkill", "technically", "self_starter", "waste_not", "out_of_ideas",
                "too_many_ideas", "posthumous", "return_to_sender", "signature_move",
            }, 30, 30),
        };

        [Test]
        public void Reign_one_is_the_King_already_crowned()
        {
            // #443 adds an epoch from Reign 2. Reign 1 has reigned, so nothing about it may move.
            Assert.That(KingSchedule.For(1), Is.EqualTo(new Reign(1, 0, 0, "overkill")));
            Assert.That(KingSchedule.For(1), Is.EqualTo(KingSchedule.For(1, AsCrowned)));
        }

        [Test]
        public void Growing_only_the_commissions_leaves_every_epithet_and_fate_where_it_was()
        {
            // Reign 2 indexing its pools from 0 would crown Reign 1's epithet and fate twice running.
            for (int n = 1; n <= 120; n++)
            {
                Reign now = KingSchedule.For(n), crowned = KingSchedule.For(n, AsCrowned);
                Assert.That((now.Epithet, now.Fate), Is.EqualTo((crowned.Epithet, crowned.Fate)), "reign " + n);
            }
        }

        [Test]
        public void From_reign_two_the_twelve_rotate_in_the_spec_tables_order()
            => Assert.That(Enumerable.Range(1, 14).Select(n => KingSchedule.For(n).Commission), Is.EqualTo(new[]
            {
                "overkill",
                "technically", "self_starter", "comeback", "the_long_game", "waste_not", "out_of_ideas",
                "too_many_ideas", "posthumous", "return_to_sender", "signature_move", "overgrown",
                "overkill", "technically",
            }));

        [Test]
        public void Reigns_two_and_three_name_what_the_crowned_schedule_named()
        {
            // So a deploy landing after Reign 2 has begun rewrites nothing a player has already seen.
            for (int n = 2; n <= 3; n++)
                Assert.That(KingSchedule.For(n), Is.EqualTo(KingSchedule.For(n, AsCrowned)), "reign " + n);
        }

        [Test]
        public void Adding_an_epoch_never_changes_an_earlier_reign()
        {
            string[] twelve = Enumerable.Range(0, 12).Select(i => "c" + i).ToArray();
            var grown = new List<KingEpoch>(KingSchedule.Epochs) { new KingEpoch(201, twelve, 40, 40) };

            for (int n = 1; n <= 200; n++)
                Assert.That(KingSchedule.For(n, grown), Is.EqualTo(KingSchedule.For(n)), "reign " + n);

            Assert.That(KingSchedule.For(201, grown).Commission, Is.EqualTo("c0"));
            Assert.That(KingSchedule.For(202, grown).Commission, Is.EqualTo("c1"));
        }

        [Test]
        public void Every_reign_within_a_pool_cycle_differs()
        {
            int cycle = CommissionIds.FromReignTwo.Count;
            var seen = Enumerable.Range(2, cycle).Select(n => KingSchedule.For(n).Commission).ToList();
            Assert.That(seen.Distinct().Count(), Is.EqualTo(cycle));
            Assert.That(seen, Is.EquivalentTo(CommissionIds.FromReignTwo));
        }

        [Test]
        public void Epithets_and_fates_do_not_repeat_within_their_pools()
        {
            var kings = Enumerable.Range(1, CommissionIds.EpithetCount).Select(KingSchedule.For).ToList();
            Assert.That(kings.Select(k => k.Epithet).Distinct().Count(), Is.EqualTo(CommissionIds.EpithetCount));
            Assert.That(kings.Select(k => k.Fate).Distinct().Count(), Is.EqualTo(CommissionIds.FateCount));
            Assert.That(kings.All(k => k.Epithet >= 0 && k.Epithet < CommissionIds.EpithetCount
                                       && k.Fate >= 0 && k.Fate < CommissionIds.FateCount), Is.True);
        }

        [Test]
        public void Reign_one_starts_every_pool_at_its_first_entry()
            => Assert.That(KingSchedule.For(1), Is.EqualTo(new Reign(1, 0, 0, CommissionIds.Launch[0])));

        [Test]
        public void The_launch_commissions_are_the_nine_ids()
            => Assert.That(CommissionIds.Launch, Is.EqualTo(new[]
            {
                "overkill", "technically", "self_starter", "waste_not", "out_of_ideas",
                "too_many_ideas", "posthumous", "return_to_sender", "signature_move",
            }));
    }
}
