using System.Threading.Tasks;
using Emberfall.Net.Client.Auth;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests.Auth
{
    /// <summary>
    /// The six-digit email code — the return path Sam chose on 2026-08-08 (EF-074e / NOR-231),
    /// reaffirmed the same day after a magic-link detour (see SignInGateController's type remarks).
    ///
    /// <para>What these pin is the difference from the redirect flow, because that difference is the
    /// whole reason the code was chosen: nothing is held on this device between asking for a code and
    /// typing it, and a wrong code costs the player nothing but a retype.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class EmailCodeSignInTests
    {
        private static SupabaseAuthConfig Config() => new SupabaseAuthConfig
        {
            ProjectUrl = "https://project.supabase.co",
            AnonKey = "anon-key",
            RedirectUrl = "emberfall://auth",
        };

        private static SupabaseAuthBackend Backend(FakeAuthHttp http, FakeClock? clock = null)
            => new SupabaseAuthBackend(Config(), http, clock ?? new FakeClock(),
                verifierFactory: () => "fixed-verifier");

        // ── Sending the code ────────────────────────────────────────────────────────────

        [Test]
        public async Task Asking_for_a_code_sends_no_pkce_challenge_and_no_redirect()
        {
            var http = new FakeAuthHttp().Reply(200, "{}");

            AuthResult result = await Backend(http).BeginEmailCodeAsync("player@example.com");

            Assert.That(result.Ok, Is.True);
            // No redirect_to: a code the player types never comes back over a deep link, and asking
            // Supabase for one is what would make the mail carry a link the game cannot catch.
            Assert.That(http.Requests[0].Url, Is.EqualTo("https://project.supabase.co/auth/v1/otp"));
            Assert.That(http.Requests[0].Body, Does.Contain("\"email\":\"player@example.com\""));
            Assert.That(http.Requests[0].Body, Does.Not.Contain("code_challenge"));
            Assert.That(http.Requests[0].Body, Does.Contain("\"create_user\":true"));
        }

        [Test]
        public async Task Asking_for_a_code_without_an_email_never_reaches_the_network()
        {
            var http = new FakeAuthHttp();

            AuthResult result = await Backend(http).BeginEmailCodeAsync("   ");

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Failure, Is.EqualTo(AuthFailure.Rejected));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task An_unconfigured_build_reports_not_configured_rather_than_failing_the_request()
        {
            var backend = new SupabaseAuthBackend(SupabaseAuthConfig.Unconfigured, new FakeAuthHttp());

            AuthResult sent = await backend.BeginEmailCodeAsync("player@example.com");
            AuthResult verified = await backend.VerifyEmailCodeAsync("player@example.com", "123456");

            Assert.That(sent.Failure, Is.EqualTo(AuthFailure.NotConfigured));
            Assert.That(verified.Failure, Is.EqualTo(AuthFailure.NotConfigured));
        }

        // ── Redeeming the code ──────────────────────────────────────────────────────────

        [Test]
        public async Task A_correct_code_becomes_a_session_dated_against_our_own_clock()
        {
            var clock = new FakeClock(5_000_000);
            var http = new FakeAuthHttp().Reply(200,
                TokenReply.Body(Jwt.ForUser("user-1", "player@example.com"), "refresh-1", expiresInSeconds: 3600));

            AuthResult result = await Backend(http, clock)
                .VerifyEmailCodeAsync("player@example.com", "123456");

            Assert.That(result.Ok, Is.True);
            Assert.That(result.Session.AccessToken, Is.Not.Empty);
            Assert.That(result.Session.RefreshToken, Is.EqualTo("refresh-1"));
            Assert.That(result.Session.ExpiresAtMs, Is.EqualTo(5_000_000 + 3_600_000));
            // Identity comes off the JWT payload, never off the reply's nested user object.
            Assert.That(result.Session.Email, Is.EqualTo("player@example.com"));
            Assert.That(result.Session.UserId, Is.EqualTo("user-1"));

            Assert.That(http.Requests[0].Url, Is.EqualTo("https://project.supabase.co/auth/v1/verify"));
            Assert.That(http.Requests[0].Body, Does.Contain("\"type\":\"email\""));
            Assert.That(http.Requests[0].Body, Does.Contain("\"token\":\"123456\""));
        }

        [Test]
        public async Task A_code_pasted_with_spaces_still_works()
        {
            var http = new FakeAuthHttp().Reply(200,
                TokenReply.Body(Jwt.ForUser("user-1", "player@example.com"), "refresh-1", 3600));

            AuthResult result = await Backend(http).VerifyEmailCodeAsync("player@example.com", " 123 456 ");

            Assert.That(result.Ok, Is.True);
            Assert.That(http.Requests[0].Body, Does.Contain("\"token\":\"123456\""));
        }

        [Test]
        public async Task A_short_code_is_refused_here_rather_than_spent_on_a_request()
        {
            var http = new FakeAuthHttp();

            AuthResult result = await Backend(http).VerifyEmailCodeAsync("player@example.com", "12");

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Failure, Is.EqualTo(AuthFailure.Rejected));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task A_wrong_code_is_a_rejection_and_a_dead_network_is_not()
        {
            var rejected = await Backend(new FakeAuthHttp().Reply(403, "{\"msg\":\"Token has expired or is invalid\"}"))
                .VerifyEmailCodeAsync("player@example.com", "000000");
            var offline = await Backend(new FakeAuthHttp().ReplyTransportFailure())
                .VerifyEmailCodeAsync("player@example.com", "000000");

            Assert.That(rejected.Failure, Is.EqualTo(AuthFailure.Rejected));
            Assert.That(rejected.Detail, Is.EqualTo("Token has expired or is invalid"));
            Assert.That(offline.Failure, Is.EqualTo(AuthFailure.Network));
        }

        // ── What the provider does with the result ──────────────────────────────────────

        [Test]
        public async Task A_verified_code_stores_the_session_and_announces_it()
        {
            var store = new InMemoryAuthSessionStore();
            var backend = new FakeAuthBackend
            {
                VerifyResult = AuthResult.Succeeded(new AuthSession
                {
                    AccessToken = "access-1",
                    RefreshToken = "refresh-1",
                    ExpiresAtMs = 9_000_000,
                    Email = "player@example.com",
                }),
            };
            var provider = new AuthTokenProvider(backend, store, new FakeClock());

            AuthSession? announced = null;
            provider.SessionChanged += s => announced = s;

            AuthResult result = await provider.VerifyEmailCodeAsync("player@example.com", "123456");

            Assert.That(result.Ok, Is.True);
            Assert.That(provider.IsSignedIn, Is.True);
            Assert.That(store.Load().AccessToken, Is.EqualTo("access-1"));
            Assert.That(announced?.Email, Is.EqualTo("player@example.com"));
        }

        [Test]
        public async Task A_refused_code_leaves_the_player_exactly_where_they_were()
        {
            var store = new InMemoryAuthSessionStore();
            var backend = new FakeAuthBackend { VerifyResult = AuthResult.Failed(AuthFailure.Rejected, "wrong code") };
            var provider = new AuthTokenProvider(backend, store, new FakeClock());

            AuthResult first = await provider.VerifyEmailCodeAsync("player@example.com", "111111");
            AuthResult second = await provider.VerifyEmailCodeAsync("player@example.com", "222222");

            Assert.That(first.Ok, Is.False);
            Assert.That(provider.IsSignedIn, Is.False);
            // Nothing was cleared and nothing was consumed: the SECOND attempt reaches the backend with
            // the same address. A mistyped digit must not send the player back to their inbox.
            Assert.That(second.Ok, Is.False);
            Assert.That(backend.VerifyCalls, Is.EqualTo(2));
            Assert.That(backend.LastVerifiedEmail, Is.EqualTo("player@example.com"));
            Assert.That(backend.LastVerifiedCode, Is.EqualTo("222222"));
        }

        // ── The shape the overlay's field and button agree on ───────────────────────────

        [TestCase("123456", "123456")]
        [TestCase(" 123 456 ", "123456")]
        [TestCase("12-34-56", "123456")]
        [TestCase("1234567", "123456")]
        [TestCase("1a2b3c4d5e6f", "123456")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void Normalize_keeps_the_first_six_digits_and_nothing_else(string? typed, string expected)
            => Assert.That(EmailCode.Normalize(typed), Is.EqualTo(expected));

        [TestCase("123456", true)]
        [TestCase("12345", false)]
        [TestCase("abcdef", false)]
        public void IsComplete_is_what_the_confirm_button_reads(string typed, bool complete)
            => Assert.That(EmailCode.IsComplete(typed), Is.EqualTo(complete));
    }
}
