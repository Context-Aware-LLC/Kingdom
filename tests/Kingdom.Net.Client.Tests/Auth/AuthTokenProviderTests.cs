using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Auth
{
    /// <summary>
    /// The session lifecycle behind <c>ClientOptions.AuthToken</c> (EF-074e / NOR-231).
    ///
    /// <para>Every test here is about the same promise: a player who has not signed in, or whose
    /// sign-in has gone wrong in any way, still reaches a match. The server closes the socket on a bad
    /// token and never downgrades silently, so the client is the only place that decision can be made
    /// safely.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class AuthTokenProviderTests
    {
        private static AuthSession Signed(long expiresAtMs, string access = "access-1", string refresh = "refresh-1")
            => new AuthSession
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresAtMs = expiresAtMs,
                UserId = "user-1",
                Email = "player@example.com",
            };

        [Test]
        public async Task A_player_who_never_signed_in_connects_with_an_empty_token()
        {
            var provider = new AuthTokenProvider(new FakeAuthBackend(), new InMemoryAuthSessionStore(), new FakeClock());

            Assert.That(provider.IsSignedIn, Is.False);
            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo(""));
        }

        [Test]
        public async Task An_unconfigured_build_connects_as_a_guest_without_touching_the_backend()
        {
            var backend = new FakeAuthBackend { IsConfigured = false };
            var store = new InMemoryAuthSessionStore(Signed(expiresAtMs: 0));
            var provider = new AuthTokenProvider(backend, store, new FakeClock());

            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo(""));
            Assert.That(backend.RefreshCalls, Is.Zero);
        }

        [Test]
        public async Task A_fresh_token_is_sent_as_is_with_no_refresh()
        {
            var clock = new FakeClock(1_000_000);
            var backend = new FakeAuthBackend();
            var provider = new AuthTokenProvider(
                backend, new InMemoryAuthSessionStore(Signed(clock.NowMs + 3_600_000)), clock);

            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo("access-1"));
            Assert.That(backend.RefreshCalls, Is.Zero);
        }

        [Test]
        public async Task A_token_inside_the_safety_margin_is_refreshed_before_the_connect()
        {
            var clock = new FakeClock(1_000_000);
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs + 30_000));  // expires in 30s, margin is 60s
            var backend = new FakeAuthBackend().OnRefresh(
                AuthResult.Succeeded(Signed(clock.NowMs + 3_600_000, "access-2", "refresh-2")));
            var provider = new AuthTokenProvider(backend, store, clock);

            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo("access-2"));
            Assert.That(backend.LastRefreshToken, Is.EqualTo("refresh-1"));
            // The rotated refresh token has to replace the spent one, or the next refresh fails.
            Assert.That(store.Load().RefreshToken, Is.EqualTo("refresh-2"));
        }

        [Test]
        public async Task Supabase_refusing_the_refresh_signs_the_player_out_rather_than_leaving_a_dead_session()
        {
            var clock = new FakeClock(1_000_000);
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs - 1));  // already expired
            var backend = new FakeAuthBackend().OnRefresh(AuthResult.Failed(AuthFailure.Rejected, "revoked"));
            var provider = new AuthTokenProvider(backend, store, clock);

            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo(""));
            Assert.That(provider.IsSignedIn, Is.False);
            Assert.That(store.Load().IsPresent, Is.False);
        }

        [Test]
        public async Task A_network_failure_keeps_the_session_and_still_sends_a_token_that_has_not_expired()
        {
            var clock = new FakeClock(1_000_000);
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs + 30_000));  // inside the margin, still valid
            var backend = new FakeAuthBackend().OnRefresh(AuthResult.Failed(AuthFailure.Network, "no route"));
            var provider = new AuthTokenProvider(backend, store, clock);

            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo("access-1"));
            Assert.That(store.Load().IsPresent, Is.True, "a request that never arrived says nothing about the credential");
        }

        [Test]
        public async Task A_network_failure_on_an_already_expired_token_falls_back_to_guest_but_stays_signed_in()
        {
            var clock = new FakeClock(1_000_000);
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs - 1));
            var backend = new FakeAuthBackend().OnRefresh(AuthResult.Failed(AuthFailure.Network, "no route"));
            var provider = new AuthTokenProvider(backend, store, clock);

            // Sending it would get the socket closed; the player still gets into a match as a guest,
            // and their session survives to be refreshed when the network comes back.
            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo(""));
            Assert.That(provider.IsSignedIn, Is.True);
        }

        [Test]
        public async Task Two_connects_racing_produce_one_refresh_not_two()
        {
            var clock = new FakeClock(1_000_000);
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs + 30_000));
            var backend = new FakeAuthBackend().OnRefresh(
                AuthResult.Succeeded(Signed(clock.NowMs + 3_600_000, "access-2", "refresh-2")));
            var provider = new AuthTokenProvider(backend, store, clock);

            string[] tokens = await Task.WhenAll(provider.TokenForConnectAsync(), provider.TokenForConnectAsync());

            // A second refresh would spend a rotated token and come back 4xx — which reads exactly
            // like a revoked credential and would sign the player out mid-connect.
            Assert.That(backend.RefreshCalls, Is.EqualTo(1));
            Assert.That(tokens, Is.All.EqualTo("access-2"));
        }

        [Test]
        public async Task A_completed_sign_in_is_stored_and_its_pending_verifier_dropped()
        {
            var clock = new FakeClock(1_000_000);
            var store = new InMemoryAuthSessionStore();
            var backend = new FakeAuthBackend
            {
                CompleteResult = AuthResult.Succeeded(Signed(clock.NowMs + 3_600_000)),
            };
            var provider = new AuthTokenProvider(backend, store, clock);

            await provider.BeginMagicLinkAsync("player@example.com");
            Assert.That(provider.Pending.IsStarted, Is.True, "the verifier must outlive the request that started it");

            AuthResult result = await provider.CompleteSignInAsync("auth-code");

            Assert.That(result.Ok, Is.True);
            Assert.That(store.Load().AccessToken, Is.EqualTo("access-1"));
            Assert.That(provider.Pending.IsStarted, Is.False, "an auth code is single-use");
            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo("access-1"));
        }

        [Test]
        public async Task A_failed_completion_also_drops_the_pending_verifier()
        {
            var store = new InMemoryAuthSessionStore();
            var backend = new FakeAuthBackend { CompleteResult = AuthResult.Failed(AuthFailure.Rejected, "expired code") };
            var provider = new AuthTokenProvider(backend, store, new FakeClock());

            await provider.BeginMagicLinkAsync("player@example.com");
            AuthResult result = await provider.CompleteSignInAsync("auth-code");

            Assert.That(result.Ok, Is.False);
            Assert.That(provider.Pending.IsStarted, Is.False);
            Assert.That(provider.IsSignedIn, Is.False);
        }

        [Test]
        public async Task Signing_out_returns_the_device_to_the_guest_path()
        {
            var clock = new FakeClock(1_000_000);
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs + 3_600_000));
            var provider = new AuthTokenProvider(new FakeAuthBackend(), store, clock);

            provider.SignOut();

            Assert.That(provider.IsSignedIn, Is.False);
            Assert.That(store.Load().IsPresent, Is.False);
            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo(""));
        }

        [Test]
        public async Task Setting_a_password_sends_this_devices_own_access_token()
        {
            var clock = new FakeClock(1_000_000);
            var backend = new FakeAuthBackend();
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs + 3_600_000, access: "access-1"));
            var provider = new AuthTokenProvider(backend, store, clock);

            AuthResult result = await provider.SetPasswordAsync("a new password");

            Assert.That(result.Ok, Is.True);
            Assert.That(backend.LastPasswordAccessToken, Is.EqualTo("access-1"));
            Assert.That(backend.LastPassword, Is.EqualTo("a new password"));
        }

        [Test]
        public async Task Storing_the_name_trades_a_still_good_token_for_one_that_carries_it()
        {
            // The token in hand was minted before the name existed. It is hours from expiring, so
            // nothing but this call would ever replace it in time for a friend to see the name.
            var clock = new FakeClock();
            var named = Signed(clock.NowMs + 3_600_000, access: "access-with-name", refresh: "refresh-2");
            var backend = new FakeAuthBackend().OnRefresh(AuthResult.Succeeded(named));
            var store = new InMemoryAuthSessionStore(Signed(clock.NowMs + 3_600_000));
            var provider = new AuthTokenProvider(backend, store, clock);

            AuthResult result = await provider.SetDisplayNameAsync("MrSoNord");

            Assert.That(result.Ok, Is.True);
            Assert.That(backend.LastDisplayNameAccessToken, Is.EqualTo("access-1"));
            Assert.That(backend.LastRefreshToken, Is.EqualTo("refresh-1"));
            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo("access-with-name"));
        }

        [Test]
        public async Task A_name_that_was_not_stored_does_not_spend_a_refresh()
        {
            var clock = new FakeClock();
            var backend = new FakeAuthBackend { SetDisplayNameResult = AuthResult.Failed(AuthFailure.Network) };
            var provider = new AuthTokenProvider(
                backend, new InMemoryAuthSessionStore(Signed(clock.NowMs + 3_600_000)), clock);

            AuthResult result = await provider.SetDisplayNameAsync("MrSoNord");

            Assert.That(result.Ok, Is.False);
            Assert.That(backend.RefreshCalls, Is.Zero);
        }

        [Test]
        public async Task A_stored_name_is_still_a_success_when_the_refresh_does_not_land()
        {
            var clock = new FakeClock();
            var backend = new FakeAuthBackend().OnRefresh(AuthResult.Failed(AuthFailure.Network));
            var provider = new AuthTokenProvider(
                backend, new InMemoryAuthSessionStore(Signed(clock.NowMs + 3_600_000)), clock);

            AuthResult result = await provider.SetDisplayNameAsync("MrSoNord");

            Assert.That(result.Ok, Is.True, "the name is stored; only the token that carries it is late");
            Assert.That(await provider.TokenForConnectAsync(), Is.EqualTo("access-1"));
        }

        [Test]
        public async Task Setting_a_password_with_no_session_never_reaches_the_backend()
        {
            var backend = new FakeAuthBackend();
            var provider = new AuthTokenProvider(backend, new InMemoryAuthSessionStore(), new FakeClock());

            AuthResult result = await provider.SetPasswordAsync("a new password");

            Assert.That(result.Failure, Is.EqualTo(AuthFailure.Rejected));
            Assert.That(backend.LastPasswordAccessToken, Is.Null);
        }
    }
}
