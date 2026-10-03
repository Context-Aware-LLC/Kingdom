using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// <c>GET /session</c> — the login handshake the client boot blocks on. Spec:
    /// <c>docs/dev-docs/specs/login-handshake.md</c>.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SessionEndpointTests
    {
        private const string Subject = "supabase-subject-0001";
        private const string Token = "a.jwt.token";

        // ── Support ─────────────────────────────────────────────────────────────────────

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            private readonly TokenVerificationResult _result;
            public StubVerifier(TokenVerificationResult result) => _result = result;
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(_result);
        }

        private static IIdentityTokenVerifier Accepts()
            => new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity(Subject, "player@example.com", EmailVerified: true)));

        private static IIdentityTokenVerifier Rejects()
            => new StubVerifier(TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));

        [Test]
        public async Task No_Token_Is_Unauthenticated()
        {
            SessionResult r = await SessionEndpoint.ReadAsync(
                null, Accepts(), new InMemoryAccountStore(new FixedTime()), null);

            Assert.That(r.Status, Is.EqualTo(SessionResultStatus.Unauthenticated));
            Assert.That(r.HttpStatus, Is.EqualTo(401));
        }

        [Test]
        public async Task Rejected_Token_Is_Unauthenticated()
        {
            SessionResult r = await SessionEndpoint.ReadAsync(
                Token, Rejects(), new InMemoryAccountStore(new FixedTime()), null);

            Assert.That(r.Status, Is.EqualTo(SessionResultStatus.Unauthenticated));
        }

        [Test]
        public async Task No_Account_Store_Is_Unavailable()
        {
            SessionResult r = await SessionEndpoint.ReadAsync(Token, Accepts(), null, null);

            Assert.That(r.Status, Is.EqualTo(SessionResultStatus.Unavailable),
                "a box with no account store cannot answer; it must not answer 'new player'");
            Assert.That(r.HttpStatus, Is.EqualTo(503));
        }

        [Test]
        public async Task A_New_Account_Is_Ready_And_Not_Onboarded()
        {
            var store = new InMemoryAccountStore(new FixedTime());

            SessionResult r = await SessionEndpoint.ReadAsync(Token, Accepts(), store, null);

            Assert.That(r.Status, Is.EqualTo(SessionResultStatus.Ready));
            Assert.That(r.Onboarded, Is.False);
            Assert.That(r.PlayerId, Is.Not.Empty);
        }

        [Test]
        public async Task An_Onboarded_Account_Reports_Onboarded()
        {
            var store = new InMemoryAccountStore(new FixedTime());
            Account a = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            await store.MarkOnboardedAsync(a.PlayerId);

            SessionResult r = await SessionEndpoint.ReadAsync(Token, Accepts(), store, null);

            Assert.That(r.Onboarded, Is.True);
        }

        [Test]
        public async Task A_Ready_Handshake_Stamps_LastSeen()
        {
            var store = new InMemoryAccountStore(new FixedTime());

            SessionResult r = await SessionEndpoint.ReadAsync(Token, Accepts(), store, null);

            Account? account = await store.FindByPlayerIdAsync(r.PlayerId);
            Assert.That(account!.LastSeen,
                Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(new FixedTime().NowMs)),
                "the handshake is the login, so it is what records one");
        }

        [Test]
        public async Task A_Rejected_Token_Stamps_Nothing()
        {
            var store = new InMemoryAccountStore(new FixedTime());
            Account a = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));

            await SessionEndpoint.ReadAsync(Token, Rejects(), store, null);

            Account? account = await store.FindByPlayerIdAsync(a.PlayerId);
            Assert.That(account!.LastSeen, Is.Null, "a refused login is not a login");
        }

        [Test]
        public async Task The_Same_Subject_Resolves_To_The_Same_Player()
        {
            var store = new InMemoryAccountStore(new FixedTime());

            SessionResult first = await SessionEndpoint.ReadAsync(Token, Accepts(), store, null);
            SessionResult second = await SessionEndpoint.ReadAsync(Token, Accepts(), store, null);

            Assert.That(second.PlayerId, Is.EqualTo(first.PlayerId),
                "a reinstall presents the same subject and must land on the same account");
        }
    }
}
