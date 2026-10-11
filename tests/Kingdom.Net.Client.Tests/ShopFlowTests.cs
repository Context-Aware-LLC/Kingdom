using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Shop;
using NUnit.Framework;

using Kingdom.Core.Tests.Support;

namespace Kingdom.Net.Client.Tests
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
                new List<GemBundleOffer>(), vaultCount: 3);
            var authoritative = new PackPurchaseResult(after, "pack.emberfall", 1);
            var gateway = new RecordingShopGateway(before, authoritative);
            var flow = new ShopFlow(gateway);

            await flow.RefreshAsync();
            PackPurchaseResult result = await flow.PurchaseAsync("pack.emberfall", ShopCurrency.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(gateway.LastRequest.PackId, Is.EqualTo("pack.emberfall"));
                Assert.That(gateway.LastRequest.Currency, Is.EqualTo(ShopCurrency.Gold));
                Assert.That(System.Guid.TryParse(gateway.LastRequest.RequestId, out _), Is.True);
                Assert.That(result, Is.SameAs(authoritative));
                Assert.That(flow.Snapshot, Is.SameAs(after));
                Assert.That(result.UnopenedCount, Is.EqualTo(1));
                Assert.That(result.Shop.VaultCount, Is.EqualTo(3));
                Assert.That(result.Shop.Wallet.Shards, Is.EqualTo(85));
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

            public Task<PackPurchaseResult> PurchaseAsync(
                PackPurchaseRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(_purchase);
            }
        }
    }
}
