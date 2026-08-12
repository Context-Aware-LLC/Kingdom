using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Shop;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests
{
    [TestFixture]
    [Category("Unit")]
    public sealed class ShopFlowTests
    {
        [Test]
        public async Task Purchase_Forwards_The_Choice_And_Adopts_The_Server_Result_Wholesale()
        {
            var before = new ShopSnapshot(
                new WalletBalances(300, 200, 75),
                new List<PackOffer>(),
                new List<GemBundleOffer>());
            var after = new ShopSnapshot(
                new WalletBalances(200, 200, 85),
                new List<PackOffer>(),
                new List<GemBundleOffer>());
            var open = new PackOpenReceipt(
                "pack.emberfall",
                "pack-instance-7",
                new[]
                {
                    new PackCardAward(
                        "emberward_ember_acolyte",
                        Rarity.RARE,
                        false,
                        0,
                        PackUpgradeReason.RareFloor),
                });
            var authoritative = new PackPurchaseResult(after, open);
            var gateway = new RecordingShopGateway(before, authoritative);
            var flow = new ShopFlow(gateway);

            await flow.RefreshAsync();
            PackPurchaseResult result = await flow.PurchaseAsync("pack.emberfall", ShopCurrency.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(gateway.LastRequest.PackId, Is.EqualTo("pack.emberfall"));
                Assert.That(gateway.LastRequest.Currency, Is.EqualTo(ShopCurrency.Gold));
                Assert.That(result, Is.SameAs(authoritative));
                Assert.That(flow.Snapshot, Is.SameAs(after));
                Assert.That(flow.LatestOpen, Is.SameAs(open));
                Assert.That(result.Shop.Wallet.Shards, Is.EqualTo(85));
            });
        }

        [Test]
        public void Locked_Catalog_Offers_Both_100_Currency_Prices_And_The_Six_Approved_Gem_Bundles()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ShopCatalog.Packs, Has.Count.EqualTo(2));
                Assert.That(ShopCatalog.Packs, Has.All.Property(nameof(PackOffer.GoldPrice)).EqualTo(100));
                Assert.That(ShopCatalog.Packs, Has.All.Property(nameof(PackOffer.GemPrice)).EqualTo(100));
                Assert.That(
                    ShopCatalog.Packs,
                    Is.All.Matches<PackOffer>(offer => offer.PackagingArtKeys.Count == 4));
                Assert.That(ShopCatalog.GemBundles, Has.Count.EqualTo(6));
                Assert.That(
                    ShopCatalog.GemBundles,
                    Has.Exactly(1).Matches<GemBundleOffer>(offer =>
                        offer.Gems == 200 && offer.PriceCents == 199));
                Assert.That(
                    ShopCatalog.GemBundles,
                    Has.Exactly(1).Matches<GemBundleOffer>(offer =>
                        offer.Gems == 15000 && offer.PriceCents == 9999));
            });
        }

        private sealed class RecordingShopGateway : IShopGateway
        {
            private readonly ShopSnapshot _snapshot;
            private readonly PackPurchaseResult _purchase;

            public RecordingShopGateway(ShopSnapshot snapshot, PackPurchaseResult purchase)
            {
                _snapshot = snapshot;
                _purchase = purchase;
            }

            public PackPurchaseRequest LastRequest { get; private set; }

            public Task<ShopSnapshot> GetShopAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(_snapshot);

            public Task<PackPurchaseResult> PurchaseAndOpenAsync(
                PackPurchaseRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(_purchase);
            }
        }
    }
}
