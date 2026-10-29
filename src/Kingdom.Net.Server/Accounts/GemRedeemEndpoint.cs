using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Accounts
{
    public enum GemRedeemStatus
    {
        Granted,
        Replayed,
        Pending,
        Rejected,
        InvalidRequest,
        Unauthenticated,
        Unavailable,
        Conflict,
    }

    public sealed class GemRedeemResult
    {
        public GemRedeemStatus Status { get; }
        public WalletBalances Wallet { get; }

        public GemRedeemResult(GemRedeemStatus status, WalletBalances wallet = default)
        {
            Status = status;
            Wallet = wallet;
        }

        /// <summary>The client reads these, not the body, to decide whether to confirm the purchase
        /// with the store: only 200 confirms; 202 and 503 keep it pending for a later retry.</summary>
        public int HttpStatus => Status switch
        {
            GemRedeemStatus.Granted or GemRedeemStatus.Replayed => 200,
            GemRedeemStatus.Pending => 202,
            GemRedeemStatus.InvalidRequest => 400,
            GemRedeemStatus.Unauthenticated => 401,
            GemRedeemStatus.Conflict => 409,
            GemRedeemStatus.Rejected => 422,
            GemRedeemStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>POST /shop/gems</c>: a store purchase in, Gems out. The body names the store's own handles
    /// for the purchase (<c>productId</c>, <c>purchaseToken</c>) and nothing else — no amount, no
    /// price, no player — and the store adapter re-reads all three from the store itself.
    ///
    /// <para>Safe to repeat: <see cref="GemGrantService"/> keys the grant on the store's order id, so
    /// a retried or replayed token answers <see cref="GemRedeemStatus.Replayed"/> and writes
    /// nothing.</para>
    /// </summary>
    public static class GemRedeemEndpoint
    {
        /// <summary>Generous bounds on client-authored strings; a Play token is about 150 chars.</summary>
        private const int MaxProductIdLength = 64;
        private const int MaxTokenLength = 2048;

        public static async Task<GemRedeemResult> RedeemAsync(
            string? bearerToken, string? provider, string? productId, string? purchaseToken,
            IIdentityTokenVerifier? verifier, IAccountStore? accounts, GemGrantService? grants,
            IStorePurchaseAcknowledger? acknowledger, OnboardingGrantService? onboarding,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken))
                return new GemRedeemResult(GemRedeemStatus.Unauthenticated);
            if (verifier == null || accounts == null || grants == null)
                return new GemRedeemResult(GemRedeemStatus.Unavailable);
            if (!string.Equals(provider, PlayStoreReceiptValidator.Provider, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(productId) || productId!.Length > MaxProductIdLength ||
                string.IsNullOrWhiteSpace(purchaseToken) || purchaseToken!.Length > MaxTokenLength)
                return new GemRedeemResult(GemRedeemStatus.InvalidRequest);

            Account? account = await ShopEndpoint.ResolveAsync(bearerToken, verifier, accounts, onboarding, ct)
                .ConfigureAwait(false);
            if (account == null)
                return new GemRedeemResult(GemRedeemStatus.Unauthenticated);

            var receipt = new StoreReceipt(provider!, JsonSerializer.Serialize(new { productId, purchaseToken }));
            GemGrantOutcome outcome;
            try
            {
                outcome = await grants.GrantAsync(account, receipt, ct).ConfigureAwait(false);
            }
            catch (StoreUnavailableException ex)
            {
                Console.Error.WriteLine("[shop] gem redeem: store unavailable for player_id={0}: {1}",
                    account.PlayerId, ex.Message);
                return new GemRedeemResult(GemRedeemStatus.Unavailable);
            }
            catch (InvalidOperationException)
            {
                // The order id is already bound to a different grant — someone else's purchase.
                return new GemRedeemResult(GemRedeemStatus.Conflict);
            }

            switch (outcome.Status)
            {
                case GemGrantStatus.Granted:
                case GemGrantStatus.Replayed:
                    // After the ledger write, never before: see IStorePurchaseAcknowledger. A failure
                    // here costs nothing — the grant stands and the client's consume acknowledges too.
                    if (acknowledger != null)
                    {
                        try { await acknowledger.AcknowledgeAsync(receipt, ct).ConfigureAwait(false); }
                        catch (StoreUnavailableException ex)
                        {
                            Console.Error.WriteLine("[shop] gem redeem: acknowledge failed for player_id={0}: {1}",
                                account.PlayerId, ex.Message);
                        }
                    }
                    return new GemRedeemResult(outcome.Status == GemGrantStatus.Granted
                        ? GemRedeemStatus.Granted : GemRedeemStatus.Replayed, outcome.Wallet);
                case GemGrantStatus.Pending:
                    return new GemRedeemResult(GemRedeemStatus.Pending, outcome.Wallet);
                default:
                    Console.Error.WriteLine("[shop] gem redeem rejected for player_id={0}: {1}",
                        account.PlayerId, outcome.RejectionReason);
                    return new GemRedeemResult(GemRedeemStatus.Rejected, outcome.Wallet);
            }
        }
    }
}
