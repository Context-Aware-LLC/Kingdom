using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Ranking;
using Emberfall.Net.Server.Telemetry;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Ranking
{
    /// <summary><c>GET /rating</c> — the signed-in player's own ladder standing.</summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class RatingEndpointTests
    {
        private const string Subject = "supabase-subject-0001";
        private const string Token = "a.jwt.token";

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            private readonly TokenVerificationResult? _result;
            public StubVerifier(TokenVerificationResult? result) => _result = result;
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => _result is null
                    ? throw new InvalidOperationException("JWKS unreachable")
                    : Task.FromResult(_result);
        }

        private static IIdentityTokenVerifier Accepts()
            => new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity(Subject, "player@example.com", EmailVerified: true)));

        [Test]
        public async Task No_Token_Is_Unauthenticated()
        {
            RatingReadResult r = await RatingEndpoint.ReadAsync(
                null, Accepts(), new InMemoryAccountStore(new FixedTime()), new InMemoryRatingStore());

            Assert.That(r.Status, Is.EqualTo(RatingReadStatus.Unauthenticated));
            Assert.That(r.HttpStatus, Is.EqualTo(401));
        }

        [Test]
        public async Task A_Verifier_Outage_Is_Unavailable_Not_Unauthenticated()
        {
            RatingReadResult r = await RatingEndpoint.ReadAsync(
                Token, new StubVerifier(null), new InMemoryAccountStore(new FixedTime()),
                new InMemoryRatingStore());

            Assert.That(r.Status, Is.EqualTo(RatingReadStatus.Unavailable),
                "401 here would sign the player out over a network blip");
            Assert.That(r.HttpStatus, Is.EqualTo(503));
        }

        [Test]
        public async Task A_Player_With_No_Rated_Match_Is_Unranked()
        {
            var ratings = new InMemoryRatingStore();

            RatingReadResult r = await RatingEndpoint.ReadAsync(
                Token, Accepts(), new InMemoryAccountStore(new FixedTime()), ratings);

            Assert.That(r.Status, Is.EqualTo(RatingReadStatus.Unranked),
                "the default rating must not read as one the player earned");
            Assert.That(r.Rating, Is.EqualTo(ratings.DefaultRating));
            Assert.That(r.GamesPlayed, Is.Zero);
            Assert.That(r.HttpStatus, Is.EqualTo(200));
        }

        [Test]
        public async Task A_Rated_Player_Reads_Their_Own_Standing()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            Account account = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            var ratings = new InMemoryRatingStore();
            await ratings.ApplyAsync(new RankedMatchResult(
                new MatchId("rating-endpoint-match"),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                AccountId.FromAuthenticatedAccount(account.PlayerId),
                "Ash",
                AccountId.FromAuthenticatedAccount("somebody-else"),
                "Cinder",
                MatchOutcome.Player0Win,
                TelemetrySource.Ladder));

            RatingReadResult r = await RatingEndpoint.ReadAsync(Token, Accepts(), accounts, ratings);

            Assert.That(r.Status, Is.EqualTo(RatingReadStatus.Ranked));
            Assert.That(r.Rating, Is.EqualTo(1_020));
            Assert.That(r.GamesPlayed, Is.EqualTo(1));
        }
    }
}
