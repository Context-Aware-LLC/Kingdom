using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>What the reconciliation report asks Play. <see cref="PlayStoreReceiptValidator"/> is the
    /// real one; a seam so the feed's rules are testable without Google.</summary>
    public interface IPlayOrders
    {
        Task<string?> GetOrderStateAsync(string orderId, CancellationToken ct = default);
        Task<IReadOnlyList<PlayVoidedPurchase>> ListVoidedAsync(long sinceMs, CancellationToken ct = default);
    }

    /// <summary>The Play side of <see cref="PlayStoreFeed.BuildAsync"/>, with what it could not put in
    /// the transaction list.</summary>
    public sealed class PlayStoreFeedResult
    {
        public IReadOnlyList<StoreTransaction> Transactions { get; }

        /// <summary>Voided with reason 8: a purchase nobody acknowledged, so Play refunded it by
        /// itself after three days. The player paid and got nothing, then got the money back — the
        /// only trace an ungranted purchase leaves, since Play has no API that lists every order.</summary>
        public IReadOnlyList<PlayVoidedPurchase> AutoRefundedUnacknowledged { get; }

        public PlayStoreFeedResult(IReadOnlyList<StoreTransaction> transactions,
                                   IReadOnlyList<PlayVoidedPurchase> autoRefunded)
        {
            Transactions = transactions;
            AutoRefundedUnacknowledged = autoRefunded;
        }
    }

    /// <summary>
    /// EF-080c's feed from Google Play, for <see cref="StoreReconciler"/>. Play cannot list every
    /// order, so the purchase side starts from the ledger: each <c>store:</c> grant's order is looked
    /// up, and an order Play knows becomes a purchase (one it does not becomes the reconciler's
    /// INVESTIGATE). A refunded order, and every order on the 30-day voided list, becomes a refund
    /// keyed on the order id — the same key the notification path claws back under.
    ///
    /// <para><b>OWED GOODS cannot come out of this feed</b>, by construction: every purchase it names
    /// already has a grant. A paid purchase we never granted is never acknowledged either, and Play
    /// refunds those after three days; they surface as <see cref="PlayStoreFeedResult.AutoRefundedUnacknowledged"/>.</para>
    /// </summary>
    public static class PlayStoreFeed
    {
        /// <summary>Order states that mean Play gave money back. <c>PENDING_REFUND</c> has not yet.</summary>
        private static readonly HashSet<string> RefundedStates = new(StringComparer.Ordinal)
        {
            "REFUNDED", "PARTIALLY_REFUNDED", "CANCELED",
        };

        private const int UnacknowledgedPurchase = 8;

        public static async Task<PlayStoreFeedResult> BuildAsync(
            IPlayOrders play, IReadOnlyList<LedgerStoreGrant> ledgerGrants, long nowMs, CancellationToken ct = default)
        {
            if (play == null) throw new ArgumentNullException(nameof(play));
            if (ledgerGrants == null) throw new ArgumentNullException(nameof(ledgerGrants));

            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (LedgerStoreGrant g in ledgerGrants)
            {
                string? orderId = StoreReconciler.TryExtractBaseId(g.ExternalTxnId, StoreReconciler.StoreTxnPrefix);
                if (orderId != null && !owners.ContainsKey(orderId)) owners[orderId] = g.PlayerId;
            }

            var txns = new List<StoreTransaction>();
            var refunded = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> owner in owners)
            {
                string? state = await play.GetOrderStateAsync(owner.Key, ct).ConfigureAwait(false);
                if (state == null) continue;
                txns.Add(new StoreTransaction(StoreTransactionKind.Purchase, owner.Key, "", owner.Value));
                if (RefundedStates.Contains(state) && refunded.Add(owner.Key))
                    txns.Add(new StoreTransaction(StoreTransactionKind.Refund, owner.Key, owner.Key, owner.Value));
            }

            var autoRefunded = new List<PlayVoidedPurchase>();
            long since = DateTimeOffset.FromUnixTimeMilliseconds(nowMs).AddDays(-30).ToUnixTimeMilliseconds();
            foreach (PlayVoidedPurchase v in await play.ListVoidedAsync(since, ct).ConfigureAwait(false))
            {
                bool granted = owners.TryGetValue(v.OrderId, out string? player);
                if (!granted && v.VoidedReason == UnacknowledgedPurchase) autoRefunded.Add(v);
                if (refunded.Add(v.OrderId))
                    txns.Add(new StoreTransaction(StoreTransactionKind.Refund, v.OrderId, v.OrderId, player ?? ""));
            }

            return new PlayStoreFeedResult(txns.AsReadOnly(), autoRefunded.AsReadOnly());
        }
    }
}
