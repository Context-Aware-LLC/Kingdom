using System;
using System.Linq;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Telemetry;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary>
    /// The commissions' scoring rules. A wrong one here pays a prize for the wrong number, so each
    /// commission is pinned to the one fact the spec's table names.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CommissionCatalogTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        /// <summary>Every scored fact set to a different value, so a rule reading the wrong one shows.</summary>
        private static readonly AchievementMatchFacts Distinct = new AchievementMatchFacts(
            "match-1", "player", Now, TelemetrySource.Ladder, SeatResult.Win, MatchEndReason.Win,
            TurnCount: 9, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0,
            FinalHealth: 11, CardsBurned: 12, SelfDamage: 13, OverkillBest: 14, UnspentMana: 15,
            FatigueDamage: 16, TollDamage: 17, StolenDamage: 18, HeroPowerDamage: 19);

        [Test]
        public void Every_scheduled_commission_has_a_rule()
        {
            foreach (string id in CommissionIds.Launch)
                Assert.That(CommissionCatalog.Find(id), Is.Not.Null, id);
        }

        [Test]
        public void An_unknown_id_has_no_rule()
            => Assert.That(CommissionCatalog.Find("comeback"), Is.Null);

        [Test]
        public void The_board_direction_is_the_commissions_and_an_unknown_id_sorts_highest_first()
        {
            Assert.Multiple(() =>
            {
                Assert.That(CommissionCatalog.LowerIsBetter(CommissionIds.Technically), Is.True);
                Assert.That(CommissionCatalog.LowerIsBetter(CommissionIds.Overkill), Is.False);
                Assert.That(CommissionCatalog.LowerIsBetter("comeback"), Is.False);
            });
        }

        [TestCase(CommissionIds.Technically, 11)]
        [TestCase(CommissionIds.TooManyIdeas, 12)]
        [TestCase(CommissionIds.SelfStarter, 13)]
        [TestCase(CommissionIds.Overkill, 14)]
        [TestCase(CommissionIds.WasteNot, 15)]
        [TestCase(CommissionIds.OutOfIdeas, 16)]
        [TestCase(CommissionIds.Posthumous, 17)]
        [TestCase(CommissionIds.ReturnToSender, 18)]
        [TestCase(CommissionIds.SignatureMove, 19)]
        public void Each_commission_scores_its_own_number(string id, int expected)
            => Assert.That(CommissionCatalog.Find(id)!.Score(Distinct), Is.EqualTo(expected));

        [Test]
        public void Only_Technically_is_lower_is_better()
            => Assert.That(CommissionCatalog.All.Where(c => c.LowerIsBetter).Select(c => c.Id),
                Is.EqualTo(new[] { CommissionIds.Technically }));

        [Test]
        public void The_win_only_commissions_are_the_spec_table_s()
            => Assert.That(CommissionCatalog.All.Where(c => c.NeedsWin).Select(c => c.Id),
                Is.EquivalentTo(new[]
                {
                    CommissionIds.Technically, CommissionIds.SelfStarter, CommissionIds.WasteNot,
                    CommissionIds.OutOfIdeas, CommissionIds.TooManyIdeas,
                }));
    }
}
