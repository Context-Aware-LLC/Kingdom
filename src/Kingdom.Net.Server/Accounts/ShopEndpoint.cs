using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Accounts
{
    public enum ShopEndpointStatus
    {
        Ready,
        Purchased,
        Replayed,
        InsufficientFunds,
        InvalidRequest,
        Unauthenticated,
        Unavailable,
        Conflict,
    }

    public sealed class ShopEndpointResult
    {
        public ShopEndpointStatus Status { get; }
        public WalletBalances Wallet { get; }
        public string PackId { get; }
        public long UnopenedCount { get; }
        public long VaultCount { get; }

        public ShopEndpointResult(ShopEndpointStatus status, WalletBalances wallet = default,
                                  string packId = "", long unopenedCount = 0, long vaultCount = 0)
        {
            Status = status;
            Wallet = wallet;
            PackId = packId;
            UnopenedCount = unopenedCount;
            VaultCount = vaultCount;
        }

        public int HttpStatus => Status switch
        {
            ShopEndpointStatus.Ready or ShopEndpointStatus.Purchased or ShopEndpointStatus.Replayed => 200,
            ShopEndpointStatus.InvalidRequest => 400,
            ShopEndpointStatus.Unauthenticated => 401,
            ShopEndpointStatus.InsufficientFunds or ShopEndpointStatus.Conflict => 409,
            ShopEndpointStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>
    /// Authenticated shop read and purchase. Purchase debits currency and grants one unopened retail
    /// pack in the same ledger batch; opening belongs exclusively to the Vault.
    /// </summary>
    public static class ShopEndpoint
    {
        public static async Task<ShopEndpointResult> ReadAsync(
            string? bearerToken, IIdentityTokenVerifier? verifier, IAccountStore? accounts,
            IEntitlementLedger? ledger, EconomyService? economy, BoosterPackGrantService? opener,
            OnboardingGrantService? onboarding,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken))
                return new ShopEndpointResult(ShopEndpointStatus.Unauthenticated);
            if (verifier == null || accounts == null || ledger == null || economy == null || opener == null)
                return new ShopEndpointResult(ShopEndpointStatus.Unavailable);
            Account? account = await ResolveAsync(bearerToken, verifier, accounts, onboarding, ct)
                .ConfigureAwait(false);
            if (account == null)
                return new ShopEndpointResult(ShopEndpointStatus.Unauthenticated);

            WalletBalances wallet = await economy.GetWalletAsync(account, ct).ConfigureAwait(false);
            IReadOnlyDictionary<string, long> balances = await ledger.BalancesAsync(account.PlayerId, ct)
                .ConfigureAwait(false);
            return new ShopEndpointResult(ShopEndpointStatus.Ready, wallet,
                vaultCount: CountVaultPacks(balances));
        }

        public static async Task<ShopEndpointResult> PurchaseAsync(
            string? bearerToken, string? packId, string? currency, string? requestId,
            IIdentityTokenVerifier? verifier, IAccountStore? accounts,
            IEntitlementLedger? ledger, EconomyService? economy,
            BoosterPackGrantService? opener, OnboardingGrantService? onboarding,
            CancellationToken ct = default, long quantity = 1)
        {
            if (string.IsNullOrWhiteSpace(bearerToken))
                return new ShopEndpointResult(ShopEndpointStatus.Unauthenticated);
            if (verifier == null || accounts == null || ledger == null || economy == null || opener == null)
                return new ShopEndpointResult(ShopEndpointStatus.Unavailable);
            // Client-authored, so bounded here before anything is priced: a count outside 1..Max is
            // a malformed request, never a clamp.
            if (quantity < 1 || quantity > EconomyService.MaxPackQuantity)
                return new ShopEndpointResult(ShopEndpointStatus.InvalidRequest);

            // The starter line is grant-only. Resolve only retail packs that this build can open.
            BoosterPack? pack = null;
            foreach (BoosterPack retail in BoosterPack.All())
                if (string.Equals(retail.Id, packId, StringComparison.Ordinal)) { pack = retail; break; }
            if (pack == null || !Guid.TryParse(requestId, out Guid intent) ||
                !Enum.TryParse(currency, true, out EconomyCurrency chosen) ||
                !Enum.IsDefined(typeof(EconomyCurrency), chosen))
                return new ShopEndpointResult(ShopEndpointStatus.InvalidRequest);

            Account? account = await ResolveAsync(bearerToken, verifier, accounts, onboarding, ct)
                .ConfigureAwait(false);
            if (account == null || !EconomyAccountPolicy.IsDurable(account))
                return new ShopEndpointResult(ShopEndpointStatus.Unauthenticated);

            // The client names an intent, never a ledger transaction or pack instance. The server
            // derives an account-scoped transaction id; replays of one intent spend exactly once.
            string transactionId = "shop:" + account.PlayerId + ":" + intent.ToString("N");
            PackPurchaseOutcome purchased;
            try
            {
                purchased = await economy.PurchasePackAsync(account, pack, chosen, transactionId, (int)quantity, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return new ShopEndpointResult(ShopEndpointStatus.Conflict);
            }

            if (purchased.Status == PackPurchaseStatus.InsufficientFunds)
                return new ShopEndpointResult(ShopEndpointStatus.InsufficientFunds, purchased.Wallet);

            IReadOnlyDictionary<string, long> balances = await ledger.BalancesAsync(account.PlayerId, ct)
                .ConfigureAwait(false);
            balances.TryGetValue(pack.Id, out long unopened);
            return new ShopEndpointResult(
                purchased.Status == PackPurchaseStatus.Purchased
                    ? ShopEndpointStatus.Purchased : ShopEndpointStatus.Replayed,
                purchased.Wallet, pack.Id, unopened, CountVaultPacks(balances));
        }

        private static long CountVaultPacks(IReadOnlyDictionary<string, long> balances)
        {
            long count = 0;
            foreach (KeyValuePair<string, long> row in balances)
                if (row.Value > 0 && PackCatalog.IsPackItem(row.Key))
                    count = checked(count + row.Value);
            return count;
        }

        private static async Task<Account?> ResolveAsync(
            string? bearerToken, IIdentityTokenVerifier? verifier, IAccountStore? accounts,
            OnboardingGrantService? onboarding, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier == null || accounts == null)
                return null;
            var verified = await verifier.VerifyAsync(bearerToken, ct).ConfigureAwait(false);
            if (!verified.IsSuccess || verified.Identity == null) return null;

            Account account = await accounts.ResolveOrCreateAsync("supabase",
                CredentialHash.Of(verified.Identity.Subject), verified.Identity.Email, ct)
                .ConfigureAwait(false);
            if (onboarding != null)
            {
                try { await onboarding.EnsureGrantedAsync(account, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine("[shop] onboarding grant failed for player_id={0}: {1}",
                        account.PlayerId, ex.Message);
                }
            }
            return account;
        }
    }
}
