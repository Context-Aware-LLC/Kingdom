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

        /// <summary>A DIFFERENT signed-in identity, so the account store mints a second PlayerId.</summary>
        private static IIdentityTokenVerifier AcceptsOther()
            => new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity("supabase-subject-0002", "other@example.com", EmailVerified: true)));

        private static IIdentityTokenVerifier Refuses()
            => new StubVerifier(TokenVerificationResult.Fail(TokenVerificationFailure.Expired));

        private static string BodyFor(string id)
        {
            var deck = new DeckList { Id = id, CharacterId = "c1", Name = "D" };
            deck.Entries.Add(new DeckEntry(1001, 2));
            return CollectionJson.WriteDecks(new[] { deck });
        }

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
        public async Task A_Well_Formed_Document_With_No_Decks_Is_Malformed()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();

            // Parses cleanly and throws nothing — ReadDecks just returns zero rows. That is the
            // branch the catch cannot reach, and there is no deck to attribute the write to.
            DeckWriteResult r = await DeckWriteEndpoint.SaveAsync(
                Token, "d1", "{\"decks\":[]}", Accepts(), accounts, data);

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.Malformed));
            Assert.That(r.HttpStatus, Is.EqualTo(400));

            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            Assert.That((await data.ReadAsync(a.PlayerId)).Decks, Is.Empty);
        }

        [Test]
        public async Task A_Deck_Id_Another_Player_Owns_Is_409_Not_A_Reported_Save()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            await DeckWriteEndpoint.SaveAsync(Token, "d1", BodyFor("d1"), Accepts(), accounts, data);

            DeckWriteResult r = await DeckWriteEndpoint.SaveAsync(
                Token, "d1", BodyFor("d1"), AcceptsOther(), accounts, data);

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.DuplicateId),
                "refusing a write it cannot ATTRIBUTE — nothing here judged the deck's legality");
            Assert.That(r.HttpStatus, Is.EqualTo(409));

            Account other = await accounts.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of("supabase-subject-0002"));
            Assert.That((await data.ReadAsync(other.PlayerId)).Decks, Is.Empty,
                "nothing was written, which is exactly why 200 would have been a lie");
        }

        [Test]
        public async Task An_Empty_Deck_Id_Is_Refused_On_Save()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();

            DeckWriteResult r = await DeckWriteEndpoint.SaveAsync(
                Token, "  ", BodyFor("d1"), Accepts(), accounts, data);

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.InvalidId));
            Assert.That(r.HttpStatus, Is.EqualTo(400));

            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            Assert.That((await data.ReadAsync(a.PlayerId)).Decks, Is.Empty,
                "a row keyed on whitespace is a row nothing can address again");
        }

        [Test]
        public async Task An_Empty_Deck_Id_Is_Refused_On_Delete()
        {
            DeckWriteResult r = await DeckWriteEndpoint.DeleteAsync(
                Token, "", Accepts(), new InMemoryAccountStore(new FixedTime()), new InMemoryPlayerDataStore());

            Assert.That(r.Status, Is.EqualTo(DeckWriteStatus.InvalidId),
                "400, not the 404 an unusable id would otherwise be reported as");
            Assert.That(r.HttpStatus, Is.EqualTo(400));
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

        // ── IsAuthenticatedAsync — PUT /decks/{id}'s auth-before-read gate ──────────────────────

        [Test]
        public async Task IsAuthenticatedAsync_Is_False_For_A_Refused_Token()
            => Assert.That(await DeckWriteEndpoint.IsAuthenticatedAsync(Token, Refuses()), Is.False,
                "the route must refuse before it reads a body, or an unbounded body outranks a bad token");

        [Test]
        public async Task IsAuthenticatedAsync_Is_False_With_No_Token_And_No_Verifier()
        {
            Assert.That(await DeckWriteEndpoint.IsAuthenticatedAsync(null, Accepts()), Is.False);
            Assert.That(await DeckWriteEndpoint.IsAuthenticatedAsync(Token, null), Is.False);
        }

        [Test]
        public async Task IsAuthenticatedAsync_Is_True_For_An_Accepted_Token()
            => Assert.That(await DeckWriteEndpoint.IsAuthenticatedAsync(Token, Accepts()), Is.True);
    }
}
