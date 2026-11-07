using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary><c>GET /account</c>: the settings Account view's email and registration date.</summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class AccountEndpointTests
    {
        private const string Subject = "supabase-subject-0002";
        private const string Token = "a.jwt.token";
        private const long Now = 1_700_000_000_000L;

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => Now;
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
        public async Task No_Token_Is_Unauthenticated()
        {
            AccountReadResult r = await AccountEndpoint.ReadAsync(
                null, Accepts(), new InMemoryAccountStore(new FixedTime()));

            Assert.That(r.HttpStatus, Is.EqualTo(401));
            Assert.That(r.CreatedAt, Is.Null);
        }

        [Test]
        public async Task No_Account_Store_Is_Unavailable_With_No_Date()
        {
            AccountReadResult r = await AccountEndpoint.ReadAsync(Token, Accepts(), null);

            Assert.That(r.HttpStatus, Is.EqualTo(503));
            Assert.That(r.CreatedAt, Is.Null, "no answer must not read as a registration date");
        }

        [Test]
        public async Task Ready_Reports_The_Email_And_The_Creation_Date()
        {
            var store = new InMemoryAccountStore(new FixedTime());

            AccountReadResult r = await AccountEndpoint.ReadAsync(Token, Accepts(), store);

            Assert.That(r.Status, Is.EqualTo(SessionResultStatus.Ready));
            Assert.That(r.Email, Is.EqualTo("player@example.com"));
            Assert.That(r.CreatedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeMilliseconds(Now)));
        }
    }
}
