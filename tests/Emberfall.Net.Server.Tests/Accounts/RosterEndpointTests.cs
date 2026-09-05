using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// <c>GET /roster</c> — the characters and decks read down after the login handshake. Spec:
    /// <c>docs/dev-docs/specs/login-handshake.md</c>.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class RosterEndpointTests
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
        public async Task No_Token_Is_Unauthenticated()
        {
            RosterResult r = await RosterEndpoint.ReadAsync(
                null, Accepts(), new InMemoryAccountStore(new FixedTime()), new InMemoryPlayerDataStore());

            Assert.That(r.Status, Is.EqualTo(RosterResultStatus.Unauthenticated));
        }

        [Test]
        public async Task No_Player_Data_Store_Is_Unavailable()
        {
            RosterResult r = await RosterEndpoint.ReadAsync(
                Token, Accepts(), new InMemoryAccountStore(new FixedTime()), null);

            Assert.That(r.Status, Is.EqualTo(RosterResultStatus.Unavailable),
                "a box that cannot read the roster must say so — an empty 'known' roster would " +
                "make the client believe this player has no characters");
            Assert.That(r.HttpStatus, Is.EqualTo(503));
        }

        [Test]
        public async Task An_Empty_Roster_Is_Known_And_Empty()
        {
            RosterResult r = await RosterEndpoint.ReadAsync(
                Token, Accepts(), new InMemoryAccountStore(new FixedTime()), new InMemoryPlayerDataStore());

            Assert.That(r.Status, Is.EqualTo(RosterResultStatus.Known));
            Assert.That(r.Roster.Decks, Is.Empty,
                "empty is a REAL answer, distinct from Unavailable — this is the whole point");
        }

        [Test]
        public async Task It_Reads_Only_The_Authenticated_Players_Rows()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            Account mine = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            await data.SaveDeckAsync(mine.PlayerId,
                new DeckList(CardClass.EMBERWARD, CardClass.TIDECALLER) { Id = "d1", Name = "Ashe" });
            await data.SaveDeckAsync("somebody-else",
                new DeckList(CardClass.VERDANT, CardClass.NIGHTVEIL) { Id = "d2", Name = "Nope" });

            RosterResult r = await RosterEndpoint.ReadAsync(Token, Accepts(), accounts, data);

            Assert.That(r.Roster.Decks.Count, Is.EqualTo(1));
            Assert.That(r.Roster.Decks[0].Id, Is.EqualTo("d1"));
        }
    }
}
