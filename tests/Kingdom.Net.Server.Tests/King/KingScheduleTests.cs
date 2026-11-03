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
            int cycle = CommissionIds.Launch.Count;
            var seen = Enumerable.Range(1, cycle).Select(n => KingSchedule.For(n).Commission).ToList();
            Assert.That(seen.Distinct().Count(), Is.EqualTo(cycle));
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
