using System;
using System.Collections.Generic;
using System.IO;
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

        // ---------------------------------------------------------------- StoreTransactionFeed

        [Test]
        public void Feed_Parses_Purchase_And_Refund_Lines()
        {
            string jsonl =
                "{\"kind\":\"purchase\",\"txn_id\":\"t1\",\"player_id\":\"p1\"}\n" +
                "{\"kind\":\"refund\",\"refund_id\":\"r1\",\"original_txn_id\":\"t1\",\"player_id\":\"p1\"}\n";

            string path = WriteTempJsonl(jsonl);
            try
            {
                IReadOnlyList<StoreTransaction> txns = StoreTransactionFeed.LoadJsonl(path);
                Assert.That(txns, Has.Count.EqualTo(2));

                Assert.That(txns[0].Kind, Is.EqualTo(StoreTransactionKind.Purchase));
                Assert.That(txns[0].TxnId, Is.EqualTo("t1"));
                Assert.That(txns[0].PlayerId, Is.EqualTo("p1"));

                Assert.That(txns[1].Kind, Is.EqualTo(StoreTransactionKind.Refund));
                Assert.That(txns[1].TxnId, Is.EqualTo("r1"));
                Assert.That(txns[1].OriginalTxnId, Is.EqualTo("t1"));
                Assert.That(txns[1].PlayerId, Is.EqualTo("p1"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Feed_Skips_Blank_Lines_And_Comments()
        {
            string jsonl =
                "\n" +
                "// this is a comment\n" +
                "{\"kind\":\"purchase\",\"txn_id\":\"t2\",\"player_id\":\"p2\"}\n" +
                "\n";

            string path = WriteTempJsonl(jsonl);
            try
            {
                IReadOnlyList<StoreTransaction> txns = StoreTransactionFeed.LoadJsonl(path);
                Assert.That(txns, Has.Count.EqualTo(1));
                Assert.That(txns[0].TxnId, Is.EqualTo("t2"));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Feed_Throws_FormatException_On_Unknown_Kind()
        {
            string jsonl = "{\"kind\":\"cancel\",\"txn_id\":\"t3\",\"player_id\":\"p3\"}\n";
            string path = WriteTempJsonl(jsonl);
            try
            {
                Assert.Throws<FormatException>(() => StoreTransactionFeed.LoadJsonl(path));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Feed_Throws_FormatException_On_Missing_Required_Field()
        {
            string jsonl = "{\"kind\":\"purchase\",\"player_id\":\"p4\"}\n"; // no txn_id
            string path = WriteTempJsonl(jsonl);
            try
            {
                Assert.Throws<FormatException>(() => StoreTransactionFeed.LoadJsonl(path));
            }
            finally { File.Delete(path); }
        }

        // ---------------------------------------------------------------- helpers

        private static StoreTransaction Purchase(string txnId, string playerId)
            => new StoreTransaction(StoreTransactionKind.Purchase, txnId, "", playerId);

        private static StoreTransaction Refund(string refundId, string originalTxnId, string playerId)
            => new StoreTransaction(StoreTransactionKind.Refund, refundId, originalTxnId, playerId);

        private static LedgerStoreGrant Grant(string externalTxnId, string playerId, string item, long qty)
            => new LedgerStoreGrant(externalTxnId, playerId, item, qty, "store_receipt:google");

        private static string WriteTempJsonl(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), $"store-reconcile-test-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path, content);
            return path;
        }
    }
}
