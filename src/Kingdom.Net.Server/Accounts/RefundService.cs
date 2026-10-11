using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// The authoritative facts from a store refund or chargeback notification, already validated by
    /// the caller. The economy domain never touches raw platform payloads.
    /// </summary>
    public sealed record ValidatedStoreRefund(
        /// <summary>The store's own refund or chargeback id — the idempotency key for the clawback.</summary>
        string StoreRefundId,
        /// <summary>The store transaction id that was purchased and is now being reversed.</summary>
        string OriginalTransactionId,
        /// <summary>The authenticated player whose ledger the clawback writes to.</summary>
        string PlayerId);

    public enum RefundStatus
    {
        /// <summary>Negative grants were written — the ledger balance is reduced by the original purchase amount.</summary>
        Refunded,

        /// <summary>This refund id has already been processed; nothing was written (idempotent replay).</summary>
        Replayed,

        /// <summary>
        /// No ledger rows were found for the original transaction id. This is a reported outcome, not an
        /// exception — the store may deliver a refund notification before the purchase write (race) or
        /// for a transaction the server never saw.
        /// </summary>
        PurchaseNotFound,
    }

    public sealed class RefundOutcome
    {
        public RefundStatus Status { get; }
        public WalletBalances Wallet { get; }

        public RefundOutcome(RefundStatus status, WalletBalances wallet)
        {
            Status = status;
            Wallet = wallet;
        }
    }

    /// <summary>
    /// Turns a validated store refund or chargeback into a set of negative ledger grants that net
    /// the player's balance. The original purchase amounts are read from the ledger, never
    /// recomputed from catalog data, so the clawback is always the exact inverse of what was granted
    /// regardless of catalog changes since the purchase.
    ///
    /// <para><b>Idempotent on the refund id.</b> Delivering the same store refund id twice writes
    /// nothing the second time and returns <see cref="RefundStatus.Replayed"/>.</para>
    ///
    /// <para><b>A clawback past zero takes the wallet negative.</b> The player carries the debt.
    /// The existing insufficient-funds check already prevents spending from a negative balance, and
    /// the next gem purchase nets against it, clearing the debt.</para>
    /// </summary>
    public sealed class RefundService
    {
        private const string RefundSource = "store_refund";

        private readonly IEntitlementLedger _ledger;
        private readonly ITimeSource _time;
        private readonly EconomyService _economy;

        public RefundService(IEntitlementLedger ledger, ITimeSource time)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _economy = new EconomyService(ledger, time);
        }

        public async Task<RefundOutcome> RefundAsync(
            Account account,
            ValidatedStoreRefund refund,
            CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (refund == null) throw new ArgumentNullException(nameof(refund));
            if (string.IsNullOrWhiteSpace(refund.StoreRefundId))
                throw new ArgumentException("A store refund id is required.", nameof(refund));
            if (string.IsNullOrWhiteSpace(refund.OriginalTransactionId))
                throw new ArgumentException("The original transaction id is required.", nameof(refund));
            if (!string.Equals(refund.PlayerId, account.PlayerId, StringComparison.Ordinal))
                throw new ArgumentException("The refund player id must match the account.", nameof(refund));
            EconomyAccountPolicy.RequireDurable(account);

            string purchaseTxnPrefix = "store:" + refund.OriginalTransactionId + ":";
            IReadOnlyList<EntitlementGrant> history =
                await _ledger.HistoryAsync(account.PlayerId, ct).ConfigureAwait(false);

            var originalGrants = new List<EntitlementGrant>();
            foreach (EntitlementGrant g in history)
            {
                if (g.ExternalTxnId.StartsWith(purchaseTxnPrefix, StringComparison.Ordinal))
                    originalGrants.Add(g);
            }

            if (originalGrants.Count == 0)
                return new RefundOutcome(
                    RefundStatus.PurchaseNotFound,
                    await _economy.GetWalletAsync(account, ct).ConfigureAwait(false));

            // Sort by the external txn id so delta order is deterministic and stable across replays
            originalGrants.Sort((a, b) =>
                string.Compare(a.ExternalTxnId, b.ExternalTxnId, StringComparison.Ordinal));

            var deltas = new List<EntitlementDelta>(originalGrants.Count);
            foreach (EntitlementGrant g in originalGrants)
                deltas.Add(new EntitlementDelta(g.Item, -g.Qty, RefundSource));

            var batch = new EntitlementBatch(
                account.PlayerId,
                "refund:" + refund.StoreRefundId,
                deltas,
                requirement: null,
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));

            EntitlementBatchResult result =
                await _ledger.ApplyBatchAsync(batch, ct).ConfigureAwait(false);
            if (result.Conflict)
                throw new InvalidOperationException(
                    "The store refund id is already bound to a different set of clawback grants.");

            return new RefundOutcome(
                result.Applied ? RefundStatus.Refunded : RefundStatus.Replayed,
                await _economy.GetWalletAsync(account, ct).ConfigureAwait(false));
        }
    }
}
