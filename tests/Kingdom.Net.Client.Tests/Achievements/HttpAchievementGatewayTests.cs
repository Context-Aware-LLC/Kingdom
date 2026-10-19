using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.Achievements;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Achievements
{
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpAchievementGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";

        private const string TwoEarned =
            "{\"status\":\"known\",\"achievements\":[" +
            "{\"id\":\"second_thoughts\",\"playGamesId\":\"CgkI-second\",\"earnedAt\":\"2026-10-18T12:00:00Z\"}," +
            "{\"id\":\"against_advice\",\"playGamesId\":\"\",\"earnedAt\":\"2026-10-18T12:00:00Z\"}]}";

        [Test]
        public async Task Fetches_what_the_signed_in_player_has_earned()
        {
            var http = new FakeAuthHttp().Reply(200, TwoEarned);

            AchievementSnapshot snapshot = await new HttpAchievementGateway(Base, http).FetchAsync("jwt-token");

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Status, Is.EqualTo(AchievementFetchStatus.Known));
                Assert.That(snapshot.Achievements.Select(a => a.Id),
                    Is.EqualTo(new[] { "second_thoughts", "against_advice" }));
                // The Ledger dates each page with this.
                Assert.That(snapshot.Achievements[0].EarnedAt,
                    Is.EqualTo(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)));
                Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/achievements"));
                Assert.That(http.Requests.Single().Headers.Any(
                    h => h.Key == "Authorization" && h.Value == "Bearer jwt-token"), Is.True);
            });
        }

        [Test]
        public async Task The_Ledger_lists_every_entry_and_marks_what_was_earned()
        {
            AchievementSnapshot snapshot =
                await new HttpAchievementGateway(Base, new FakeAuthHttp().Reply(200, TwoEarned)).FetchAsync("jwt");

            IReadOnlyList<LedgerEntry> ledger = Ledger.Of(snapshot.Achievements);

            Assert.That(ledger.Select(e => e.Text.Id), Is.EqualTo(Kingdom.Net.Protocol.AchievementTexts.All.Select(t => t.Id)));
            Assert.That(ledger.Where(e => e.Earned).Select(e => e.Text.Id),
                Is.EqualTo(new[] { "second_thoughts", "against_advice" }));
            Assert.That(ledger.Where(e => !e.Earned).All(e => e.EarnedAt == null), Is.True);
        }

        [Test]
        public async Task Only_earned_achievements_with_a_Play_Games_id_are_unlocked_and_each_once()
        {
            AchievementSnapshot snapshot =
                await new HttpAchievementGateway(Base, new FakeAuthHttp().Reply(200, TwoEarned)).FetchAsync("jwt");

            Assert.That(snapshot.PlayGamesIdsToUnlock(new HashSet<string>()), Is.EqualTo(new[] { "CgkI-second" }),
                "an achievement with no console entry yet has nothing to unlock");
            Assert.That(snapshot.PlayGamesIdsToUnlock(new HashSet<string> { "CgkI-second" }), Is.Empty,
                "an id already unlocked this session is not sent again");
        }

        [Test]
        public async Task A_signed_out_device_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            AchievementSnapshot snapshot = await new HttpAchievementGateway(Base, http).FetchAsync("");

            Assert.That(snapshot.Status, Is.EqualTo(AchievementFetchStatus.Unauthenticated));
            Assert.That(http.Requests, Is.Empty);
        }

        [TestCase(401, AchievementFetchStatus.Unauthenticated)]
        [TestCase(503, AchievementFetchStatus.Unavailable)]
        public async Task A_failed_read_unlocks_nothing(int statusCode, AchievementFetchStatus expected)
        {
            var http = new FakeAuthHttp().Reply(statusCode, "{}");

            AchievementSnapshot snapshot = await new HttpAchievementGateway(Base, http).FetchAsync("jwt");

            Assert.That(snapshot.Status, Is.EqualTo(expected));
            Assert.That(snapshot.PlayGamesIdsToUnlock(new HashSet<string>()), Is.Empty);
        }

        [Test]
        public async Task A_row_without_its_ids_rejects_the_whole_snapshot()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"known\",\"achievements\":[{\"earnedAt\":\"2026-10-18T12:00:00Z\"}]}");

            AchievementSnapshot snapshot = await new HttpAchievementGateway(Base, http).FetchAsync("jwt");

            Assert.That(snapshot.Status, Is.EqualTo(AchievementFetchStatus.Malformed));
            Assert.That(snapshot.Achievements, Is.Empty);
        }
    }
}
