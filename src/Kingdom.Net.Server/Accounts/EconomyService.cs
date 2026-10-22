using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>Canonical ledger item ids for the player wallet currencies.</summary>
    public static class EconomyItems
    {
        public const string Gold = "gold";
        public const string Gems = "gems";
        public const string Shards = ShardValues.LedgerItem;
    }

    /// <summary>The server-authored wallet projection returned to callers.</summary>
    public readonly record struct WalletBalances
    {
        public long Gold { get; }
        public long Gems { get; }
        public long Shards { get; }

        public WalletBalances(long gold, long gems, long shards) : this()
        {
            Gold = gold;
            Gems = gems;
            Shards = shards;
        }
    }

    public enum EconomyCurrency
    {
        Gold,
        Gems,
    }

    public enum GoldRewardKind
    {
        DailyQuest,
        FirstWinOfDay,
        Match,
    }

    internal static class EconomyAccountPolicy
    {
        /// <summary>Every inventory write is keyed on a durable player id. Accounts come only from
        /// <see cref="IAccountStore"/> — a signed-in player or a bot — so this guards a malformed row,
        /// not a kind of player: the tooling-only guest seat never resolves an <see cref="Account"/>.</summary>
        public static void RequireDurable(Account account)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (string.IsNullOrWhiteSpace(account.PlayerId))
                throw new ArgumentException("A durable player id is required.", nameof(account));
            if (string.IsNullOrWhiteSpace(account.CredentialKind))
                throw new ArgumentException("A credential kind is required.", nameof(account));
        }
    }

    public enum PackPurchaseStatus
    {
        Purchased,
        Replayed,
        InsufficientFunds,
    }

    public sealed class PackPurchaseOutcome
    {
        public PackPurchaseStatus Status { get; }
        public string PurchaseTransactionId { get; }
        public WalletBalances Wallet { get; }

        public PackPurchaseOutcome(
            PackPurchaseStatus status,
            string purchaseTransactionId,
            WalletBalances wallet)
        {
            Status = status;
            PurchaseTransactionId = purchaseTransactionId;
            Wallet = wallet;
        }
    }

    /// <summary>
    /// The server-authoritative economy module. Its interface accepts an account and returns ledger
    /// projections; callers never supply or calculate wallet balances.
    /// </summary>
    public sealed class EconomyService
    {
        public const long PackPrice = 200;
        /// <summary>The most packs one purchase may take. The client picks the quantity, so the
        /// server owns the bound (docs/dev-docs/quality/security.md, "purchasable inventory").</summary>
        public const int MaxPackQuantity = 10;
        public const string PackPurchaseSource = "pack_purchase";

        private readonly IEntitlementLedger _ledger;
        private readonly ITimeSource _time;

        public EconomyService(IEntitlementLedger ledger, ITimeSource time)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        public async Task<WalletBalances> GetWalletAsync(Account account, CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            IReadOnlyDictionary<string, long> balances =
                await _ledger.BalancesAsync(account.PlayerId, ct).ConfigureAwait(false);
            long gold = balances.TryGetValue(EconomyItems.Gold, out long heldGold) ? heldGold : 0;
            long gems = balances.TryGetValue(EconomyItems.Gems, out long heldGems) ? heldGems : 0;
            // No shard row is the normal pre-first-duplicate state. The ledger projection owns the
            // zero so the client can distinguish it from an unavailable wallet gateway.
            long shards = balances.TryGetValue(EconomyItems.Shards, out long heldShards) ? heldShards : 0;
            return new WalletBalances(gold, gems, shards);
        }

        /// <summary>
        /// Grants an amount selected by the authoritative quest or match subsystem. This module
        /// deliberately defines no reward amounts; it only supplies one replay-safe seam for each
        /// founder-locked Gold faucet.
        /// </summary>
        public async Task<WalletBalances> GrantGoldAsync(
            Account account,
            GoldRewardKind kind,
            long amount,
            string rewardEventId,
            CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (string.IsNullOrWhiteSpace(rewardEventId))
                throw new ArgumentException("A server-authored reward event id is required.", nameof(rewardEventId));
            EconomyAccountPolicy.RequireDurable(account);

            string source = kind switch
            {
                GoldRewardKind.DailyQuest => "daily_quest",
                GoldRewardKind.FirstWinOfDay => "first_win",
                GoldRewardKind.Match => "match_reward",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var batch = new EntitlementBatch(
                account.PlayerId,
                "gold:" + account.PlayerId + ":" + source + ":" + rewardEventId,
                new[] { new EntitlementDelta(EconomyItems.Gold, amount, source) },
                requirement: null,
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));
            EntitlementBatchResult result = await _ledger.ApplyBatchAsync(batch, ct).ConfigureAwait(false);
            if (result.Conflict)
                throw new InvalidOperationException(
                    "The reward event id is already bound to a different Gold grant.");
            return await GetWalletAsync(account, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Atomically debit one founder-locked currency price and grant one unopened pack. The
        /// caller supplies a server-minted transaction id; retries reuse it and therefore cannot
        /// spend twice. Opening is a separate resumable step, so a failed open leaves this durable
        /// pack entitlement available for retry instead of stranding the spend.
        /// </summary>
        public async Task<PackPurchaseOutcome> PurchasePackAsync(
            Account account,
            BoosterPack pack,
            EconomyCurrency currency,
            string serverTransactionId,
            int quantity = 1,
            CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (quantity < 1 || quantity > MaxPackQuantity)
                throw new ArgumentOutOfRangeException(nameof(quantity));
            if (string.IsNullOrWhiteSpace(serverTransactionId))
                throw new ArgumentException("A server-minted purchase transaction id is required.", nameof(serverTransactionId));
            EconomyAccountPolicy.RequireDurable(account);

            string currencyItem = currency switch
            {
                EconomyCurrency.Gold => EconomyItems.Gold,
                EconomyCurrency.Gems => EconomyItems.Gems,
                _ => throw new ArgumentOutOfRangeException(nameof(currency)),
            };
            var batch = new EntitlementBatch(
                account.PlayerId,
                PackPurchaseSource + ":" + serverTransactionId,
                new List<EntitlementDelta>
                {
                    new EntitlementDelta(currencyItem, -PackPrice * quantity, PackPurchaseSource),
                    new EntitlementDelta(pack.Id, quantity, PackPurchaseSource),
                },
                new EntitlementBalanceRequirement(currencyItem, PackPrice * quantity),
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));

            EntitlementBatchResult applied = await _ledger.ApplyBatchAsync(batch, ct).ConfigureAwait(false);
            if (applied.Conflict)
                throw new InvalidOperationException(
                    "The purchase transaction id is already bound to different economy movements.");

            WalletBalances wallet = await GetWalletAsync(account, ct).ConfigureAwait(false);
            PackPurchaseStatus status = applied.Applied
                ? PackPurchaseStatus.Purchased
                : applied.Duplicate
                    ? PackPurchaseStatus.Replayed
                    : PackPurchaseStatus.InsufficientFunds;
            return new PackPurchaseOutcome(status, serverTransactionId, wallet);
        }
    }
}
