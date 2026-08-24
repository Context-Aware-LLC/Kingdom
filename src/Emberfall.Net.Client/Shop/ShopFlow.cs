using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Client.Shop
{
    public enum ShopCurrency
    {
        Gold,
        Gems,
    }

    /// <summary>Server-authored reason a revealed slot was upgraded above its roll.</summary>
    public enum PackUpgradeReason
    {
        None,
        RareFloor,
        EpicPity,
        LegendaryPity,
    }

    /// <summary>
    /// One slot from the authoritative open receipt. This is deliberately a data-only client DTO:
    /// it mirrors the server fields without importing the deterministic pack-opening implementation.
    ///
    /// <para>Every slot is a card. The <c>ConvertedToShards</c>/<c>Shards</c> pair was removed with the
    /// no-auto-sharding ruling (Sam, 2026-08-12) rather than left permanently false — a receipt field
    /// that cannot happen is a receipt that lies about what the shop can do to you.</para>
    /// </summary>
    public readonly struct PackCardAward
    {
        public int CardId { get; }
        public Rarity Rarity { get; }
        public PackUpgradeReason Upgrade { get; }

        public PackCardAward(int cardId, Rarity rarity, PackUpgradeReason upgrade)
        {
            if (cardId <= 0)
                throw new ArgumentOutOfRangeException(nameof(cardId), cardId, "Card id is required.");

            CardId = cardId;
            Rarity = rarity;
            Upgrade = upgrade;
        }
    }

    public readonly struct WalletBalances
    {
        public long Gold { get; }

        /// <summary>
        /// May be negative. A store refund claws back gems the player has already spent, so the
        /// balance carries the debt until their next purchase nets it off (EF-080b). Rejecting a
        /// negative here would make an authoritative wallet snapshot unrepresentable on the client.
        /// </summary>
        public long Gems { get; }

        /// <summary>
        /// The authoritative shard balance projected by the account server. Shards cannot carry
        /// debt: crafting is balance-gated, unlike the store-refund path that can make Gems negative.
        /// </summary>
        public long Shards { get; }

        public WalletBalances(long gold, long gems, long shards)
        {
            if (gold < 0) throw new ArgumentOutOfRangeException(nameof(gold));
            if (shards < 0) throw new ArgumentOutOfRangeException(nameof(shards));
            Gold = gold;
            Gems = gems;
            Shards = shards;
        }
    }

    public sealed class PackOffer
    {
        public string PackId { get; }
        public string DisplayName { get; }
        public int GoldPrice { get; }
        public int GemPrice { get; }

        public PackOffer(string packId, string displayName, int goldPrice, int gemPrice)
        {
            if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("Pack id is required.", nameof(packId));
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", nameof(displayName));
            if (goldPrice < 0) throw new ArgumentOutOfRangeException(nameof(goldPrice));
            if (gemPrice < 0) throw new ArgumentOutOfRangeException(nameof(gemPrice));
            PackId = packId;
            DisplayName = displayName;
            GoldPrice = goldPrice;
            GemPrice = gemPrice;
        }
    }

    public sealed class GemBundleOffer
    {
        public string ProductKey { get; }
        public int Gems { get; }
        public int PriceCents { get; }

        public GemBundleOffer(string productKey, int gems, int priceCents)
        {
            if (string.IsNullOrWhiteSpace(productKey)) throw new ArgumentException("Product key is required.", nameof(productKey));
            if (gems <= 0) throw new ArgumentOutOfRangeException(nameof(gems));
            if (priceCents <= 0) throw new ArgumentOutOfRangeException(nameof(priceCents));
            ProductKey = productKey;
            Gems = gems;
            PriceCents = priceCents;
        }
    }

    public sealed class ShopSnapshot
    {
        public WalletBalances Wallet { get; }
        public IReadOnlyList<PackOffer> Packs { get; }
        public IReadOnlyList<GemBundleOffer> GemBundles { get; }

        public ShopSnapshot(WalletBalances wallet, IReadOnlyList<PackOffer>? packs,
                            IReadOnlyList<GemBundleOffer>? gemBundles)
        {
            Wallet = wallet;
            Packs = packs ?? Array.Empty<PackOffer>();
            GemBundles = gemBundles ?? Array.Empty<GemBundleOffer>();
        }
    }

    public readonly struct PackPurchaseRequest
    {
        public string PackId { get; }
        public ShopCurrency Currency { get; }

        public PackPurchaseRequest(string packId, ShopCurrency currency)
        {
            if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("Pack id is required.", nameof(packId));
            PackId = packId;
            Currency = currency;
        }
    }

    /// <summary>
    /// The server-authored reveal payload. The client renders these awards in order and never rolls,
    /// substitutes, or predicts a slot locally.
    /// </summary>
    public sealed class PackOpenReceipt
    {
        public string PackId { get; }
        public string PackInstanceId { get; }
        public IReadOnlyList<PackCardAward> Awards { get; }

        public PackOpenReceipt(string packId, string packInstanceId, IReadOnlyList<PackCardAward>? awards)
        {
            if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("Pack id is required.", nameof(packId));
            if (string.IsNullOrWhiteSpace(packInstanceId))
                throw new ArgumentException("Pack instance id is required.", nameof(packInstanceId));
            PackId = packId;
            PackInstanceId = packInstanceId;
            Awards = awards ?? Array.Empty<PackCardAward>();
        }
    }

    public sealed class PackPurchaseResult
    {
        public ShopSnapshot Shop { get; }
        public PackOpenReceipt OpenedPack { get; }

        public PackPurchaseResult(ShopSnapshot shop, PackOpenReceipt openedPack)
        {
            Shop = shop ?? throw new ArgumentNullException(nameof(shop));
            OpenedPack = openedPack ?? throw new ArgumentNullException(nameof(openedPack));
        }
    }

    /// <summary>
    /// Founder-locked display catalog for the client shell. The authoritative gateway remains free
    /// to return updated offers; these values keep the disconnected UI useful without inventing
    /// wallet balances or pack contents.
    ///
    /// <para><b>An offer carries no art.</b> It used to name its packaging sprites in a
    /// <c>PackagingArtKeys</c> list, which meant a new expansion's art needed an edit here — in a
    /// transport project, to add a picture. Art is discovered from disk instead
    /// (<c>Assets/Art/Shop/Packs/&lt;setId&gt;/</c>, presentation-side), joined to an offer by the
    /// last segment of its <see cref="PackOffer.PackId"/>. Nothing about a purchasable belongs in
    /// an art folder and nothing about art belongs here.</para>
    /// </summary>
    public static class ShopCatalog
    {
        public static IReadOnlyList<PackOffer> Packs { get; } = Array.AsReadOnly(new[]
        {
            new PackOffer("pack.emberfall", "Emberfall Pack", 100, 100),
            new PackOffer("pack.rekindling", "Rekindling Pack", 100, 100),
        });

        public static IReadOnlyList<GemBundleOffer> GemBundles { get; } = Array.AsReadOnly(new[]
        {
            new GemBundleOffer("gems.tier.200", 200, 199),
            new GemBundleOffer("gems.tier.550", 550, 499),
            new GemBundleOffer("gems.tier.1200", 1200, 999),
            new GemBundleOffer("gems.tier.2600", 2600, 1999),
            new GemBundleOffer("gems.tier.7000", 7000, 4999),
            new GemBundleOffer("gems.tier.15000", 15000, 9999),
        });
    }

    /// <summary>
    /// Seam for the authoritative wallet, purchase, and open path supplied by the account server.
    /// </summary>
    public interface IShopGateway
    {
        Task<ShopSnapshot> GetShopAsync(CancellationToken cancellationToken = default);
        Task<PackPurchaseResult> PurchaseAndOpenAsync(
            PackPurchaseRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Owns the client flow around the shop seam. It forwards player intent and adopts the returned
    /// snapshot and open receipt by reference so no client-side price, wallet, or card calculation
    /// can drift from authority.
    /// </summary>
    public sealed class ShopFlow
    {
        private readonly IShopGateway _gateway;

        public ShopSnapshot? Snapshot { get; private set; }
        public PackOpenReceipt? LatestOpen { get; private set; }

        public ShopFlow(IShopGateway gateway)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        }

        public async Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = await _gateway.GetShopAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The shop gateway returned no snapshot.");
        }

        public async Task<PackPurchaseResult> PurchaseAsync(
            string packId,
            ShopCurrency currency,
            CancellationToken cancellationToken = default)
        {
            var request = new PackPurchaseRequest(packId, currency);
            PackPurchaseResult result = await _gateway.PurchaseAndOpenAsync(request, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The shop gateway returned no purchase result.");

            Snapshot = result.Shop;
            LatestOpen = result.OpenedPack;
            return result;
        }
    }
}
