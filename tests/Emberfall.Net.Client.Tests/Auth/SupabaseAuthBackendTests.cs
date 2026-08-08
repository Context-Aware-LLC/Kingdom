using System.Threading.Tasks;
using Emberfall.Net.Client.Auth;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests.Auth
{
    /// <summary>
    /// The Supabase call layer (EF-074e / NOR-231): what goes out, and how a reply is classified.
    /// The classification is the load-bearing half — <see cref="AuthFailure.Rejected"/> costs the
    /// player their stored session and <see cref="AuthFailure.Network"/> does not.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SupabaseAuthBackendTests
    {
        private static SupabaseAuthConfig Config() => new SupabaseAuthConfig
        {
            ProjectUrl = "https://project.supabase.co",
            AnonKey = "anon-key",
            RedirectUrl = "emberfall://auth",
        };

        private static SupabaseAuthBackend Backend(FakeAuthHttp http, FakeClock clock)
            => new SupabaseAuthBackend(Config(), http, clock, verifierFactory: () => "fixed-verifier");

        [Test]
        public async Task Magic_link_sends_a_pkce_challenge_and_keeps_the_verifier()
        {
            var http = new FakeAuthHttp().Reply(200, "{}");
            var backend = Backend(http, new FakeClock());

            SignInStart start = await backend.BeginMagicLinkAsync("player@example.com");

            Assert.That(start.Result.Ok, Is.True);
            Assert.That(start.Pending.CodeVerifier, Is.EqualTo("fixed-verifier"));
            Assert.That(http.Requests[0].Url, Does.StartWith("https://project.supabase.co/auth/v1/otp?redirect_to="));
            Assert.That(http.Requests[0].Body, Does.Contain("\"email\":\"player@example.com\""));
            Assert.That(http.Requests[0].Body, Does.Contain("\"code_challenge_method\":\"s256\""));
            // The challenge is the hash, never the verifier itself — sending the verifier would make
            // PKCE prove nothing.
            Assert.That(http.Requests[0].Body, Does.Not.Contain("fixed-verifier"));
        }

        [Test]
        public void Oauth_builds_an_authorize_url_carrying_the_provider_redirect_and_challenge()
        {
            var backend = Backend(new FakeAuthHttp(), new FakeClock());

            SignInStart start = backend.BeginOAuth("google");

            Assert.That(start.Pending.AuthorizeUrl, Does.StartWith("https://project.supabase.co/auth/v1/authorize?"));
            Assert.That(start.Pending.AuthorizeUrl, Does.Contain("provider=google"));
            Assert.That(start.Pending.AuthorizeUrl, Does.Contain("redirect_to=emberfall%3A%2F%2Fauth"));
            Assert.That(start.Pending.AuthorizeUrl, Does.Contain("code_challenge_method=s256"));
            Assert.That(start.Pending.CodeVerifier, Is.EqualTo("fixed-verifier"));
        }

        [Test]
        public async Task Completing_a_sign_in_dates_expiry_against_our_own_clock()
        {
            var clock = new FakeClock(5_000_000);
            var http = new FakeAuthHttp().Reply(200,
                TokenReply.Body(Jwt.ForUser("user-1", "player@example.com"), "refresh-1", expiresInSeconds: 3600));
            var backend = Backend(http, clock);

            AuthResult result = await backend.CompleteAsync(
                new PendingSignIn { CodeVerifier = "fixed-verifier" }, "auth-code");

            Assert.That(result.Ok, Is.True);
            // 3600s from OUR now, not from the reply's expires_at (1893456000, a date in 2030).
            Assert.That(result.Session.ExpiresAtMs, Is.EqualTo(5_000_000 + 3_600_000));
            Assert.That(result.Session.RefreshToken, Is.EqualTo("refresh-1"));
        }

        [Test]
        public async Task Identity_is_read_from_the_jwt_payload_not_the_nested_user_object()
        {
            var http = new FakeAuthHttp().Reply(200,
                TokenReply.Body(Jwt.ForUser("user-1", "player@example.com"), "refresh-1", 3600));
            var backend = Backend(http, new FakeClock());

            AuthResult result = await backend.CompleteAsync(new PendingSignIn { CodeVerifier = "v" }, "code");

            // The reply's user object says nested-id / nested@example.com. Reading top-level-only and
            // taking identity from the token itself is what keeps those from being picked up.
            Assert.That(result.Session.UserId, Is.EqualTo("user-1"));
            Assert.That(result.Session.Email, Is.EqualTo("player@example.com"));
        }

        [Test]
        public async Task A_reply_with_no_refresh_token_is_malformed_not_a_partial_session()
        {
            var http = new FakeAuthHttp().Reply(200,
                "{\"access_token\":\"" + Jwt.ForUser("u", "e@x.com") + "\",\"expires_in\":3600}");
            var backend = Backend(http, new FakeClock());

            AuthResult result = await backend.CompleteAsync(new PendingSignIn { CodeVerifier = "v" }, "code");

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Failure, Is.EqualTo(AuthFailure.Malformed));
            Assert.That(result.Session.IsPresent, Is.False);
        }

        [Test]
        public async Task A_4xx_is_rejected_and_carries_supabase_own_words()
        {
            var http = new FakeAuthHttp().Reply(400, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has expired\"}");
            var backend = Backend(http, new FakeClock());

            AuthResult result = await backend.RefreshAsync("stale-refresh");

            Assert.That(result.Failure, Is.EqualTo(AuthFailure.Rejected));
            Assert.That(result.Detail, Is.EqualTo("Token has expired"));
        }

        [Test]
        public async Task A_5xx_and_a_dead_socket_are_both_network_not_rejected()
        {
            var backend500 = Backend(new FakeAuthHttp().Reply(503, "gateway down"), new FakeClock());
            var backendDead = Backend(new FakeAuthHttp().ReplyTransportFailure(), new FakeClock());

            Assert.That((await backend500.RefreshAsync("refresh")).Failure, Is.EqualTo(AuthFailure.Network));
            Assert.That((await backendDead.RefreshAsync("refresh")).Failure, Is.EqualTo(AuthFailure.Network));
        }

        [Test]
        public async Task An_unconfigured_build_makes_no_request_at_all()
        {
            var http = new FakeAuthHttp();
            var backend = new SupabaseAuthBackend(SupabaseAuthConfig.Unconfigured, http, new FakeClock());

            SignInStart start = await backend.BeginMagicLinkAsync("player@example.com");

            Assert.That(backend.IsConfigured, Is.False);
            Assert.That(start.Result.Failure, Is.EqualTo(AuthFailure.NotConfigured));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task A_code_from_a_sign_in_this_device_never_started_is_refused_locally()
        {
            var http = new FakeAuthHttp();
            var backend = Backend(http, new FakeClock());

            AuthResult result = await backend.CompleteAsync(PendingSignIn.None, "code-from-somewhere-else");

            Assert.That(result.Failure, Is.EqualTo(AuthFailure.Rejected));
            Assert.That(http.Requests, Is.Empty);
        }
    }
}
