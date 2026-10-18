using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Social;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Identity
{
    /// <summary>
    /// Seamless restore (#425). The failures worth a test: restoring an account the Play Games
    /// profile is not linked to, and restoring one whose address now names somebody else.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class PlayGamesRestoreTests
    {
        /// <summary>Google: "code-for:ann" opens Ann's Play Games profile, g-ann.</summary>
        private sealed class FakeGoogle : IPlayGamesApi
        {
            public Task<PlayGamesSession> OpenAsync(string authCode, bool withFriends, CancellationToken ct = default)
                => Task.FromResult(authCode.StartsWith("code-for:")
                    ? new PlayGamesSession(PlayGamesCallStatus.Ok, "g-" + authCode.Substring("code-for:".Length), null)
                    : new PlayGamesSession(PlayGamesCallStatus.CodeRefused, null, null));
        }

        /// <summary>Supabase's users, by address.</summary>
        private sealed class FakeAdmin : ISupabaseAdmin
        {
            public readonly Dictionary<string, string> UserIdByEmail = new Dictionary<string, string>();
            public readonly List<string> Asked = new List<string>();

            public Task<SignInToken> IssueSignInTokenAsync(string email, CancellationToken ct = default)
            {
                Asked.Add(email);
                return Task.FromResult(UserIdByEmail.TryGetValue(email, out string? id)
                    ? new SignInToken(SignInTokenStatus.Issued, "hash-for-" + id, "recovery", id)
                    : new SignInToken(SignInTokenStatus.NoSuchUser));
            }
        }

        private FakeTime _time = null!;
        private InMemoryAccountStore _accounts = null!;
        private InMemoryPlayGamesLinkStore _links = null!;
        private FakeAdmin _admin = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new FakeTime();
            _accounts = new InMemoryAccountStore(_time);
            _links = new InMemoryPlayGamesLinkStore();
            _admin = new FakeAdmin();
        }

        private PlayGamesRestoreEndpoint Endpoint(ISupabaseAdmin? admin = null, int limit = 10)
            => new PlayGamesRestoreEndpoint(new FakeGoogle(), _links, _accounts, admin ?? _admin,
                new FriendRequestLimiter(_time, max: limit, windowMs: 60_000));

        /// <summary>A Supabase user with an account here, linked to a Play Games profile.</summary>
        private async Task<Account> LinkedAsync(string who)
        {
            Account account = await _accounts.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of("user-" + who), who + "@example.com");
            _admin.UserIdByEmail[who + "@example.com"] = "user-" + who;
            await _links.LinkAsync(account.PlayerId, "g-" + who);
            return account;
        }

        [Test]
        public async Task A_Linked_Profile_Restores_Its_Own_Account()
        {
            await LinkedAsync("ann");

            PlayGamesRestoreResult result = await Endpoint().RestoreAsync("code-for:ann", "1.2.3.4");

            Assert.That(result.Status, Is.EqualTo(PlayGamesRestoreStatus.Restored));
            Assert.That(result.TokenHash, Is.EqualTo("hash-for-user-ann"));
            Assert.That(result.TokenType, Is.EqualTo("recovery"));
            Assert.That(_admin.Asked, Is.EqualTo(new[] { "ann@example.com" }));
        }

        [Test]
        public async Task An_Unlinked_Profile_Restores_Nothing_And_Never_Reaches_Supabase()
        {
            await LinkedAsync("ann");

            PlayGamesRestoreResult result = await Endpoint().RestoreAsync("code-for:bob", "1.2.3.4");

            Assert.That(result.Status, Is.EqualTo(PlayGamesRestoreStatus.NotLinked));
            Assert.That(result.HttpStatus, Is.EqualTo(404));
            Assert.That(result.TokenHash, Is.Empty);
            Assert.That(_admin.Asked, Is.Empty);
        }

        [Test]
        public async Task A_Code_Google_Refuses_Restores_Nothing()
        {
            await LinkedAsync("ann");

            PlayGamesRestoreResult result = await Endpoint().RestoreAsync("g-ann", "1.2.3.4");

            Assert.That(result.Status, Is.EqualTo(PlayGamesRestoreStatus.Refused),
                "the id itself is not a code: a client cannot name the profile it restores");
            Assert.That(_admin.Asked, Is.Empty);
        }

        [Test]
        public async Task An_Address_That_Now_Names_Another_User_Restores_Nobody()
        {
            await LinkedAsync("ann");
            _admin.UserIdByEmail["ann@example.com"] = "user-someone-else";

            PlayGamesRestoreResult result = await Endpoint().RestoreAsync("code-for:ann", "1.2.3.4");

            Assert.That(result.Status, Is.EqualTo(PlayGamesRestoreStatus.NotLinked));
            Assert.That(result.TokenHash, Is.Empty);
        }

        [Test]
        public async Task An_Address_Supabase_No_Longer_Has_Restores_Nobody()
        {
            await LinkedAsync("ann");
            _admin.UserIdByEmail.Remove("ann@example.com");

            PlayGamesRestoreResult result = await Endpoint().RestoreAsync("code-for:ann", "1.2.3.4");

            Assert.That(result.Status, Is.EqualTo(PlayGamesRestoreStatus.NotLinked));
        }

        [Test]
        public async Task The_Account_That_Last_Linked_The_Profile_Is_The_One_Restored()
        {
            await LinkedAsync("ann");
            Account second = await _accounts.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of("user-ann2"), "ann2@example.com");
            _admin.UserIdByEmail["ann2@example.com"] = "user-ann2";
            await _links.LinkAsync(second.PlayerId, "g-ann");

            PlayGamesRestoreResult result = await Endpoint().RestoreAsync("code-for:ann", "1.2.3.4");

            Assert.That(result.TokenHash, Is.EqualTo("hash-for-user-ann2"));
        }

        [Test]
        public async Task Restores_Are_Rate_Limited_Per_Address()
        {
            await LinkedAsync("ann");
            PlayGamesRestoreEndpoint endpoint = Endpoint(limit: 2);

            await endpoint.RestoreAsync("code-for:ann", "1.2.3.4");
            await endpoint.RestoreAsync("code-for:ann", "1.2.3.4");
            PlayGamesRestoreResult third = await endpoint.RestoreAsync("code-for:ann", "1.2.3.4");
            PlayGamesRestoreResult elsewhere = await endpoint.RestoreAsync("code-for:ann", "5.6.7.8");

            Assert.That(third.Status, Is.EqualTo(PlayGamesRestoreStatus.RateLimited));
            Assert.That(third.HttpStatus, Is.EqualTo(429));
            Assert.That(elsewhere.Status, Is.EqualTo(PlayGamesRestoreStatus.Restored));
        }

        [Test]
        public async Task Without_Supabase_Admin_The_Route_Is_Off()
        {
            await LinkedAsync("ann");
            var endpoint = new PlayGamesRestoreEndpoint(new FakeGoogle(), _links, _accounts, admin: null,
                new FriendRequestLimiter(_time));

            PlayGamesRestoreResult result = await endpoint.RestoreAsync("code-for:ann", "1.2.3.4");

            Assert.That(result.Status, Is.EqualTo(PlayGamesRestoreStatus.Unavailable));
            Assert.That(result.HttpStatus, Is.EqualTo(503));
        }

        // ── the route on the real app: the JSON PlayGamesRestoreGateway parses ──────────

        [Test, CancelAfter(30_000), NonParallelizable]
        public async Task The_Route_Answers_A_Token_Only_For_A_Linked_Profile()
        {
            await LinkedAsync("ann");
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                AccountStore = _accounts,
                PlayGames = new FakeGoogle(),
                PlayGamesLinks = _links,
                SupabaseAdmin = _admin,
            });
            await app.StartAsync();
            try
            {
                using var http = new System.Net.Http.HttpClient { BaseAddress = new System.Uri(System.Linq.Enumerable.First(app.Urls)) };

                async Task<(HttpStatusCode, JsonDocument, HttpResponseMessage)> Post(string code)
                {
                    HttpResponseMessage response = await http.PostAsync("/play-games/restore",
                        new StringContent($"{{\"authCode\":\"{code}\"}}", Encoding.UTF8, "application/json"));
                    return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()), response);
                }

                var (okStatus, ok, okResponse) = await Post("code-for:ann");
                Assert.That(okStatus, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(ok.RootElement.GetProperty("status").GetString(), Is.EqualTo("restored"));
                Assert.That(ok.RootElement.GetProperty("tokenHash").GetString(), Is.EqualTo("hash-for-user-ann"));
                Assert.That(ok.RootElement.GetProperty("tokenType").GetString(), Is.EqualTo("recovery"));
                Assert.That(okResponse.Headers.CacheControl?.NoStore, Is.True, "the token is a credential");

                var (noStatus, no, _) = await Post("code-for:bob");
                Assert.That(noStatus, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(no.RootElement.GetProperty("status").GetString(), Is.EqualTo("notlinked"));
                Assert.That(no.RootElement.TryGetProperty("tokenHash", out _), Is.False);
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        // ── SupabaseAdmin, against a scripted HTTP answer ───────────────────────────────

        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public HttpRequestMessage? Request;
            public string RequestBody = "";

            public ScriptedHandler(HttpStatusCode status, string body)
            {
                _status = status;
                _body = body;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Request = request;
                RequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
            }
        }

        [Test]
        public async Task The_Admin_Call_Asks_For_A_Recovery_Link_With_The_Secret_Key()
        {
            var handler = new ScriptedHandler(HttpStatusCode.OK,
                "{\"id\":\"user-ann\",\"email\":\"ann@example.com\",\"hashed_token\":\"abc\",\"verification_type\":\"recovery\"}");
            var admin = new SupabaseAdmin(new HttpClient(handler), "https://project.supabase.co/", "sb_secret_x");

            SignInToken token = await admin.IssueSignInTokenAsync("ann@example.com");

            Assert.That(token, Is.EqualTo(new SignInToken(SignInTokenStatus.Issued, "abc", "recovery", "user-ann")));
            Assert.That(handler.Request!.RequestUri!.ToString(),
                Is.EqualTo("https://project.supabase.co/auth/v1/admin/generate_link"));
            Assert.That(handler.Request.Headers.GetValues("apikey"), Is.EqualTo(new[] { "sb_secret_x" }));
            using JsonDocument body = JsonDocument.Parse(handler.RequestBody);
            Assert.That(body.RootElement.GetProperty("type").GetString(), Is.EqualTo("recovery"),
                "a magic link for an unknown address is a sign-up; a recovery link is a 404");
            Assert.That(body.RootElement.GetProperty("email").GetString(), Is.EqualTo("ann@example.com"));
        }

        [TestCase(HttpStatusCode.NotFound, SignInTokenStatus.NoSuchUser)]
        [TestCase(HttpStatusCode.Unauthorized, SignInTokenStatus.Unavailable)]
        [TestCase(HttpStatusCode.InternalServerError, SignInTokenStatus.Unavailable)]
        public async Task A_Refused_Admin_Call_Issues_No_Token(HttpStatusCode status, SignInTokenStatus expected)
        {
            var admin = new SupabaseAdmin(new HttpClient(new ScriptedHandler(status, "{}")),
                "https://project.supabase.co", "sb_secret_x");

            SignInToken token = await admin.IssueSignInTokenAsync("ann@example.com");

            Assert.That(token.Status, Is.EqualTo(expected));
            Assert.That(token.TokenHash, Is.Empty);
        }
    }
}
