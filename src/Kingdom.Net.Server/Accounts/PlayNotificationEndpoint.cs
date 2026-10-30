using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Accounts
{
    public sealed record PlayNotificationResult(int HttpStatus, string Status);

    /// <summary>
    /// <c>POST /store/play/notifications</c>: Google Play's Real-time developer notifications,
    /// delivered by a Pub/Sub push subscription. An <b>external webhook</b>: nothing in the body is
    /// trusted until the bearer verifies as the subscription's own service account
    /// (<see cref="IPushTokenVerifier"/>), and even then the body only names an order or a token —
    /// what was bought and by whom is read from our ledger or from Play itself.
    ///
    /// <para><b>Two events act.</b> A voided one-time purchase (refund, chargeback, revocation)
    /// claws its Gems back through <see cref="RefundService"/>, keyed on Play's order id, so a
    /// redelivered or partially voided order writes once. A completed one-time purchase is granted
    /// and acknowledged exactly as <c>/shop/gems</c> would, which is what lands a deferred purchase
    /// that clears while the app is closed before Play's three-day auto-refund. Everything else is
    /// acknowledged and ignored.</para>
    ///
    /// <para><b>The status code is Pub/Sub's ack.</b> 2xx removes the message; anything else is
    /// redelivered for up to seven days. So a message that can never succeed (wrong package, an
    /// order we never granted) is acked with a log line, and only a transient failure — Play or the
    /// database unreachable — answers 503.</para>
    /// </summary>
    public sealed class PlayNotificationEndpoint
    {
        public const int MaxBodyBytes = 64 * 1024;

        private readonly IPushTokenVerifier _verifier;
        private readonly string _packageName;
        private readonly IEntitlementLedger _ledger;
        private readonly IAccountStore _accounts;
        private readonly RefundService _refunds;
        private readonly IStoreReceiptValidator _validator;
        private readonly GemGrantService _grants;
        private readonly IStorePurchaseAcknowledger? _acknowledger;

        public PlayNotificationEndpoint(
            IPushTokenVerifier verifier, string packageName, IEntitlementLedger ledger, IAccountStore accounts,
            IStoreReceiptValidator validator, IStorePurchaseAcknowledger? acknowledger, ITimeSource time)
        {
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _packageName = packageName ?? throw new ArgumentNullException(nameof(packageName));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _acknowledger = acknowledger;
            _refunds = new RefundService(ledger, time);
            _grants = new GemGrantService(ledger, validator, time);
        }

        /// <param name="body">Null when the body exceeded <see cref="MaxBodyBytes"/>.</param>
        public async Task<PlayNotificationResult> HandleAsync(
            string? bearer, ReadOnlyMemory<byte>? body, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearer)) return new PlayNotificationResult(401, "unauthenticated");
            PushTokenVerdict verdict = await _verifier.VerifyAsync(bearer!, ct).ConfigureAwait(false);
            if (verdict == PushTokenVerdict.Unavailable) return new PlayNotificationResult(503, "unavailable");
            if (verdict != PushTokenVerdict.Accepted) return new PlayNotificationResult(401, "unauthenticated");

            if (body == null) return Dropped("oversized");
            if (!TryReadNotification(body.Value, out JsonDocument? doc)) return Dropped("malformed");
            using (doc)
            {
                JsonElement n = doc!.RootElement;
                if (!TryString(n, "packageName", out string package) ||
                    !string.Equals(package, _packageName, StringComparison.Ordinal))
                    return Dropped("wrong_package");

                try
                {
                    if (n.TryGetProperty("voidedPurchaseNotification", out JsonElement voided))
                        return await VoidedAsync(voided, ct).ConfigureAwait(false);
                    if (n.TryGetProperty("oneTimeProductNotification", out JsonElement oneTime))
                        return await OneTimeAsync(oneTime, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Play unreachable, the ledger down: redeliver. A refund must not be lost to a blip.
                    Console.Error.WriteLine("[play-rtdn] transient failure, redelivery asked: {0}: {1}",
                        ex.GetType().Name, ex.Message);
                    return new PlayNotificationResult(503, "unavailable");
                }

                if (n.TryGetProperty("testNotification", out _))
                {
                    Console.WriteLine("[play-rtdn] test notification received");
                    return new PlayNotificationResult(200, "test");
                }
                return new PlayNotificationResult(200, "ignored");
            }
        }

        private async Task<PlayNotificationResult> VoidedAsync(JsonElement voided, CancellationToken ct)
        {
            // 2 is a one-time product; 1, a subscription, is nothing we sell.
            if (!TryInt(voided, "productType", out int productType) || productType != 2)
                return new PlayNotificationResult(200, "ignored");
            if (!TryString(voided, "orderId", out string orderId)) return Dropped("voided_without_order");

            // The ledger, not Play, says whose purchase this was: the voided notification carries no
            // product and no account, and the grant row names both.
            EntitlementGrant? purchase = await _ledger.FindAsync(GemGrantService.PurchaseRowId(orderId), ct)
                .ConfigureAwait(false);
            if (purchase == null)
            {
                // Paid and refunded before any grant, or never redeemed: nothing to claw back.
                Console.WriteLine("[play-rtdn] voided order {0} was never granted; nothing to reverse", orderId);
                return new PlayNotificationResult(200, "not_granted");
            }
            Account? account = await _accounts.FindByPlayerIdAsync(purchase.PlayerId, ct).ConfigureAwait(false);
            if (account == null)
            {
                Console.Error.WriteLine("[play-rtdn] voided order {0}: grant names player_id={1}, which has no account",
                    orderId, purchase.PlayerId);
                return new PlayNotificationResult(200, "no_account");
            }

            // A quantity-based partial refund voids the same order again; gem tiers are single-quantity,
            // so the first void reverses the whole grant and the order id makes any repeat a replay.
            RefundOutcome outcome = await _refunds.RefundAsync(
                account, new ValidatedStoreRefund(orderId, orderId, account.PlayerId), ct).ConfigureAwait(false);
            Console.WriteLine("[play-rtdn] voided order {0}: {1} for player_id={2}, gems now {3}",
                orderId, outcome.Status, account.PlayerId, outcome.Wallet.Gems);
            return new PlayNotificationResult(200, outcome.Status.ToString().ToLowerInvariant());
        }

        private async Task<PlayNotificationResult> OneTimeAsync(JsonElement oneTime, CancellationToken ct)
        {
            // 1 purchased; 2 a pending purchase canceled, which wrote nothing.
            if (!TryInt(oneTime, "notificationType", out int type) || type != 1)
                return new PlayNotificationResult(200, "ignored");
            if (!TryString(oneTime, "sku", out string productId) ||
                !TryString(oneTime, "purchaseToken", out string token))
                return Dropped("purchase_without_token");

            var receipt = new StoreReceipt(PlayStoreReceiptValidator.Provider,
                JsonSerializer.Serialize(new { productId, purchaseToken = token }));
            StoreReceiptValidation validation = await _validator.ValidateAsync(receipt, ct).ConfigureAwait(false);
            if (validation.Status != StoreReceiptStatus.Accepted || validation.Purchase == null)
                return new PlayNotificationResult(200, "not_paid");

            Account? account = await _accounts.FindByPlayerIdAsync(validation.Purchase.PlayerId, ct).ConfigureAwait(false);
            if (account == null)
            {
                Console.Error.WriteLine("[play-rtdn] purchase {0} names no account; left for the client to redeem",
                    validation.Purchase.StoreTransactionId);
                return new PlayNotificationResult(200, "no_account");
            }

            GemGrantOutcome outcome;
            try
            {
                outcome = await _grants.GrantAsync(account, receipt, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // The order id is already bound to a different grant. Redelivery cannot fix that.
                Console.Error.WriteLine("[play-rtdn] purchase {0}: {1}", validation.Purchase.StoreTransactionId, ex.Message);
                return new PlayNotificationResult(200, "conflict");
            }
            if ((outcome.Status == GemGrantStatus.Granted || outcome.Status == GemGrantStatus.Replayed) &&
                _acknowledger != null)
            {
                // After the ledger write, as /shop/gems does. A failure costs nothing: the client's
                // consume acknowledges too.
                try { await _acknowledger.AcknowledgeAsync(receipt, ct).ConfigureAwait(false); }
                catch (StoreUnavailableException ex)
                {
                    Console.Error.WriteLine("[play-rtdn] acknowledge failed for {0}: {1}",
                        validation.Purchase.StoreTransactionId, ex.Message);
                }
            }
            Console.WriteLine("[play-rtdn] purchase {0}: {1} for player_id={2}",
                validation.Purchase.StoreTransactionId, outcome.Status, account.PlayerId);
            return new PlayNotificationResult(200, outcome.Status.ToString().ToLowerInvariant());
        }

        private static PlayNotificationResult Dropped(string why)
        {
            Console.Error.WriteLine("[play-rtdn] dropped an authenticated message: {0}", why);
            return new PlayNotificationResult(200, why);
        }

        /// <summary>Pub/Sub's push envelope, <c>{"message":{"data":"&lt;base64&gt;"}}</c>, unwrapped to
        /// Play's DeveloperNotification.</summary>
        private static bool TryReadNotification(ReadOnlyMemory<byte> body, out JsonDocument? notification)
        {
            notification = null;
            try
            {
                using JsonDocument envelope = JsonDocument.Parse(body);
                if (envelope.RootElement.ValueKind != JsonValueKind.Object ||
                    !envelope.RootElement.TryGetProperty("message", out JsonElement message) ||
                    !TryString(message, "data", out string data))
                    return false;
                notification = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
                if (notification.RootElement.ValueKind == JsonValueKind.Object) return true;
                notification.Dispose();
                notification = null;
                return false;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            {
                return false;
            }
        }

        private static bool TryString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement e) ||
                e.ValueKind != JsonValueKind.String) return false;
            value = e.GetString() ?? "";
            return value.Length > 0;
        }

        private static bool TryInt(JsonElement obj, string name, out int value)
        {
            value = 0;
            return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement e) &&
                   e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
        }
    }
}
