using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// Projects the append-only entitlement ledger into the <see cref="IOwnedCardCounts"/> the deck
    /// rules take (spec: <c>docs/dev-docs/specs/card-ownership.md</c>, slice 2). This is the
    /// "ledger-backed ownership source" <see cref="AccountOwnedCounts"/> was written waiting for.
    ///
    /// <para><b>A snapshot, not a live view, and that is deliberate.</b>
    /// <see cref="IOwnedCardCounts.CopiesOwned"/> is synchronous while the ledger is not, so this
    /// reads every balance once and hands back a plain <see cref="OwnedCardCounts"/>. The consequence
    /// is the valuable part: every rule evaluated against one snapshot sees one consistent
    /// collection. A validator that re-read the ledger per card could accept a deck that no single
    /// moment in time would have — each card owned, but never all of them at once, if a craft or a
    /// trade landed mid-check.</para>
    ///
    /// <para><b>Only <c>card:</c> rows are a collection.</b> The ledger also carries currency,
    /// unopened pack entitlements, pack seeds and completion markers under the same player id; a
    /// projection that swept those in would report gold as a card id.</para>
    /// </summary>
    public static class LedgerOwnedCounts
    {
        /// <summary>
        /// This player's collection as of now. Never null and never throws on a row it cannot read —
        /// see <see cref="LoadAsync"/>'s skip rules.
        /// </summary>
        public static async Task<OwnedCardCounts> LoadAsync(IEntitlementLedger ledger, string playerId,
                                                            CancellationToken ct = default)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("A player id is required.", nameof(playerId));

            IReadOnlyDictionary<string, long> balances =
                await ledger.BalancesAsync(playerId, ct).ConfigureAwait(false);

            var owned = new OwnedCardCounts();
            if (balances == null) return owned;

            foreach (KeyValuePair<string, long> row in balances)
            {
                if (row.Key == null) continue;
                if (!row.Key.StartsWith(BoosterPackGrantService.CardItemPrefix, StringComparison.Ordinal))
                    continue;

                // Skipped, never thrown on — the same posture BoosterPackGrantService.ProjectOwnership
                // takes. A row this cannot parse is one written under a future or foreign item shape,
                // and losing a player's whole collection over a single unrecognised row would be far
                // the worse failure.
                if (!int.TryParse(row.Key.Substring(BoosterPackGrantService.CardItemPrefix.Length),
                                  out int cardId))
                    continue;

                // A net-negative balance is a LEDGER bug — more spent than granted — and it must not
                // travel into the deck rules, where a negative copy count would make DeckValidator's
                // CopyLimit arithmetic nonsense. Zero is the honest projection of "owns none".
                if (row.Value <= 0) continue;

                owned.Set(cardId, row.Value > int.MaxValue ? int.MaxValue : (int)row.Value);
            }

            return owned;
        }
    }
}
