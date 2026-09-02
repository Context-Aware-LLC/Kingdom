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
    /// <c>DELETE /characters/{id}</c>. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para>The regression this fixture exists for is <b>a delete that resurrects</b>: before this
    /// route, a character removed on the device stayed on the server and came straight back down on
    /// the next <c>GET /roster</c>, decks included.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CharacterDeleteEndpointTests
    {
        private const string Subject = "supabase-subject-0001";
        private const string Token = "a.jwt.token";

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

        private static Character NewCharacter(string id = "c1")
            => new Character(id, "Ashe", CardClass.EMBERWARD, CardClass.TIDECALLER);

        private static DeckList NewDeck(string id, string characterId)
        {
            var deck = new DeckList { Id = id, CharacterId = characterId, Name = "Aggro" };
            deck.Entries.Add(new DeckEntry(1001, 2));
            return deck;
        }

        /// <summary>Save a character and a deck for the identity <see cref="Accepts"/> presents, the
        /// way the routes themselves resolve it — not by inventing a player id.</summary>
        private static async Task<string> SeedAsync(
            InMemoryAccountStore accounts, InMemoryPlayerDataStore data,
            string subject = Subject, string characterId = "c1", string deckId = "d1")
        {
            Account account = await accounts.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(subject), "player@example.com");
            await data.SaveCharacterAsync(account.PlayerId, NewCharacter(characterId));
            await data.SaveDeckAsync(account.PlayerId, NewDeck(deckId, characterId));
            return account.PlayerId;
        }

        // ── The point of the route ──────────────────────────────────────────────────────

        [Test]
        public async Task Deleting_A_Character_Removes_It_And_Its_Decks_From_The_Roster()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            string playerId = await SeedAsync(accounts, data);

            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                Token, "c1", Accepts(), accounts, data);

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.Deleted));
            Assert.That(result.HttpStatus, Is.EqualTo(200));

            PlayerRoster roster = await data.ReadAsync(playerId);
            Assert.That(roster.Characters, Is.Empty);
            Assert.That(roster.Decks, Is.Empty,
                "the whole reason for the route: what GET /roster reads back must not resurrect it");
        }

        [Test]
        public async Task Deleting_The_Same_Character_Twice_Is_NotFound_The_Second_Time()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            await SeedAsync(accounts, data);

            await CharacterDeleteEndpoint.DeleteAsync(Token, "c1", Accepts(), accounts, data);
            CharacterDeleteResult again = await CharacterDeleteEndpoint.DeleteAsync(
                Token, "c1", Accepts(), accounts, data);

            Assert.That(again.Status, Is.EqualTo(CharacterDeleteStatus.NotFound));
            Assert.That(again.HttpStatus, Is.EqualTo(404));
        }

        [Test]
        public async Task An_Unknown_Character_Is_NotFound()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();

            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                Token, "no-such-character", Accepts(), accounts, data);

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.NotFound));
        }

        // ── The identity boundary ───────────────────────────────────────────────────────

        [Test]
        public async Task One_Player_Cannot_Delete_Anothers_Character()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            string victim = await SeedAsync(accounts, data);

            // A different signed-in identity, addressing the victim's character id directly.
            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                Token, "c1", AcceptsOther(), accounts, data);

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.NotFound),
                "404, not 200 — and nothing deleted");
            PlayerRoster roster = await data.ReadAsync(victim);
            Assert.That(roster.Characters.Count, Is.EqualTo(1));
            Assert.That(roster.Decks.Count, Is.EqualTo(1), "the cascade must not have run either");
        }

        // ── Auth and the answers that are not about the character ───────────────────────

        [Test]
        public async Task No_Token_Is_Unauthenticated()
        {
            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                null, "c1", Accepts(), new InMemoryAccountStore(new FixedTime()),
                new InMemoryPlayerDataStore());

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.Unauthenticated));
            Assert.That(result.HttpStatus, Is.EqualTo(401));
        }

        [Test]
        public async Task A_Refused_Token_Is_Unauthenticated()
        {
            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                Token, "c1", Refuses(), new InMemoryAccountStore(new FixedTime()),
                new InMemoryPlayerDataStore());

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.Unauthenticated));
        }

        [Test]
        public async Task Auth_Is_Checked_Before_The_Id()
        {
            // An empty id AND no token: the answer must be 401, the same as every other route,
            // rather than telling an unauthenticated caller anything about request shape.
            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                null, "", Accepts(), new InMemoryAccountStore(new FixedTime()),
                new InMemoryPlayerDataStore());

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.Unauthenticated));
        }

        [TestCase("")]
        [TestCase("   ")]
        public async Task An_Unusable_Id_Is_InvalidId(string id)
        {
            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                Token, id, Accepts(), new InMemoryAccountStore(new FixedTime()),
                new InMemoryPlayerDataStore());

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.InvalidId));
            Assert.That(result.HttpStatus, Is.EqualTo(400));
        }

        [Test]
        public async Task No_Account_Store_Is_Unavailable()
        {
            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                Token, "c1", Accepts(), null, new InMemoryPlayerDataStore());

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.Unavailable));
            Assert.That(result.HttpStatus, Is.EqualTo(503),
                "503 is 'I could not find out' — it must never read as 'that character is gone'");
        }

        [Test]
        public async Task No_Player_Data_Store_Is_Unavailable()
        {
            CharacterDeleteResult result = await CharacterDeleteEndpoint.DeleteAsync(
                Token, "c1", Accepts(), new InMemoryAccountStore(new FixedTime()), null);

            Assert.That(result.Status, Is.EqualTo(CharacterDeleteStatus.Unavailable));
        }
    }
}
