using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>EF-080c reconciliation report — unit tests against the stub feed and pure reconciler.</summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class StoreReconcileTests
    {
        // ---------------------------------------------------------------- StoreReconciler

        [Test]
        public void Clean_When_Every_Purchase_Has_A_Grant_And_Every_Refund_Has_A_Clawback()
        {
            var txns = new[]
            {
                Purchase("txn-1", "p1"),
                Refund("ref-1", "txn-1", "p1"),
            };
            var grants = new[]
            {
                Grant("store:txn-1:0", "p1", "gems", 200),
                Grant("refund:ref-1:0", "p1", "gems", -200),
            };

            ReconciliationReport report = StoreReconciler.Run(txns, grants);

            Assert.That(report.OwedGoods, Is.Empty, "no owed goods on a clean pair");
            Assert.That(report.Investigate, Is.Empty, "no investigate on a clean pair");
            Assert.That(report.OwedClawback, Is.Empty, "no owed clawback on a clean pair");
            Assert.That(report.HasIssues, Is.False);
        }

        [Test]
        public void Owed_Goods_When_Purchase_Has_No_Ledger_Grant()
        {
            var txns = new[]
            {
                Purchase("txn-missing", "p1"),
            };
            var grants = Array.Empty<LedgerStoreGrant>();

            ReconciliationReport report = StoreReconciler.Run(txns, grants);

            Assert.That(report.OwedGoods, Has.Count.EqualTo(1));
            Assert.That(report.OwedGoods[0].StoreTxnId, Is.EqualTo("txn-missing"));
            Assert.That(report.OwedGoods[0].PlayerId, Is.EqualTo("p1"));
            Assert.That(report.Investigate, Is.Empty);
            Assert.That(report.OwedClawback, Is.Empty);
        }

        [Test]
        public void Investigate_When_Ledger_Has_Grant_With_No_Store_Transaction()
        {
            var txns = Array.Empty<StoreTransaction>();
            var grants = new[]
            {
                Grant("store:txn-orphan:0", "p2", "gems", 550),
            };

            ReconciliationReport report = StoreReconciler.Run(txns, grants);

            Assert.That(report.Investigate, Has.Count.EqualTo(1));
            Assert.That(report.Investigate[0].BaseTxnId, Is.EqualTo("txn-orphan"));
            Assert.That(report.Investigate[0].PlayerId, Is.EqualTo("p2"));
            Assert.That(report.Investigate[0].LedgerRowCount, Is.EqualTo(1));
            Assert.That(report.OwedGoods, Is.Empty);
            Assert.That(report.OwedClawback, Is.Empty);
        }

        [Test]
        public void Owed_Clawback_When_Refund_Has_No_Ledger_Clawback()
        {
            var txns = new[]
            {
                Purchase("txn-1", "p1"),
                Refund("ref-missing", "txn-1", "p1"),
            };
            var grants = new[]
            {
                Grant("store:txn-1:0", "p1", "gems", 200),
                // no refund:ref-missing row
            };

            ReconciliationReport report = StoreReconciler.Run(txns, grants);

            Assert.That(report.OwedGoods, Is.Empty);
            Assert.That(report.Investigate, Is.Empty);
            Assert.That(report.OwedClawback, Has.Count.EqualTo(1));
            Assert.That(report.OwedClawback[0].StoreRefundId, Is.EqualTo("ref-missing"));
            Assert.That(report.OwedClawback[0].OriginalTxnId, Is.EqualTo("txn-1"));
            Assert.That(report.OwedClawback[0].PlayerId, Is.EqualTo("p1"));
        }

        [Test]
        public void Multi_Item_Purchase_Batch_Reports_One_Investigate_Finding_Per_Base_Txn_Id()
        {
            // A hypothetical multi-item batch would produce :0, :1, :2 rows in the ledger.
            var txns = Array.Empty<StoreTransaction>();
            var grants = new[]
            {
                Grant("store:txn-batch:0", "p3", "gems", 200),
                Grant("store:txn-batch:1", "p3", "gold", 500),
                Grant("store:txn-batch:2", "p3", "pack_core", 1),
            };

            ReconciliationReport report = StoreReconciler.Run(txns, grants);

            Assert.That(report.Investigate, Has.Count.EqualTo(1), "one finding per base txn, not per ledger row");
            Assert.That(report.Investigate[0].BaseTxnId, Is.EqualTo("txn-batch"));
            Assert.That(report.Investigate[0].LedgerRowCount, Is.EqualTo(3));
        }

        [Test]
        public void All_Three_Findings_In_One_Report()
        {
            var txns = new[]
            {
                Purchase("txn-present", "p1"),   // clean — grant exists
                Purchase("txn-missing", "p2"),   // OWED GOODS — no grant
                Refund("ref-done", "txn-present", "p1"),  // clean — clawback exists
                Refund("ref-missing", "txn-present", "p1"), // OWED CLAWBACK — no clawback
            };
            var grants = new[]
            {
                Grant("store:txn-present:0", "p1", "gems", 200), // matches txn-present
                Grant("store:txn-orphan:0", "p3", "gems", 1200), // INVESTIGATE — no store txn
                Grant("refund:ref-done:0", "p1", "gems", -200),  // matches ref-done
            };

            ReconciliationReport report = StoreReconciler.Run(txns, grants);

            Assert.That(report.OwedGoods, Has.Count.EqualTo(1));
            Assert.That(report.OwedGoods[0].StoreTxnId, Is.EqualTo("txn-missing"));

            Assert.That(report.Investigate, Has.Count.EqualTo(1));
            Assert.That(report.Investigate[0].BaseTxnId, Is.EqualTo("txn-orphan"));

            Assert.That(report.OwedClawback, Has.Count.EqualTo(1));
            Assert.That(report.OwedClawback[0].StoreRefundId, Is.EqualTo("ref-missing"));

            Assert.That(report.HasIssues, Is.True);
        }

        // ---------------------------------------------------------------- TryExtractBaseId

        [Test]
        public void TryExtractBaseId_Parses_Standard_Store_Format()
        {
            Assert.That(
                StoreReconciler.TryExtractBaseId("store:abc123:0", StoreReconciler.StoreTxnPrefix),
                Is.EqualTo("abc123"));
        }

        [Test]
        public void TryExtractBaseId_Parses_Refund_Format()
        {
            Assert.That(
                StoreReconciler.TryExtractBaseId("refund:ref-xyz:0", StoreReconciler.RefundTxnPrefix),
                Is.EqualTo("ref-xyz"));
        }

        [Test]
        public void TryExtractBaseId_Handles_Txn_Id_With_Colons()
        {
            // If a store txn id itself contained colons the last-colon approach still extracts correctly.
            Assert.That(
                StoreReconciler.TryExtractBaseId("store:a:b:c:0", StoreReconciler.StoreTxnPrefix),
                Is.EqualTo("a:b:c"));
        }

        [Test]
        public void TryExtractBaseId_Returns_Null_For_Wrong_Prefix()
        {
            Assert.That(
                StoreReconciler.TryExtractBaseId("refund:r1:0", StoreReconciler.StoreTxnPrefix),
                Is.Null);
        }

        [Test]
        public void TryExtractBaseId_Returns_Null_For_Malformed_Id_Without_Batch_Colon()
        {
            // A row with no batch index (e.g., just "store:abc") is malformed and returns null.
            Assert.That(
                StoreReconciler.TryExtractBaseId("store:abc", StoreReconciler.StoreTxnPrefix),
                Is.Null);
        }

        [Test]
        public void Refund_Of_A_Purchase_Never_Granted_Owes_Nothing()
        {
            // Play refunded an order we never granted (it was never redeemed): nothing to reverse.
            var txns = new[] { Refund("order-never-granted", "order-never-granted", "") };

            ReconciliationReport report = StoreReconciler.Run(txns, Array.Empty<LedgerStoreGrant>());

            Assert.That(report.HasIssues, Is.False);
        }

        // ---------------------------------------------------------------- helpers

        private static StoreTransaction Purchase(string txnId, string playerId)
            => new StoreTransaction(StoreTransactionKind.Purchase, txnId, "", playerId);

        private static StoreTransaction Refund(string refundId, string originalTxnId, string playerId)
            => new StoreTransaction(StoreTransactionKind.Refund, refundId, originalTxnId, playerId);

        private static LedgerStoreGrant Grant(string externalTxnId, string playerId, string item, long qty)
            => new LedgerStoreGrant(externalTxnId, playerId, item, qty, "store_receipt:google");

        // ---------------------------------------------------------------- PlayStoreFeed

        private const long Now = 1_790_000_000_000;

        [Test]
        public async Task Play_Feed_Reconciles_Clean_When_Every_Refund_Was_Clawed_Back()
        {
            var play = new FakePlay
            {
                States = { ["GPA.1"] = "PROCESSED", ["GPA.2"] = "REFUNDED" },
                Voided = { new PlayVoidedPurchase("GPA.2", "tok-2", Now - 1000, 1) },
            };
            var grants = new[]
            {
                Grant("store:GPA.1:0", "p1", "gems", 200),
                Grant("store:GPA.2:0", "p2", "gems", 550),
                Grant("refund:GPA.2:0", "p2", "gems", -550),
            };

            PlayStoreFeedResult feed = await PlayStoreFeed.BuildAsync(play, grants, Now);
            ReconciliationReport report = StoreReconciler.Run(feed.Transactions, grants);

            Assert.That(report.HasIssues, Is.False);
            Assert.That(feed.Transactions, Has.Count.EqualTo(3), "two purchases and one refund, the refund named once");
            Assert.That(play.VoidedSince, Is.EqualTo(Now - 30L * 24 * 3600 * 1000));
        }

        [Test]
        public async Task Play_Feed_Names_A_Missed_Clawback_And_An_Order_Play_Does_Not_Know()
        {
            var play = new FakePlay
            {
                States = { ["GPA.1"] = "PROCESSED" },
                Voided = { new PlayVoidedPurchase("GPA.1", "tok-1", Now - 1000, 7) },
            };
            var grants = new[]
            {
                Grant("store:GPA.1:0", "p1", "gems", 200),
                Grant("store:GPA.ghost:0", "p3", "gems", 1200),
            };

            PlayStoreFeedResult feed = await PlayStoreFeed.BuildAsync(play, grants, Now);
            ReconciliationReport report = StoreReconciler.Run(feed.Transactions, grants);

            Assert.That(report.OwedClawback, Has.Count.EqualTo(1));
            Assert.That(report.OwedClawback[0].StoreRefundId, Is.EqualTo("GPA.1"));
            Assert.That(report.OwedClawback[0].PlayerId, Is.EqualTo("p1"));
            Assert.That(report.Investigate, Has.Count.EqualTo(1));
            Assert.That(report.Investigate[0].BaseTxnId, Is.EqualTo("GPA.ghost"));
            Assert.That(report.OwedGoods, Is.Empty);
        }

        [Test]
        public async Task Play_Feed_Lists_An_Ungranted_Purchase_Play_Refunded_For_Want_Of_An_Acknowledgement()
        {
            var play = new FakePlay
            {
                Voided =
                {
                    new PlayVoidedPurchase("GPA.never", "tok-n", Now - 1000, 8),
                    new PlayVoidedPurchase("GPA.remorse", "tok-r", Now - 1000, 1),
                },
            };

            PlayStoreFeedResult feed = await PlayStoreFeed.BuildAsync(play, Array.Empty<LedgerStoreGrant>(), Now);
            ReconciliationReport report = StoreReconciler.Run(feed.Transactions, Array.Empty<LedgerStoreGrant>());

            Assert.That(feed.AutoRefundedUnacknowledged.Select(v => v.OrderId), Is.EqualTo(new[] { "GPA.never" }));
            Assert.That(report.HasIssues, Is.False, "a refund of an ungranted order owes no clawback");
        }

        private sealed class FakePlay : IPlayOrders
        {
            public Dictionary<string, string> States { get; } = new Dictionary<string, string>();
            public List<PlayVoidedPurchase> Voided { get; } = new List<PlayVoidedPurchase>();
            public long VoidedSince { get; private set; }

            public Task<string?> GetOrderStateAsync(string orderId, CancellationToken ct = default)
                => Task.FromResult(States.TryGetValue(orderId, out string? s) ? s : null);

            public Task<IReadOnlyList<PlayVoidedPurchase>> ListVoidedAsync(long sinceMs, CancellationToken ct = default)
            {
                VoidedSince = sinceMs;
                return Task.FromResult((IReadOnlyList<PlayVoidedPurchase>)Voided);
            }
        }
    }
}
