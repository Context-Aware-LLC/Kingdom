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
    /// <c>POST /characters</c> — creates a character. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CharacterCreateEndpointTests
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
        public async Task It_Creates_A_Character()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();

            CharacterCreateResult r = await CharacterCreateEndpoint.CreateAsync(
                Token, "c1", "Ashe", CardClass.EMBERWARD, CardClass.TIDECALLER,
                Accepts(), accounts, data);

            Assert.That(r.Status, Is.EqualTo(CharacterCreateStatus.Created));

            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            Assert.That((await data.ReadAsync(a.PlayerId)).Characters.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task An_Invalid_Pairing_Is_Refused()
        {
            CharacterCreateResult r = await CharacterCreateEndpoint.CreateAsync(
                Token, "c1", "Ashe", CardClass.EMBERWARD, CardClass.EMBERWARD,
                Accepts(), new InMemoryAccountStore(new FixedTime()), new InMemoryPlayerDataStore());

            Assert.That(r.Status, Is.EqualTo(CharacterCreateStatus.InvalidPairing),
                "Character.IsValidPairing: both classes must be real and distinct");
            Assert.That(r.HttpStatus, Is.EqualTo(400));
        }

        [Test]
        public async Task A_Neutral_Class_Is_An_Invalid_Pairing()
        {
            CharacterCreateResult r = await CharacterCreateEndpoint.CreateAsync(
                Token, "c1", "Ashe", CardClass.NEUTRAL, CardClass.TIDECALLER,
                Accepts(), new InMemoryAccountStore(new FixedTime()), new InMemoryPlayerDataStore());

            Assert.That(r.Status, Is.EqualTo(CharacterCreateStatus.InvalidPairing));
        }

        [Test]
        public async Task Creating_The_Same_Id_Twice_Is_Refused_Not_Silently_Overwritten()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            var data = new InMemoryPlayerDataStore();
            await CharacterCreateEndpoint.CreateAsync(Token, "c1", "Ashe",
                CardClass.EMBERWARD, CardClass.TIDECALLER, Accepts(), accounts, data);

            CharacterCreateResult again = await CharacterCreateEndpoint.CreateAsync(
                Token, "c1", "Different", CardClass.VERDANT, CardClass.NIGHTVEIL,
                Accepts(), accounts, data);

            Assert.That(again.Status, Is.EqualTo(CharacterCreateStatus.DuplicateId),
                "the pairing is permanent and has no legal repair — an overwrite would BE a repair");
        }

        [Test]
        public async Task It_Does_Not_Set_Onboarded()
        {
            var accounts = new InMemoryAccountStore(new FixedTime());
            await CharacterCreateEndpoint.CreateAsync(Token, "c1", "Ashe",
                CardClass.EMBERWARD, CardClass.TIDECALLER, Accepts(), accounts, new InMemoryPlayerDataStore());

            Account a = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(Subject));
            Assert.That(a.Onboarded, Is.False,
                "onboarded flips at the END of the flow, after the grant — never at creation. " +
                "Flipping here strands a player who drops before the grant lands.");
        }

        // ── TryParseClass — the route-level wire parse, tested where the route cannot be ────────

        [Test]
        public void TryParseClass_Accepts_A_Valid_Name_Case_Insensitively()
        {
            bool ok = CharacterCreateEndpoint.TryParseClass("emberward", out CardClass value);

            Assert.That(ok, Is.True);
            Assert.That(value, Is.EqualTo(CardClass.EMBERWARD));
        }

        [Test]
        public void TryParseClass_Rejects_An_Unrecognised_Name()
        {
            bool ok = CharacterCreateEndpoint.TryParseClass("not-a-class", out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryParseClass_Rejects_A_Numeric_String_With_No_Backing_Member()
        {
            // Enum.TryParse<CardClass>("99", true, out _) alone returns true with an undefined
            // value — this is exactly the gap Enum.IsDefined has to close.
            bool ok = CharacterCreateEndpoint.TryParseClass("99", out CardClass value);

            Assert.That(ok, Is.False,
                "a numeric string that doesn't back a real enum member must not parse as valid");
            Assert.That(value, Is.EqualTo(default(CardClass)),
                "out value on failure is the default, never the undefined parsed value");
        }

        [Test]
        public void TryParseClass_Rejects_The_Empty_String()
        {
            bool ok = CharacterCreateEndpoint.TryParseClass("", out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryParseClass_Rejects_Null()
        {
            bool ok = CharacterCreateEndpoint.TryParseClass(null, out _);

            Assert.That(ok, Is.False);
        }
    }
}
