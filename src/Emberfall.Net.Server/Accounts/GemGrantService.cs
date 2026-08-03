using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// An abstract purchasable bundle. Platform SKU ids bind to these keys in a store adapter; they
    /// are intentionally absent from the server's economy domain.
    /// </summary>
    public sealed record GemBundleDefinition(string ProductKey, long Gems, int UsdPriceCents);

    public static class GemBundleCatalog
    {
        private static readonly IReadOnlyList<GemBundleDefinition> Bundles =
            Array.AsReadOnly(new[]
            {
                new GemBundleDefinition("gems.tier.200", 200, 199),
                new GemBundleDefinition("gems.tier.550", 550, 499),
                new GemBundleDefinition("gems.tier.1200", 1200, 999),
                new GemBundleDefinition("gems.tier.2600", 2600, 1999),
                new GemBundleDefinition("gems.tier.7000", 7000, 4999),
                new GemBundleDefinition("gems.tier.15000", 15000, 9999),
            });

        public static IReadOnlyList<GemBundleDefinition> All => Bundles;

        public static bool TryGet(string productKey, out GemBundleDefinition definition)
        {
            for (int i = 0; i < Bundles.Count; i++)
            {
                if (!string.Equals(Bundles[i].ProductKey, productKey, StringComparison.Ordinal)) continue;
                definition = Bundles[i];
                return true;
            }
            definition = null!;
            return false;
        }
    }

    /// <summary>An opaque client receipt sent to one platform-specific server validator.</summary>
    public sealed record StoreReceipt(string Provider, string Payload);

    /// <summary>
    /// The only facts the economy trusts after a platform adapter validates a receipt, including the
    /// authenticated player identity bound into the store transaction.
    /// </summary>
    public sealed record ValidatedStorePurchase(
        string Provider,
        string StoreTransactionId,
        string ProductKey,
        string PlayerId);

    public sealed class StoreReceiptValidation
    {
        public bool Accepted { get; }
        public ValidatedStorePurchase? Purchase { get; }
        public string RejectionReason { get; }

        private StoreReceiptValidation(
            bool accepted,
            ValidatedStorePurchase? purchase,
            string rejectionReason)
        {
            Accepted = accepted;
            Purchase = purchase;
            RejectionReason = rejectionReason;
        }

        public static StoreReceiptValidation Valid(ValidatedStorePurchase purchase)
            => new StoreReceiptValidation(
                true,
                purchase ?? throw new ArgumentNullException(nameof(purchase)),
                "");

        public static StoreReceiptValidation Invalid(string reason)
            => new StoreReceiptValidation(false, null, reason ?? "");
    }

    /// <summary>
    /// Platform adapter seam. Implementations must verify the receipt with the store before returning
    /// an accepted purchase; the economy never grants from client-asserted product or quantity data.
    /// </summary>
    public interface IStoreReceiptValidator
    {
        Task<StoreReceiptValidation> ValidateAsync(
            StoreReceipt receipt,
            CancellationToken ct = default);
    }

    public enum GemGrantStatus
    {
        Granted,
        Replayed,
        Rejected,
    }

    public sealed class GemGrantOutcome
    {
        public GemGrantStatus Status { get; }
        public WalletBalances Wallet { get; }
        public string RejectionReason { get; }

        public GemGrantOutcome(
            GemGrantStatus status,
            WalletBalances wallet,
            string rejectionReason = "")
        {
            Status = status;
            Wallet = wallet;
            RejectionReason = rejectionReason;
        }
    }

    /// <summary>
    /// Validates store receipts before crossing the replay-once entitlement-ledger seam. The store
    /// transaction id, not receipt bytes or a client-generated id, anchors idempotency. A validated
    /// purchase must also be bound to the same authenticated player that receives the Gems.
    /// </summary>
    public sealed class GemGrantService
    {
        private readonly IEntitlementLedger _ledger;
        private readonly IStoreReceiptValidator _validator;
        private readonly ITimeSource _time;
        private readonly EconomyService _economy;

        public GemGrantService(
            IEntitlementLedger ledger,
            IStoreReceiptValidator validator,
            ITimeSource time)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _economy = new EconomyService(ledger, time);
        }

        public async Task<GemGrantOutcome> GrantAsync(
            Account account,
            StoreReceipt receipt,
            CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            if (string.IsNullOrWhiteSpace(receipt.Provider))
                throw new ArgumentException("A receipt provider is required.", nameof(receipt));
            if (string.IsNullOrWhiteSpace(receipt.Payload))
                throw new ArgumentException("Receipt payload is required.", nameof(receipt));
            EconomyAccountPolicy.RequireDurable(account);

            StoreReceiptValidation validation =
                await _validator.ValidateAsync(receipt, ct).ConfigureAwait(false);
            if (validation == null)
                return await RejectedAsync(account, "validator_returned_no_result", ct).ConfigureAwait(false);
            if (!validation.Accepted || validation.Purchase == null)
                return await RejectedAsync(account, validation.RejectionReason, ct).ConfigureAwait(false);

            ValidatedStorePurchase purchase = validation.Purchase;
            if (!string.Equals(purchase.Provider, receipt.Provider, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(purchase.StoreTransactionId) ||
                !string.Equals(purchase.PlayerId, account.PlayerId, StringComparison.Ordinal) ||
                !GemBundleCatalog.TryGet(purchase.ProductKey, out GemBundleDefinition bundle))
                return await RejectedAsync(account, "invalid_validated_purchase", ct).ConfigureAwait(false);

            string source = "store_receipt:" + purchase.Provider;
            var batch = new EntitlementBatch(
                account.PlayerId,
                "store:" + purchase.StoreTransactionId,
                new[] { new EntitlementDelta(EconomyItems.Gems, bundle.Gems, source) },
                requirement: null,
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));
            EntitlementBatchResult result =
                await _ledger.ApplyBatchAsync(batch, ct).ConfigureAwait(false);
            if (result.Conflict)
                throw new InvalidOperationException(
                    "The store transaction id is already bound to a different Gem grant.");

            WalletBalances wallet = await _economy.GetWalletAsync(account, ct).ConfigureAwait(false);
            return new GemGrantOutcome(
                result.Applied ? GemGrantStatus.Granted : GemGrantStatus.Replayed,
                wallet);
        }

        private async Task<GemGrantOutcome> RejectedAsync(
            Account account,
            string reason,
            CancellationToken ct)
            => new GemGrantOutcome(
                GemGrantStatus.Rejected,
                await _economy.GetWalletAsync(account, ct).ConfigureAwait(false),
                reason);
    }
}
