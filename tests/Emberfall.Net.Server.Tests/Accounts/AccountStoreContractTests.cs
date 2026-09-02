using System.Threading.Tasks;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The account-store contract (EF-072), run against BOTH implementations. The store's job is to turn
    /// a credential into a stable player identity, idempotently — so the same credential always resolves
    /// to the same player, and that player id is the key the entitlement ledger records against.
    /// </summary>
    [Category("Contract")]
    public abstract class AccountStoreContractTests
    {
        protected abstract IAccountStore Store { get; }

        [Test]
        public async Task ResolveOrCreate_Is_Idempotent_On_Credential()
        {
            Account a = await Store.ResolveOrCreateAsync("device_token", "hash-aaa");
            Account b = await Store.ResolveOrCreateAsync("device_token", "hash-aaa");

            Assert.That(b.PlayerId, Is.EqualTo(a.PlayerId),
                "the same credential always resolves to the same player");
        }

        [Test]
        public async Task Different_Credentials_Get_Different_Players()
        {
            Account a = await Store.ResolveOrCreateAsync("device_token", "hash-aaa");
            Account b = await Store.ResolveOrCreateAsync("device_token", "hash-bbb");

            Assert.That(b.PlayerId, Is.Not.EqualTo(a.PlayerId));
        }

        [Test]
        public async Task FindByPlayerId_Round_Trips()
        {
            Account a = await Store.ResolveOrCreateAsync("google", "sub-123");

            Account? found = await Store.FindByPlayerIdAsync(a.PlayerId);
            Assert.That(found, Is.Not.Null);
            Assert.That(found!.CredentialKind, Is.EqualTo("google"));
            Assert.That(found.CredentialHash, Is.EqualTo("sub-123"));
        }

        [Test]
        public async Task FindByPlayerId_Unknown_Is_Null()
            => Assert.That(await Store.FindByPlayerIdAsync("no-such-player"), Is.Null);

        [Test]
        public async Task New_Accounts_Default_To_Standard_Kind()
        {
            Account a = await Store.ResolveOrCreateAsync("device_token", "hash-kind-default");
            Assert.That(a.Kind, Is.EqualTo(AccountKind.Standard));
        }

        [Test]
        public async Task SetKind_Promotes_And_Persists()
        {
            Account a = await Store.ResolveOrCreateAsync("device_token", "hash-kind-promote");

            Account? promoted = await Store.SetKindAsync(a.PlayerId, AccountKind.Superuser);
            Assert.That(promoted, Is.Not.Null);
            Assert.That(promoted!.Kind, Is.EqualTo(AccountKind.Superuser));

            Account? reread = await Store.FindByPlayerIdAsync(a.PlayerId);
            Assert.That(reread!.Kind, Is.EqualTo(AccountKind.Superuser),
                "the promotion must persist, not just live on the returned instance");
        }

        [Test]
        public async Task SetKind_On_Unknown_Player_Is_Null()
            => Assert.That(await Store.SetKindAsync("no-such-player", AccountKind.Superuser), Is.Null);

        // ── onboarded (Sam, 2026-09-01) ──────────────────────────────────────────────────
        // The flag exists so a client can tell "brand new player" from "the read failed" when a
        // roster comes back empty. These pin the two properties that make it usable for that: it
        // starts false, and once true it stays true across a re-read.

        [Test]
        public async Task New_Accounts_Are_Not_Onboarded()
        {
            Account a = await Store.ResolveOrCreateAsync("device_token", "hash-onboard-default");
            Assert.That(a.Onboarded, Is.False, "a brand-new account has not been through onboarding");
        }

        [Test]
        public async Task MarkOnboarded_Persists()
        {
            Account a = await Store.ResolveOrCreateAsync("device_token", "hash-onboard-mark");

            Account? marked = await Store.MarkOnboardedAsync(a.PlayerId);
            Assert.That(marked, Is.Not.Null);
            Assert.That(marked!.Onboarded, Is.True);

            Account? reread = await Store.FindByPlayerIdAsync(a.PlayerId);
            Assert.That(reread!.Onboarded, Is.True,
                "onboarding must survive a re-read — a reinstall re-reads, and that is the whole point");
        }

        [Test]
        public async Task MarkOnboarded_Is_Idempotent()
        {
            Account a = await Store.ResolveOrCreateAsync("device_token", "hash-onboard-twice");
            await Store.MarkOnboardedAsync(a.PlayerId);

            Account? again = await Store.MarkOnboardedAsync(a.PlayerId);
            Assert.That(again, Is.Not.Null,
                "a second call must not read as 'no such account' — the caller cannot distinguish that " +
                "from a genuinely missing player, and would report the wrong failure");
            Assert.That(again!.Onboarded, Is.True);
        }

        [Test]
        public async Task ResolveOrCreate_Does_Not_Reset_Onboarded()
        {
            Account a = await Store.ResolveOrCreateAsync("google", "sub-onboard-return");
            await Store.MarkOnboardedAsync(a.PlayerId);

            // The returning-player path: the same credential resolves again on the next sign-in.
            Account again = await Store.ResolveOrCreateAsync("google", "sub-onboard-return");
            Assert.That(again.Onboarded, Is.True,
                "signing in again must not look like a new player — that is the reinstall case");
        }

        [Test]
        public async Task MarkOnboarded_On_Unknown_Player_Is_Null()
            => Assert.That(await Store.MarkOnboardedAsync("no-such-player"), Is.Null);
    }

    /// <summary>The in-memory implementation against the shared contract. Always runs.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class InMemoryAccountStoreContractTests : AccountStoreContractTests
    {
        private InMemoryAccountStore _store = null!;
        protected override IAccountStore Store => _store;

        [SetUp]
        public void SetUp() => _store = new InMemoryAccountStore(new FakeTime());
    }

    /// <summary>The identical suite against Postgres. Gated on <c>EMBERFALL_LEDGER_DB</c> (see
    /// <see cref="PostgresProbe"/>); truncates before each test for a clean store.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class PostgresAccountStoreContractTests : AccountStoreContractTests
    {
        private string _conn = null!;
        private PostgresAccountStore _store = null!;
        protected override IAccountStore Store => _store;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _conn = PostgresProbe.RequireOrIgnore();
            _store = new PostgresAccountStore(_conn, new FakeTime());
            await _store.EnsureSchemaAsync();
        }

        [SetUp]
        public async Task SetUp() => await PostgresProbe.TruncateAsync(_conn);
    }
}
