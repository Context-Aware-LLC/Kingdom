using System;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.Shop;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Shop
{
    [TestFixture]
    public sealed class HttpShopGatewayTests
    {
        private const string Snapshot =
            "{\"status\":\"ready\",\"vaultCount\":2,\"wallet\":{\"gold\":500,\"gems\":30,\"shards\":5}," +
            "\"packs\":[{\"packId\":\"pack.pendingham\",\"displayName\":\"Kingdom Pack\"," +
            "\"goldPrice\":200,\"gemPrice\":200}]}";

        private static HttpShopGateway Gateway(FakeAuthHttp http)
            => new HttpShopGateway("https://emberfall.fly.dev", http, () => Task.FromResult("jwt"));

        [Test]
        public async Task Catalog_and_wallet_are_read_from_authenticated_server_reply()
        {
            var http = new FakeAuthHttp().Reply(200, Snapshot);
            ShopSnapshot shop = await Gateway(http).GetShopAsync();

            Assert.That(shop.Wallet.Gold, Is.EqualTo(500));
            Assert.That(shop.VaultCount, Is.EqualTo(2));
            Assert.That(shop.Packs.Single().GoldPrice, Is.EqualTo(200));
            Assert.That(http.Requests.Single().Url, Is.EqualTo("https://emberfall.fly.dev/shop"));
            Assert.That(http.Requests.Single().Headers.Any(h =>
                h.Key == "Authorization" && h.Value == "Bearer jwt"), Is.True);
        }

        [Test]
        public void Catalog_without_an_authoritative_vault_count_is_rejected()
        {
            var http = new FakeAuthHttp().Reply(200, Snapshot.Replace("\"vaultCount\":2,", ""));

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await Gateway(http).GetShopAsync());
        }

        [Test]
        public async Task Purchase_returns_unopened_pack_and_never_calls_open()
        {
            var http = new FakeAuthHttp().Reply(200,
                Snapshot.Replace("\"ready\"", "\"purchased\"").Replace("\"vaultCount\":2", "\"vaultCount\":3").Replace(
                    "\"packs\":", "\"packId\":\"pack.pendingham\",\"unopenedCount\":1,\"packs\":"));
            var request = new PackPurchaseRequest("pack.pendingham", ShopCurrency.Gold,
                "bd6ad741-7d8b-4e51-a581-154425424370");

            PackPurchaseResult result = await Gateway(http).PurchaseAsync(request);

            Assert.That(result.UnopenedCount, Is.EqualTo(1));
            Assert.That(result.Shop.VaultCount, Is.EqualTo(3));
            Assert.That(http.Requests, Has.Count.EqualTo(1));
            Assert.That(http.Requests[0].Url, Is.EqualTo("https://emberfall.fly.dev/shop/purchase"));
            Assert.That(http.Requests[0].Body, Is.EqualTo(
                "{\"packId\":\"pack.pendingham\",\"currency\":\"gold\"," +
                "\"requestId\":\"bd6ad741-7d8b-4e51-a581-154425424370\",\"quantity\":1}"));
        }

        [Test]
        public async Task A_multi_pack_purchase_sends_its_quantity()
        {
            var http = new FakeAuthHttp().Reply(200, Snapshot.Replace("\"ready\"", "\"purchased\"").Replace(
                "\"packs\":", "\"packId\":\"pack.pendingham\",\"unopenedCount\":3,\"packs\":"));
            var request = new PackPurchaseRequest("pack.pendingham", ShopCurrency.Gems,
                "bd6ad741-7d8b-4e51-a581-154425424370", 3);

            await Gateway(http).PurchaseAsync(request);

            Assert.That(http.Requests[0].Body, Does.EndWith("\"quantity\":3}"));
        }

        [Test]
        public async Task A_lost_response_retries_the_same_intent_not_a_second_purchase()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure().Reply(200,
                Snapshot.Replace("\"ready\"", "\"replayed\"").Replace(
                    "\"packs\":", "\"packId\":\"pack.pendingham\",\"unopenedCount\":1,\"packs\":"));
            var request = new PackPurchaseRequest("pack.pendingham", ShopCurrency.Gems,
                "bd6ad741-7d8b-4e51-a581-154425424370");

            await Gateway(http).PurchaseAsync(request);

            Assert.That(http.Requests, Has.Count.EqualTo(2));
            Assert.That(http.Requests[1].Body, Is.EqualTo(http.Requests[0].Body));
        }

        [Test]
        public void Two_lost_responses_report_an_uncertain_purchase()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure().ReplyTransportFailure();
            var request = new PackPurchaseRequest("pack.pendingham", ShopCurrency.Gold,
                "bd6ad741-7d8b-4e51-a581-154425424370");

            ShopPurchaseException? error = Assert.ThrowsAsync<ShopPurchaseException>(async () =>
                await Gateway(http).PurchaseAsync(request));

            Assert.That(error!.OutcomeUnknown, Is.True);
        }

        [Test]
        public void Insufficient_funds_is_a_definite_decline_not_an_uncertain_charge()
        {
            var http = new FakeAuthHttp().Reply(409, "{\"status\":\"insufficientfunds\"}");
            var request = new PackPurchaseRequest("pack.pendingham", ShopCurrency.Gold,
                "bd6ad741-7d8b-4e51-a581-154425424370");

            ShopPurchaseException? error = Assert.ThrowsAsync<ShopPurchaseException>(async () =>
                await Gateway(http).PurchaseAsync(request));

            Assert.That(error!.OutcomeUnknown, Is.False);
        }
    }
}
