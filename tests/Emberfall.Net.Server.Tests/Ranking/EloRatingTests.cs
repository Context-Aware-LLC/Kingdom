using System;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Ranking;
using Emberfall.Net.Server.Tests.Support;
using Emberfall.Net.Server.Telemetry;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Ranking
{
    [TestFixture]
    public sealed class EloRatingTests
    {
        [Test]
        public void Equal_provisional_players_exchange_twenty_points()
        {
            var (p0, p1) = EloRating.Calculate(1_000, 0, 1_000, 0, MatchOutcome.Player0Win);

            Assert.That(p0, Is.EqualTo(20));
            Assert.That(p1, Is.EqualTo(-20));
        }

        [Test]
        public void Upsets_are_worth_more_than_expected_wins()
        {
            var favorite = EloRating.Calculate(1_200, 25, 800, 25, MatchOutcome.Player0Win);
            var upset = EloRating.Calculate(1_200, 25, 800, 25, MatchOutcome.Player1Win);

            Assert.That(favorite.p0Delta, Is.GreaterThan(0).And.LessThan(5));
            Assert.That(upset.p1Delta, Is.GreaterThan(20));
        }

        [Test]
        public void Draw_moves_the_lower_player_up_and_stays_zero_sum()
        {
            var (high, low) = EloRating.Calculate(1_300, 30, 900, 30, MatchOutcome.Draw);

            Assert.That(high, Is.LessThan(0));
            Assert.That(low, Is.GreaterThan(0));
            Assert.That(high + low, Is.Zero);
        }

        [Test]
        public async Task Store_applies_a_match_once_and_orders_the_table()
        {
            var store = new InMemoryRatingStore();
            AccountId ash = AccountId.FromAuthenticatedAccount("ash");
            AccountId cinder = AccountId.FromAuthenticatedAccount("cinder");
            var result = Result("ranked-1", ash, "Ash", cinder, "Cinder", MatchOutcome.Player0Win);

            RatingChange? first = await store.ApplyAsync(result);
            RatingChange? duplicate = await store.ApplyAsync(result);
            var table = await store.GetLeaderboardAsync(20);

            Assert.That(first, Is.Not.Null);
            Assert.That(duplicate, Is.Null);
            Assert.That(table, Has.Count.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(table[0].DisplayName, Is.EqualTo("Ash"));
                Assert.That(table[0].Rating, Is.EqualTo(1_020));
                Assert.That(table[0].Wins, Is.EqualTo(1));
                Assert.That(table[1].Rating, Is.EqualTo(980));
                Assert.That(table[1].Losses, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task Bot_soak_void_and_same_account_results_never_move_the_ladder()
        {
            var store = new InMemoryRatingStore();
            AccountId ash = AccountId.FromAuthenticatedAccount("ash");
            AccountId cinder = AccountId.FromAuthenticatedAccount("cinder");

            await store.ApplyAsync(Result("bot", ash, "Ash", cinder, "Cinder",
                MatchOutcome.Player0Win, TelemetrySource.BotFill));
            await store.ApplyAsync(Result("soak", ash, "Ash", cinder, "Cinder",
                MatchOutcome.Player0Win, TelemetrySource.AiSoak));
            await store.ApplyAsync(Result("unfinished", ash, "Ash", cinder, "Cinder",
                MatchOutcome.InProgress));
            await store.ApplyAsync(Result("self", ash, "Ash", ash, "Ash",
                MatchOutcome.Player0Win));

            Assert.That(await store.GetLeaderboardAsync(20), Is.Empty);
            Assert.That(await store.GetRatingAsync(ash), Is.EqualTo(1_000));
        }

        [Test]
        public async Task Decided_match_session_records_one_ranked_result()
        {
            var store = new InMemoryRatingStore();
            var h = new SessionHarness(attributed: true, rankedMatchSink: store);
            h.ConfirmMulligans();

            h.Post(1, h.Client1.Intent(new ConcedeIntent(h.P1), h.Id));

            var table = await store.GetLeaderboardAsync(20);
            Assert.That(table, Has.Count.EqualTo(2));
            Assert.Multiple(() =>
            {
                Assert.That(table[0].DisplayName, Is.EqualTo("p0"));
                Assert.That(table[0].Wins, Is.EqualTo(1));
                Assert.That(table[1].DisplayName, Is.EqualTo("p1"));
                Assert.That(table[1].Losses, Is.EqualTo(1));
            });
        }

        private static RankedMatchResult Result(
            string match,
            AccountId p0,
            string p0Name,
            AccountId p1,
            string p1Name,
            MatchOutcome outcome,
            TelemetrySource source = TelemetrySource.Ladder)
            => new RankedMatchResult(
                new MatchId(match),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                p0,
                p0Name,
                p1,
                p1Name,
                outcome,
                source);
    }
}
