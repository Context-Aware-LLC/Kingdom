using System;
using System.Collections.Generic;
using Emberfall.Core.Collection;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The one place a ledger pack item becomes the <see cref="BoosterPack"/> it opens as — card
    /// count, odds and pool. Spec: <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>, slice 0.
    ///
    /// <para><b>This exists because the shape of a pack is the CALLER's argument, not the
    /// entitlement's.</b> <see cref="BoosterPackGrantService.OpenAsync"/> reserves and debits by
    /// <see cref="BoosterPack.Id"/> while taking the card count and the odds from whatever
    /// <see cref="BoosterPack"/> it is handed. So the mapping from an owned item to a pack shape is a
    /// server decision with real value attached to it, and if the route ever let the client name the
    /// shape, a player would spend a retail entitlement at the starter's boosted odds for a bigger,
    /// richer pack. The client names a pack LINE; this table decides what that line opens as.</para>
    ///
    /// <para><b>Also the validation set for <c>packId</c>.</b> A request naming an item that is not a
    /// pack — <c>card:1042</c>, <c>gold</c>, anything a player has a balance of — must be refused
    /// rather than reserved against, and <see cref="TryResolve"/> is what refuses it.</para>
    ///
    /// <para>RETIREMENT TRIGGER: this table names every pack line by hand, so <b>the commit that adds
    /// a purchasable pack must add it here</b> or the line is unopenable — a balance the player can
    /// see and cannot spend, which is exactly the defect (#346) that this slice exists to close.
    /// <see cref="BoosterPack.All"/> is enumerated rather than listed for that reason; only the
    /// starter, which <see cref="BoosterPack.All"/> deliberately excludes, is named.</para>
    /// </summary>
    public static class PackCatalog
    {
        /// <summary>
        /// The pack this item opens as, or false when the item is not a pack line at all.
        ///
        /// <para>Ordinal comparison, like every other ledger item key: a pack id is a wire token, not
        /// display text, and a culture-sensitive match here would resolve differently on a Turkish
        /// server.</para>
        /// </summary>
        public static bool TryResolve(string? packId, out BoosterPack pack)
        {
            pack = null!;
            if (string.IsNullOrWhiteSpace(packId)) return false;

            // The granted starter. Not in All() — that is what keeps a granted pack and a bought one
            // from being one fungible balance, per BoosterPack.StarterSku.
            BoosterPack starter = OnboardingGrant.StarterPack();
            if (string.Equals(packId, starter.Id, StringComparison.Ordinal))
            {
                pack = starter;
                return true;
            }

            IReadOnlyList<BoosterPack> retail = BoosterPack.All();
            for (int i = 0; i < retail.Count; i++)
            {
                if (!string.Equals(packId, retail[i].Id, StringComparison.Ordinal)) continue;
                pack = retail[i];
                return true;
            }

            return false;
        }

        /// <summary>True when this ledger item is a pack line. The filter <c>GET /collection</c> runs
        /// its balances through, so a player's <c>card:</c> rows and currencies never reach the pack
        /// shelf.</summary>
        public static bool IsPackItem(string? item) => TryResolve(item, out _);
    }
}
