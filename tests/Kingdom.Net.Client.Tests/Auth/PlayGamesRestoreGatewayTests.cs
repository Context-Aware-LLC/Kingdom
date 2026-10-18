using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Auth
{
    [TestFixture]
    [Category("Unit")]
    public sealed class PlayGamesRestoreGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";

        [Test]
        public async Task A_linked_profile_comes_back_with_the_token_to_redeem()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"status\":\"restored\",\"tokenHash\":\"hashed-token\",\"tokenType\":\"recovery\"}");

            PlayGamesRestore restore = await new PlayGamesRestoreGateway(Base, http).RestoreAsync("4/0Aserver-code");

            Assert.Multiple(() =>
            {
                Assert.That(restore.Status, Is.EqualTo(PlayGamesRestoreStatus.Restored));
                Assert.That(restore.TokenHash, Is.EqualTo("hashed-token"));
                Assert.That(restore.TokenType, Is.EqualTo("recovery"));
                Assert.That(http.Requests.Single().Url, Is.EqualTo(Base + "/play-games/restore"));
                Assert.That(http.Requests.Single().Body, Is.EqualTo("{\"authCode\":\"4/0Aserver-code\"}"));
                Assert.That(http.Requests.Single().Headers.Any(h => h.Key == "Authorization"), Is.False,
                    "asked before sign-in: there is no bearer to send");
            });
        }

        [TestCase(404, "{\"status\":\"notlinked\"}", PlayGamesRestoreStatus.NotLinked)]
        [TestCase(401, "{\"status\":\"refused\"}", PlayGamesRestoreStatus.Refused)]
        [TestCase(429, "{\"status\":\"ratelimited\"}", PlayGamesRestoreStatus.RateLimited)]
        [TestCase(503, "{\"status\":\"unavailable\"}", PlayGamesRestoreStatus.Unavailable)]
        [TestCase(502, "<html>bad gateway</html>", PlayGamesRestoreStatus.Unavailable)]
        [TestCase(200, "{\"status\":\"restored\"}", PlayGamesRestoreStatus.Malformed)]
        public async Task Anything_but_a_restore_carries_no_token(int code, string body, PlayGamesRestoreStatus expected)
        {
            PlayGamesRestore restore = await new PlayGamesRestoreGateway(Base, new FakeAuthHttp().Reply(code, body))
                .RestoreAsync("code");

            Assert.That(restore.Status, Is.EqualTo(expected));
            Assert.That(restore.TokenHash, Is.Empty);
        }

        [Test]
        public async Task No_auth_code_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            PlayGamesRestore restore = await new PlayGamesRestoreGateway(Base, http).RestoreAsync("");

            Assert.That(restore.Status, Is.EqualTo(PlayGamesRestoreStatus.Refused));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task A_dropped_connection_is_offline()
        {
            PlayGamesRestore restore = await new PlayGamesRestoreGateway(Base, new FakeAuthHttp().ReplyTransportFailure())
                .RestoreAsync("code");

            Assert.That(restore.Status, Is.EqualTo(PlayGamesRestoreStatus.Offline));
        }
    }
}
