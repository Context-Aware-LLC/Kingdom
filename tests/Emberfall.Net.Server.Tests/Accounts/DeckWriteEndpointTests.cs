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
    /// <c>PUT /decks/{id}</c> and <c>DELETE /decks/{id}</c>. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class DeckWriteEndpointTests
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
        public async Task A_Deck_That_Fails_L6_Is_Still_Saved()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            var draft = new DeckList { Id = "d1", CharacterId = "c1", Name = "Draft" };
            draft.Entries.Add(new DeckEntry(1001, 1));

            DeckWriteResult r = await DeckWriteEndpoint.SaveAsync(
                Token, "d1", CollectionJson.WriteDecks(new[] { draft }), Accepts(), accounts, data);

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.Saved),
                "validate on QUEUE, never on WRITE — refusing here deletes work-in-progress the " +
                "moment a balance patch makes a deck illegal");
        }

        [Test]
        public async Task A_Malformed_Body_Is_Refused_Without_Touching_The_Store()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();

            DeckWriteResult r = await DeckWriteEndpoint.SaveAsync(
                Token, "d1", "{ not json", Accepts(), accounts, data);

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.Malformed));
            Assert.That(r.HttpStatus, Is.EqualTo(400));

            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            Assert.That((await data.ReadAsync(a.PlayerId)).Decks, Is.Empty,
                "the name claims the store was not touched — assert it, not just the status");
        }

        [Test]
        public async Task The_Route_Id_Wins_Over_The_Body_Id()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            var deck = new DeckList { Id = "body-says-this", CharacterId = "c1", Name = "D" };

            await DeckWriteEndpoint.SaveAsync(
                Token, "route-says-this", CollectionJson.WriteDecks(new[] { deck }),
                Accepts(), accounts, data);

            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            PlayerRoster roster = await data.ReadAsync(a.PlayerId);
            Assert.That(roster.Decks[0].Id, Is.EqualTo("route-says-this"),
                "one authority for the id, or a client could write to an id it did not address");
        }

        [Test]
        public async Task Deleting_A_Deck_Removes_It()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            var deck = new DeckList { Id = "d1", CharacterId = "c1", Name = "D" };
            await DeckWriteEndpoint.SaveAsync(
                Token, "d1", CollectionJson.WriteDecks(new[] { deck }), Accepts(), accounts, data);

            DeckWriteResult r = await DeckWriteEndpoint.DeleteAsync(Token, "d1", Accepts(), accounts, data);

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.Deleted));
            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            Assert.That((await data.ReadAsync(a.PlayerId)).Decks, Is.Empty,
                "one record, so delete means delete — there is no tombstone to resurrect from");
        }

        [Test]
        public async Task Deleting_An_Unknown_Deck_Is_NotFound()
        {
            DeckWriteResult r = await DeckWriteEndpoint.DeleteAsync(
                Token, "nope", Accepts(), new InMemoryAccountStore(new FixedTime()), new InMemoryPlayerDataStore());

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.NotFound));
            Assert.That(r.HttpStatus, Is.EqualTo(404));
        }
    }
}
