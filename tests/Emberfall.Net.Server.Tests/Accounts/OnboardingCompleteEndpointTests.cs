using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// <c>POST /onboarding/complete</c> — the only route that sets <c>Onboarded</c>. Spec:
    /// <c>docs/dev-docs/specs/login-handshake.md</c>.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class OnboardingCompleteEndpointTests
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

        [Test]
        public async Task It_Marks_The_Account_Onboarded()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());

            OnboardingCompleteResult r = await OnboardingCompleteEndpoint.CompleteAsync(
                Token, Accepts(), accounts);

            Assert.That(r.Status, Is.EqualTo(OnboardingCompleteStatus.Completed));
            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            Assert.That(a.Onboarded, Is.True);
        }

        [Test]
        public async Task Calling_It_Twice_Is_Idempotent()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            await OnboardingCompleteEndpoint.CompleteAsync(Token, Accepts(), accounts);

            OnboardingCompleteResult again = await OnboardingCompleteEndpoint.CompleteAsync(
                Token, Accepts(), accounts);

            Assert.That(again.Status, Is.EqualTo(OnboardingCompleteStatus.Completed),
                "a client that retries after a dropped response must not get an error");
        }

        [Test]
        public async Task It_Is_Reflected_By_The_Next_Session_Handshake()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            await OnboardingCompleteEndpoint.CompleteAsync(Token, Accepts(), accounts);

            SessionResult session = await SessionEndpoint.ReadAsync(Token, Accepts(), accounts, null);

            Assert.That(session.Onboarded, Is.True,
                "this is the whole contract: the flag set here is what the next launch reads");
        }

        [Test]
        public async Task No_Token_Is_Unauthenticated()
            => Assert.That((await OnboardingCompleteEndpoint.CompleteAsync(
                    null, Accepts(), new InMemoryAccountStore(new FixedTime()))).Status,
                Is.EqualTo(OnboardingCompleteStatus.Unauthenticated));
    }
}
