using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.Quests;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Quests
{
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpQuestGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";

        [Test]
        public async Task Fetches_the_signed_in_players_flat_quest_rows()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"known\",\"playerId\":\"p1\",\"quests\":[" +
                "{\"id\":\"play-three\",\"title\":\"Play 3 matches\",\"goal\":3," +
                "\"progress\":1,\"rewardGold\":60,\"day\":\"2026-09-19\",\"state\":\"active\"}," +
                "{\"id\":\"win-two\",\"title\":\"Win 2 matches\",\"goal\":2," +
                "\"progress\":2,\"rewardGold\":60,\"day\":\"2026-09-18\",\"state\":\"complete\"}]}");

            QuestSnapshot snapshot = await new HttpQuestGateway(Base, http).FetchAsync("jwt-token");

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Status, Is.EqualTo(QuestFetchStatus.Known));
                Assert.That(snapshot.Quests, Has.Count.EqualTo(2));
                Assert.That(snapshot.Quests[0].Title, Is.EqualTo("Play 3 matches"));
                Assert.That(snapshot.Quests[0].Progress, Is.EqualTo(1));
                Assert.That(snapshot.Quests[0].Goal, Is.EqualTo(3));
                Assert.That(snapshot.Quests[0].RewardGold, Is.EqualTo(60));
                Assert.That(snapshot.Quests[1].Complete, Is.True);
                Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/quests"));
                Assert.That(http.Requests.Single().Headers.Any(
                    h => h.Key == "Authorization" && h.Value == "Bearer jwt-token"), Is.True);
            });
        }

        [Test]
        public async Task A_signed_out_device_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            QuestSnapshot snapshot = await new HttpQuestGateway(Base, http).FetchAsync("");

            Assert.That(snapshot.Status, Is.EqualTo(QuestFetchStatus.Unauthenticated));
            Assert.That(http.Requests, Is.Empty);
        }

        [TestCase(401, QuestFetchStatus.Unauthenticated)]
        [TestCase(503, QuestFetchStatus.Unavailable)]
        public async Task Expected_server_failures_stay_distinct_from_an_empty_quest_day(
            int statusCode, QuestFetchStatus expected)
        {
            var http = new FakeAuthHttp().Reply(statusCode, "{}");

            QuestSnapshot snapshot = await new HttpQuestGateway(Base, http).FetchAsync("jwt");

            Assert.That(snapshot.Status, Is.EqualTo(expected));
            Assert.That(snapshot.Quests, Is.Empty);
        }

        [Test]
        public async Task A_malformed_row_rejects_the_whole_snapshot()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"known\",\"quests\":[" +
                "{\"id\":\"play-three\",\"title\":\"Play 3 matches\",\"goal\":3," +
                "\"progress\":99,\"rewardGold\":60,\"day\":\"2026-09-19\",\"state\":\"active\"}]}");

            QuestSnapshot snapshot = await new HttpQuestGateway(Base, http).FetchAsync("jwt");

            Assert.That(snapshot.Status, Is.EqualTo(QuestFetchStatus.Malformed));
            Assert.That(snapshot.Quests, Is.Empty);
        }
    }
}
