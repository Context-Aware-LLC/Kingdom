using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>Card-back grants and ownership in the existing append-only entitlement ledger.
    /// No client grant route exists. Rewards may call GrantAsync with a server-authored event ID.</summary>
    public static class CardBackOwnership
    {
        public static string Item(int id)
        {
            if (!CardBackDefinitions.TryGet(id, out _))
                throw new ArgumentOutOfRangeException(nameof(id), "Unknown card back.");
            return "cardback:" + id.ToString(CultureInfo.InvariantCulture);
        }

        public static Task<GrantResult> GrantAsync(IEntitlementLedger ledger, Account account,
            int id, string source, string eventId, CancellationToken ct = default)
        {
            EconomyAccountPolicy.RequireDurable(account);
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A source is required.", nameof(source));
            if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("An event ID is required.", nameof(eventId));
            string item = Item(id);
            // Scope the idempotency key to its recipient and item, even for a shared reward event.
            return ledger.GrantAsync(new EntitlementGrant(account.PlayerId, item, 1, source,
                item + ":" + account.PlayerId + ":" + eventId, DateTimeOffset.UtcNow), ct);
        }

        public static Task<GrantResult> EnsureDefaultAsync(IEntitlementLedger ledger, Account account,
            CancellationToken ct = default) =>
            GrantAsync(ledger, account, CardBackDefinitions.DefaultId, "default_card_back", "starter-v1", ct);

        public static async Task<IReadOnlyDictionary<int, long>> ReadAsync(
            IEntitlementLedger ledger, Account account, CancellationToken ct = default)
        {
            await EnsureDefaultAsync(ledger, account, ct).ConfigureAwait(false);
            var balances = await ledger.BalancesAsync(account.PlayerId, ct).ConfigureAwait(false);
            var owned = new Dictionary<int, long>();
            foreach (var back in CardBackDefinitions.All)
                if (balances.TryGetValue(Item(back.Id), out long count) && count > 0)
                    owned.Add(back.Id, count);
            return owned;
        }
    }
}
