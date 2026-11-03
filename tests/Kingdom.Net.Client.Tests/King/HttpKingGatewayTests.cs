using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.King;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.King
{
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpKingGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";

        // The shapes ServerApp's GET /king and GET /king/roll write: camelCase, compact, a JSON null
        // for a reign with no Favorite.
        private const string Reigning =
            "{\"status\":\"ok\",\"reign\":12,\"epithet\":17,\"fate\":1,\"commission\":\"waste_not\",\"endsAtMs\":1794268800000}";

        private const string Roll =
            "{\"status\":\"ok\",\"reigns\":[" +
            "{\"reign\":3,\"epithet\":14,\"fate\":22,\"favorite\":null}," +
            "{\"reign\":2,\"epithet\":7,\"fate\":11,\"favorite\":\"Lady Grimsby\"}," +
            "{\"reign\":1,\"epithet\":0,\"fate\":0,\"favorite\":null}]}";

        [Test]
        public async Task No_King_reads_as_null()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"none\"}");

            KingNow? king = await new HttpKingGateway(Base, http).FetchKingAsync(default);

            Assert.That(king, Is.Null);
            Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/king"));
            Assert.That(http.Requests.Single().Verb, Is.EqualTo("GET"));
        }

        [Test]
        public async Task A_reigning_King_maps_every_field()
        {
            var http = new FakeAuthHttp().Reply(200, Reigning);

            KingNow? king = await new HttpKingGateway(Base + "/", http).FetchKingAsync(default);

            Assert.That(king, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(king!.Reign, Is.EqualTo(12));
                Assert.That(king.Epithet, Is.EqualTo(17));
                Assert.That(king.Fate, Is.EqualTo(1));
                Assert.That(king.Commission, Is.EqualTo("waste_not"));
                Assert.That(king.EndsAtMs, Is.EqualTo(1794268800000L));
                Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/king"),
                    "a trailing slash on the base is not doubled");
            });
        }

        [Test]
        public async Task A_transport_failure_reads_as_null()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure();

            Assert.That(await new HttpKingGateway(Base, http).FetchKingAsync(default), Is.Null);
        }

        [TestCase(500, "{\"status\":\"ok\",\"reign\":1}")]
        [TestCase(503, "{}")]
        [TestCase(200, "<html>not json</html>")]
        [TestCase(200, "{\"status\":\"ok\",\"epithet\":1,\"fate\":1,\"commission\":\"overkill\",\"endsAtMs\":1}")]
        [TestCase(200, "{\"status\":\"ok\",\"reign\":0,\"epithet\":1,\"fate\":1,\"commission\":\"overkill\",\"endsAtMs\":1}")]
        [TestCase(200, "{\"status\":\"ok\",\"reign\":1,\"epithet\":-1,\"fate\":1,\"commission\":\"overkill\",\"endsAtMs\":1}")]
        [TestCase(200, "{\"status\":\"ok\",\"reign\":1,\"epithet\":1,\"fate\":1,\"endsAtMs\":1}")]
        public async Task A_failed_or_unreadable_answer_reads_as_null(int status, string body)
        {
            var http = new FakeAuthHttp().Reply(status, body);

            Assert.That(await new HttpKingGateway(Base, http).FetchKingAsync(default), Is.Null);
        }

        [Test]
        public async Task The_Roll_maps_every_reign_newest_first()
        {
            var http = new FakeAuthHttp().Reply(200, Roll);

            IReadOnlyList<RollEntry> roll = await new HttpKingGateway(Base, http).FetchRollAsync(default);

            Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/king/roll"));
            Assert.That(roll.Select(r => r.Reign), Is.EqualTo(new[] { 3, 2, 1 }));
            Assert.Multiple(() =>
            {
                Assert.That(roll[0].Epithet, Is.EqualTo(14));
                Assert.That(roll[0].Fate, Is.EqualTo(22));
                Assert.That(roll[0].Favorite, Is.Null);
                Assert.That(roll[1].Epithet, Is.EqualTo(7));
                Assert.That(roll[1].Fate, Is.EqualTo(11));
                Assert.That(roll[1].Favorite, Is.EqualTo("Lady Grimsby"));
                Assert.That(roll[2].Favorite, Is.Null);
            });
        }

        // PlayerName allows "null". A JSON null and a player of that name are different answers, and
        // a reader that keeps only unquoted text cannot tell them apart.
        [Test]
        public async Task A_Favorite_named_null_is_a_name_not_an_absence()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"ok\",\"reigns\":[{\"reign\":2,\"epithet\":7,\"fate\":11,\"favorite\":\"null\"}," +
                "{\"reign\":1,\"epithet\":0,\"fate\":0,\"favorite\":null}]}");

            IReadOnlyList<RollEntry> roll = await new HttpKingGateway(Base, http).FetchRollAsync(default);

            Assert.That(roll[0].Favorite, Is.EqualTo("null"));
            Assert.That(roll[1].Favorite, Is.Null);
        }

        [TestCase(200, "{\"status\":\"none\"}")]
        [TestCase(500, "{}")]
        [TestCase(200, "{\"status\":\"ok\"}")]
        [TestCase(200, "{\"status\":\"ok\",\"reigns\":[{\"epithet\":0,\"fate\":0,\"favorite\":null}]}")]
        public async Task A_Roll_that_is_absent_failed_or_unreadable_is_empty(int status, string body)
        {
            var http = new FakeAuthHttp().Reply(status, body);

            Assert.That(await new HttpKingGateway(Base, http).FetchRollAsync(default), Is.Empty);
        }

        [Test]
        public async Task A_Roll_transport_failure_is_empty()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure();

            Assert.That(await new HttpKingGateway(Base, http).FetchRollAsync(default), Is.Empty);
        }

        // The server sends "" for a Favorite with no name to show (FavorNames); null is no Favorite.
        [Test]
        public async Task An_unnamed_Favorite_is_empty_not_absent()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"ok\",\"reigns\":[{\"reign\":2,\"epithet\":7,\"fate\":11,\"favorite\":\"\"}," +
                "{\"reign\":1,\"epithet\":0,\"fate\":0,\"favorite\":null}]}");

            IReadOnlyList<RollEntry> roll = await new HttpKingGateway(Base, http).FetchRollAsync(default);

            Assert.That(roll[0].Favorite, Is.EqualTo(""));
            Assert.That(roll[1].Favorite, Is.Null);
        }

        // ── GET /king/favor ──────────────────────────────────────────────────────────────

        private const string Board =
            "{\"status\":\"ok\",\"reign\":2,\"commission\":\"technically\",\"lowerIsBetter\":true,\"rows\":[" +
            "{\"place\":1,\"name\":\"Lady Grimsby\",\"score\":1}," +
            "{\"place\":2,\"name\":\"\",\"score\":3}," +
            "{\"place\":3,\"name\":\"null\",\"score\":-2}]}";

        [Test]
        public async Task The_board_maps_every_row_in_order()
        {
            var http = new FakeAuthHttp().Reply(200, Board);

            FavorBoard? board = await new HttpKingGateway(Base, http).FetchFavorAsync(default);

            Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/king/favor"));
            Assert.That(board, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(board!.Reign, Is.EqualTo(2));
                Assert.That(board.Commission, Is.EqualTo("technically"));
                Assert.That(board.LowerIsBetter, Is.True);
                Assert.That(board.Rows.Select(r => (r.Place, r.Name, r.Score)),
                    Is.EqualTo(new[] { (1, "Lady Grimsby", 1), (2, "", 3), (3, "null", -2) }),
                    "an empty name is the placeholder's cue; a player named null is a name");
            });
        }

        [Test]
        public async Task An_empty_board_is_a_board()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"ok\",\"reign\":1,\"commission\":\"overkill\",\"lowerIsBetter\":false,\"rows\":[]}");

            FavorBoard? board = await new HttpKingGateway(Base, http).FetchFavorAsync(default);

            Assert.That(board, Is.Not.Null);
            Assert.That(board!.Rows, Is.Empty);
            Assert.That(board.LowerIsBetter, Is.False);
        }

        [TestCase(200, "{\"status\":\"none\"}")]
        [TestCase(500, "{}")]
        [TestCase(200, "{\"status\":\"ok\",\"reign\":1,\"commission\":\"overkill\",\"lowerIsBetter\":false}")]
        [TestCase(200, "{\"status\":\"ok\",\"reign\":1,\"commission\":\"overkill\",\"rows\":[]}")]
        [TestCase(200, "{\"status\":\"ok\",\"reign\":1,\"commission\":\"overkill\",\"lowerIsBetter\":false,\"rows\":[{\"place\":1,\"score\":3}]}")]
        [TestCase(200, "{\"status\":\"ok\",\"reign\":1,\"commission\":\"overkill\",\"lowerIsBetter\":false,\"rows\":[{\"place\":0,\"name\":\"a\",\"score\":3}]}")]
        public async Task A_board_that_is_absent_failed_or_unreadable_is_null(int status, string body)
        {
            var http = new FakeAuthHttp().Reply(status, body);

            Assert.That(await new HttpKingGateway(Base, http).FetchFavorAsync(default), Is.Null);
        }

        // ── GET /king/favor/me ───────────────────────────────────────────────────────────

        [Test]
        public async Task The_standing_maps_place_score_and_seals_and_sends_the_bearer()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"ok\",\"place\":101,\"score\":-1,\"seals\":[\"overkill\",\"technically\"]}");

            FavorStanding? me = await new HttpKingGateway(Base, http).FetchStandingAsync("tok", default);

            Assert.That(me, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(me!.Place, Is.EqualTo(101));
                Assert.That(me.Score, Is.EqualTo(-1));
                Assert.That(me.Seals, Is.EqualTo(new[] { "overkill", "technically" }));
                Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/king/favor/me"));
                Assert.That(http.Requests.Single().Headers,
                    Has.Member(new KeyValuePair<string, string>("Authorization", "Bearer tok")));
            });
        }

        [Test]
        public async Task A_player_off_the_board_has_no_place_but_keeps_seals()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"ok\",\"place\":null,\"score\":null,\"seals\":[\"waste_not\"]}");

            FavorStanding? me = await new HttpKingGateway(Base, http).FetchStandingAsync("tok", default);

            Assert.That(me, Is.Not.Null);
            Assert.That(me!.Place, Is.Null);
            Assert.That(me.Score, Is.Null);
            Assert.That(me.Seals, Is.EqualTo(new[] { "waste_not" }));
        }

        [TestCase(200, "{\"status\":\"none\"}")]
        [TestCase(401, "{\"status\":\"unauthenticated\",\"place\":null,\"score\":null,\"seals\":[]}")]
        [TestCase(503, "{\"status\":\"unavailable\",\"place\":null,\"score\":null,\"seals\":[]}")]
        [TestCase(200, "{\"status\":\"ok\",\"place\":null,\"score\":null}")]
        [TestCase(200, "{\"status\":\"ok\",\"place\":null,\"score\":null,\"seals\":[3]}")]
        [TestCase(200, "{\"status\":\"ok\",\"place\":0,\"score\":4,\"seals\":[]}")]
        public async Task A_standing_that_is_absent_failed_or_unreadable_is_null(int status, string body)
        {
            var http = new FakeAuthHttp().Reply(status, body);

            Assert.That(await new HttpKingGateway(Base, http).FetchStandingAsync("tok", default), Is.Null);
        }

        [Test]
        public async Task No_token_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            Assert.That(await new HttpKingGateway(Base, http).FetchStandingAsync("", default), Is.Null);
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task An_unconfigured_gateway_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();
            var gateway = new HttpKingGateway("", http);

            Assert.That(await gateway.FetchKingAsync(default), Is.Null);
            Assert.That(await gateway.FetchRollAsync(default), Is.Empty);
            Assert.That(await gateway.FetchFavorAsync(default), Is.Null);
            Assert.That(await gateway.FetchStandingAsync("tok", default), Is.Null);
            Assert.That(http.Requests, Is.Empty);
        }
    }
}
