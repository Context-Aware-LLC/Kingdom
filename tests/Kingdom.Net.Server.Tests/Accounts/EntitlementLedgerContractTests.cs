using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The ONE entitlement-ledger contract (EF-072), run against BOTH implementations — the DONE-WHEN's
    /// "both implementations pass the same suite." Every <c>[Test]</c> here is inherited by each concrete
    /// fixture below, so the in-memory and Postgres ledgers are held to identical behaviour. The
    /// headline is <see cref="Replayed_External_Txn_Id_Grants_Once"/>: a replayed receipt grants exactly
    /// once, which is the whole point of an append-only ledger keyed on a UNIQUE transaction id.
    /// </summary>
    [Category("Contract")]
    public abstract class EntitlementLedgerContractTests
    {
        /// <summary>A fresh, empty ledger for the test in flight (set up by the concrete fixture).</summary>
        protected abstract IEntitlementLedger Ledger { get; }

        // A microsecond-clean instant (Postgres timestamptz precision) so CreatedAt round-trips exactly.
        private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000);

        private static EntitlementGrant Grant(string player, string item, long qty, string source, string txn)
            => new EntitlementGrant(player, item, qty, source, txn, At);

        private static EntitlementBatch PurchaseBatch(string txn)
            => new EntitlementBatch(
                "p1",
                txn,
                new List<EntitlementDelta>
                {
                    new EntitlementDelta("gold", -100, "pack_purchase"),
                    new EntitlementDelta("pack_core", 1, "pack_purchase"),
                },
                new EntitlementBalanceRequirement("gold", 100),
                At);

        [Test]
        public async Task Grant_Appends_And_Balance_Reflects_It()
        {
            GrantResult res = await Ledger.GrantAsync(Grant("p1", "pack_core", 5, "play_billing", "txn-1"));

            Assert.That(res.Applied, Is.True);
            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(5));
        }

        [Test]
        public async Task Conditional_Batch_Appends_All_Deltas_When_The_Balance_Covers_The_Spend()
        {
            await Ledger.GrantAsync(Grant("p1", "gold", 100, "quest", "quest-1"));

            EntitlementBatchResult result = await Ledger.ApplyBatchAsync(PurchaseBatch("purchase-1"));

            Assert.Multiple(() =>
            {
                Assert.That(result.Applied, Is.True);
                Assert.That(result.Outcome, Is.EqualTo(EntitlementBatchOutcome.Applied));
            });
            Assert.That(await Ledger.BalanceAsync("p1", "gold"), Is.EqualTo(0));
            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(1));
            Assert.That(await Ledger.HistoryAsync("p1"), Has.Count.EqualTo(3),
                "The seed plus both batch rows must commit together.");
        }

        [Test]
        public async Task Conditional_Batch_Appends_Nothing_When_The_Balance_Is_Insufficient()
        {
            await Ledger.GrantAsync(Grant("p1", "gold", 99, "quest", "quest-1"));

            EntitlementBatchResult result = await Ledger.ApplyBatchAsync(PurchaseBatch("purchase-1"));

            Assert.That(result.InsufficientBalance, Is.True);
            Assert.That(await Ledger.BalanceAsync("p1", "gold"), Is.EqualTo(99));
            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(0));
            Assert.That(await Ledger.HistoryAsync("p1"), Has.Count.EqualTo(1),
                "A failed precondition must not expose either batch row.");
        }

        [Test]
        public async Task Replayed_Conditional_Batch_Is_A_No_Op()
        {
            await Ledger.GrantAsync(Grant("p1", "gold", 100, "quest", "quest-1"));
            EntitlementBatch batch = PurchaseBatch("purchase-1");

            EntitlementBatchResult first = await Ledger.ApplyBatchAsync(batch);
            EntitlementBatchResult second = await Ledger.ApplyBatchAsync(batch);

            Assert.Multiple(() =>
            {
                Assert.That(first.Applied, Is.True);
                Assert.That(second.Duplicate, Is.True);
            });
            Assert.That(await Ledger.BalanceAsync("p1", "gold"), Is.EqualTo(0));
            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(1));
            Assert.That(await Ledger.HistoryAsync("p1"), Has.Count.EqualTo(3));
        }

        [Test]
        public async Task Reused_Batch_Id_With_Different_Deltas_Is_A_Conflict()
        {
            await Ledger.GrantAsync(Grant("p1", "gold", 200, "quest", "quest-1"));
            await Ledger.ApplyBatchAsync(PurchaseBatch("purchase-1"));
            var conflicting = new EntitlementBatch(
                "p1",
                "purchase-1",
                new List<EntitlementDelta>
                {
                    new EntitlementDelta("gold", -100, "pack_purchase"),
                    new EntitlementDelta("pack_other", 1, "pack_purchase"),
                },
                new EntitlementBalanceRequirement("gold", 100),
                At);

            EntitlementBatchResult result = await Ledger.ApplyBatchAsync(conflicting);

            Assert.That(result.Conflict, Is.True);
            Assert.That(await Ledger.BalanceAsync("p1", "gold"), Is.EqualTo(100));
            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(1));
            Assert.That(await Ledger.BalanceAsync("p1", "pack_other"), Is.EqualTo(0));
        }

        [Test]
        public async Task Concurrent_Conditional_Batches_Cannot_Overspend_One_Balance()
        {
            await Ledger.GrantAsync(Grant("p1", "gold", 100, "quest", "quest-1"));

            EntitlementBatchResult[] results = await Task.WhenAll(
                Ledger.ApplyBatchAsync(PurchaseBatch("purchase-1")),
                Ledger.ApplyBatchAsync(PurchaseBatch("purchase-2")));

            int applied = (results[0].Applied ? 1 : 0) + (results[1].Applied ? 1 : 0);
            int insufficient = (results[0].InsufficientBalance ? 1 : 0) +
                               (results[1].InsufficientBalance ? 1 : 0);
            Assert.Multiple(() =>
            {
                Assert.That(applied, Is.EqualTo(1));
                Assert.That(insufficient, Is.EqualTo(1));
            });
            Assert.That(await Ledger.BalanceAsync("p1", "gold"), Is.EqualTo(0));
            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(1));
            Assert.That(await Ledger.HistoryAsync("p1"), Has.Count.EqualTo(3));
        }

        [Test]
        public async Task Player_Mutation_Lock_Serializes_Cross_Store_Workflows()
        {
            int active = 0;
            int maximumActive = 0;

            async Task HoldLock()
            {
                await using IAsyncDisposable held =
                    await Ledger.AcquirePlayerMutationLockAsync("p1");
                int current = Interlocked.Increment(ref active);
                int observed;
                do
                {
                    observed = maximumActive;
                    if (observed >= current) break;
                }
                while (Interlocked.CompareExchange(
                    ref maximumActive, current, observed) != observed);

                await Task.Delay(25);
                Interlocked.Decrement(ref active);
            }

            await Task.WhenAll(HoldLock(), HoldLock());

            Assert.That(maximumActive, Is.EqualTo(1));
        }

        [Test]
        public async Task Replayed_External_Txn_Id_Grants_Once()
        {
            EntitlementGrant g = Grant("p1", "pack_core", 5, "play_billing", "receipt-abc");

            GrantResult first = await Ledger.GrantAsync(g);
            GrantResult second = await Ledger.GrantAsync(g);   // the same receipt, delivered twice

            Assert.That(first.Applied, Is.True, "the first insert of a receipt applies");
            Assert.That(second.Duplicate, Is.True, "the replay is a no-op");
            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(5),
                "a replayed receipt grants EXACTLY once");
            Assert.That((await Ledger.HistoryAsync("p1")).Count, Is.EqualTo(1),
                "the ledger appended exactly one line");
        }

        [Test]
        public async Task Refund_Is_A_Negative_Grant()
        {
            await Ledger.GrantAsync(Grant("p1", "gold", 100, "play_billing", "buy-1"));
            await Ledger.GrantAsync(Grant("p1", "gold", -100, "refund", "refund-of-buy-1"));

            Assert.That(await Ledger.BalanceAsync("p1", "gold"), Is.EqualTo(0),
                "a refund is a negative grant that nets the balance back to zero");
            Assert.That((await Ledger.HistoryAsync("p1")).Count, Is.EqualTo(2),
                "both lines are kept — nothing is ever mutated");
        }

        [Test]
        public async Task Balance_Is_A_Projection_Over_Many_Grants()
        {
            await Ledger.GrantAsync(Grant("p1", "dust", 40, "promo", "g1"));
            await Ledger.GrantAsync(Grant("p1", "dust", 40, "play_billing", "g2"));
            await Ledger.GrantAsync(Grant("p1", "dust", -30, "refund", "g3"));

            Assert.That(await Ledger.BalanceAsync("p1", "dust"), Is.EqualTo(50),
                "balance is the signed sum of every grant, computed on read");
        }

        [Test]
        public async Task Balances_Groups_By_Item()
        {
            await Ledger.GrantAsync(Grant("p1", "pack_core", 3, "play_billing", "b1"));
            await Ledger.GrantAsync(Grant("p1", "gold", 500, "play_billing", "b2"));

            var balances = await Ledger.BalancesAsync("p1");
            Assert.That(balances["pack_core"], Is.EqualTo(3));
            Assert.That(balances["gold"], Is.EqualTo(500));
        }

        [Test]
        public async Task Players_Are_Isolated()
        {
            await Ledger.GrantAsync(Grant("p1", "pack_core", 5, "play_billing", "p1-buy"));
            await Ledger.GrantAsync(Grant("p2", "pack_core", 2, "play_billing", "p2-buy"));

            Assert.That(await Ledger.BalanceAsync("p1", "pack_core"), Is.EqualTo(5));
            Assert.That(await Ledger.BalanceAsync("p2", "pack_core"), Is.EqualTo(2));
        }

        [Test]
        public async Task Unknown_Player_Or_Item_Is_Zero()
        {
            Assert.That(await Ledger.BalanceAsync("nobody", "pack_core"), Is.EqualTo(0));
            Assert.That(await Ledger.HistoryAsync("nobody"), Is.Empty);
        }

        [Test]
        public void Grant_Requires_A_Non_Empty_External_Txn_Id()
        {
            // The idempotency key is mandatory — an empty one would silently defeat replay protection.
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await Ledger.GrantAsync(Grant("p1", "pack_core", 1, "play_billing", "")));
        }
    }

    /// <summary>The in-memory implementation against the shared contract. Always runs.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class InMemoryEntitlementLedgerContractTests : EntitlementLedgerContractTests
    {
        private InMemoryEntitlementLedger _ledger = null!;
        protected override IEntitlementLedger Ledger => _ledger;

        [SetUp]
        public void SetUp() => _ledger = new InMemoryEntitlementLedger();
    }

    /// <summary>
    /// The identical suite against Postgres. Gated on a dedicated, throwaway <c>EMBERFALL_LEDGER_DB</c>:
    /// with none set the fixture is Ignored (see <see cref="PostgresProbe"/>); with one set it applies
    /// the schema once and truncates before each test so every case gets an empty ledger.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class PostgresEntitlementLedgerContractTests : EntitlementLedgerContractTests
    {
        private string _conn = null!;
        private PostgresEntitlementLedger _ledger = null!;
        protected override IEntitlementLedger Ledger => _ledger;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _conn = PostgresProbe.RequireOrIgnore();
            _ledger = new PostgresEntitlementLedger(_conn);
            await _ledger.EnsureSchemaAsync();
        }

        [SetUp]
        public async Task SetUp() => await PostgresProbe.TruncateAsync(_conn);
    }
}
