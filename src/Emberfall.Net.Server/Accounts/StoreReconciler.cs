using System;
using System.Collections.Generic;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// EF-080c. A store transaction from the stub feed — either a charge or a refund. A real platform
    /// adapter will replace <see cref="StoreTransactionFeed"/> once EF-080d lands; the reconciler
    /// itself is feed-agnostic (it consumes a list, not a file).
    /// </summary>
    public enum StoreTransactionKind { Purchase, Refund }

    public sealed record StoreTransaction(
        StoreTransactionKind Kind,
        /// <summary>
        /// For a purchase: the store's own transaction id (binds to <c>external_txn_id</c> prefix
        /// <c>store:{TxnId}:</c> in the ledger). For a refund: the store's refund id (binds to
        /// <c>refund:{TxnId}:</c>).
        /// </summary>
        string TxnId,
        /// <summary>Refund only — the purchase transaction id that is being reversed.</summary>
        string OriginalTxnId,
        string PlayerId);

    /// <summary>
    /// One row from <c>entitlement_grant</c> whose <c>external_txn_id</c> starts with <c>store:</c>
    /// or <c>refund:</c>. Passed to <see cref="StoreReconciler"/> already fetched; the reconciler
    /// does no I/O.
    /// </summary>
    public sealed record LedgerStoreGrant(
        string ExternalTxnId,
        string PlayerId,
        string Item,
        long Qty,
        string Source);

    /// <summary>
    /// EF-080c finding: the store charged this player and we have no grant for it.
    /// The player paid and received nothing — we owe them goods.
    /// </summary>
    public sealed record OwedGoodsFinding(string StoreTxnId, string PlayerId);

    /// <summary>
    /// EF-080c finding: the ledger has a store grant for a transaction id that is not in the store
    /// feed. We granted something we have no store record for — needs investigation.
    /// </summary>
    public sealed record InvestigateFinding(
        string BaseTxnId,
        string PlayerId,
        int LedgerRowCount);

    /// <summary>
    /// EF-080c finding: the store issued a refund but our ledger has no matching clawback.
    /// The player was refunded by the store but we never reversed the grant — we owe a clawback.
    /// </summary>
    public sealed record OwedClawbackFinding(
        string StoreRefundId,
        string OriginalTxnId,
        string PlayerId);

    public sealed class ReconciliationReport
    {
        /// <summary>Store charges with no matching ledger grant.</summary>
        public IReadOnlyList<OwedGoodsFinding> OwedGoods { get; }

        /// <summary>Ledger grants with no matching store charge.</summary>
        public IReadOnlyList<InvestigateFinding> Investigate { get; }

        /// <summary>Store refunds with no matching ledger clawback.</summary>
        public IReadOnlyList<OwedClawbackFinding> OwedClawback { get; }

        public bool HasIssues =>
            OwedGoods.Count > 0 || Investigate.Count > 0 || OwedClawback.Count > 0;

        public ReconciliationReport(
            IReadOnlyList<OwedGoodsFinding> owedGoods,
            IReadOnlyList<InvestigateFinding> investigate,
            IReadOnlyList<OwedClawbackFinding> owedClawback)
        {
            OwedGoods = owedGoods;
            Investigate = investigate;
            OwedClawback = owedClawback;
        }
    }

    /// <summary>
    /// EF-080c. Pure, read-only reconciliation function — no DB access, no side effects.
    ///
    /// <para>The ledger uses a keying convention that the reconciler depends on:
    /// <list type="bullet">
    /// <item>A store purchase batch has <c>external_txn_id</c> = <c>store:{storeTransactionId}:{batchIndex}</c>.</item>
    /// <item>A clawback batch has <c>external_txn_id</c> = <c>refund:{storeRefundId}:{batchIndex}</c>.</item>
    /// </list>
    /// The base id is everything between the prefix and the last colon. This matches the
    /// <c>StartsWith("store:" + originalTxnId + ":")</c> probe in <see cref="RefundService"/>.
    /// </para>
    ///
    /// <para><b>RETIREMENT TRIGGER</b>: when EF-080d replaces <see cref="StoreTransactionFeed"/> with
    /// a real platform adapter, update the doc comment on <see cref="Run"/> to name the feed type.</para>
    /// </summary>
    public static class StoreReconciler
    {
        internal const string StoreTxnPrefix = "store:";
        internal const string RefundTxnPrefix = "refund:";

        /// <summary>
        /// Run reconciliation. Returns a report with three categories. The caller is responsible for
        /// fetching <paramref name="ledgerGrants"/> — rows where <c>external_txn_id LIKE 'store:%' OR
        /// external_txn_id LIKE 'refund:%'</c>.
        /// </summary>
        public static ReconciliationReport Run(
            IReadOnlyList<StoreTransaction> storeTxns,
            IReadOnlyList<LedgerStoreGrant> ledgerGrants)
        {
            if (storeTxns == null) throw new ArgumentNullException(nameof(storeTxns));
            if (ledgerGrants == null) throw new ArgumentNullException(nameof(ledgerGrants));

            // Index ledger: store grants by base txn id, refund grants by base refund id.
            // Track row count per base id for the InvestigateFinding.
            var storeGrantsByTxnId = new Dictionary<string, (string PlayerId, int Count)>(
                StringComparer.Ordinal);
            var refundGrantIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (LedgerStoreGrant grant in ledgerGrants)
            {
                string? baseTxnId = TryExtractBaseId(grant.ExternalTxnId, StoreTxnPrefix);
                if (baseTxnId != null)
                {
                    if (storeGrantsByTxnId.TryGetValue(baseTxnId, out var existing))
                        storeGrantsByTxnId[baseTxnId] = (existing.PlayerId, existing.Count + 1);
                    else
                        storeGrantsByTxnId[baseTxnId] = (grant.PlayerId, 1);
                    continue;
                }

                string? baseRefundId = TryExtractBaseId(grant.ExternalTxnId, RefundTxnPrefix);
                if (baseRefundId != null)
                    refundGrantIds.Add(baseRefundId);
            }

            // Walk store transactions to build the three finding lists.
            var storePurchaseTxnIds = new HashSet<string>(StringComparer.Ordinal);
            var owedGoods = new List<OwedGoodsFinding>();
            var owedClawback = new List<OwedClawbackFinding>();

            foreach (StoreTransaction txn in storeTxns)
            {
                if (txn.Kind == StoreTransactionKind.Purchase)
                {
                    storePurchaseTxnIds.Add(txn.TxnId);
                    if (!storeGrantsByTxnId.ContainsKey(txn.TxnId))
                        owedGoods.Add(new OwedGoodsFinding(txn.TxnId, txn.PlayerId));
                }
                else
                {
                    if (!refundGrantIds.Contains(txn.TxnId))
                        owedClawback.Add(new OwedClawbackFinding(txn.TxnId, txn.OriginalTxnId, txn.PlayerId));
                }
            }

            // Investigate: ledger has a store grant for a txn id absent from the store feed.
            var investigate = new List<InvestigateFinding>();
            foreach (var kv in storeGrantsByTxnId)
            {
                if (!storePurchaseTxnIds.Contains(kv.Key))
                    investigate.Add(new InvestigateFinding(kv.Key, kv.Value.PlayerId, kv.Value.Count));
            }

            return new ReconciliationReport(
                owedGoods.AsReadOnly(),
                investigate.AsReadOnly(),
                owedClawback.AsReadOnly());
        }

        /// <summary>
        /// Extract the base transaction id from an <c>external_txn_id</c> of the form
        /// <c>{prefix}{baseId}:{batchIndex}</c>. Returns null if the id does not match the prefix or
        /// lacks a batch-index colon.
        /// </summary>
        internal static string? TryExtractBaseId(string externalTxnId, string prefix)
        {
            if (!externalTxnId.StartsWith(prefix, StringComparison.Ordinal)) return null;
            int lastColon = externalTxnId.LastIndexOf(':');
            int afterPrefix = prefix.Length;
            // The last colon must be past the prefix (it separates base id from batch index).
            if (lastColon < afterPrefix) return null;
            return externalTxnId.Substring(afterPrefix, lastColon - afterPrefix);
        }
    }
}
