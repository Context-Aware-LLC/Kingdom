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
