using System.Threading.Tasks;
using Kingdom.Net.Client.Shop;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Shop
{
    /// <summary>The outcome decides whether the client consumes the purchase with Play. Consuming one
    /// the server did not grant loses the player's money; so only a 200 may read as Granted.</summary>
    [TestFixture]
    public sealed class HttpGemRedeemTests
    {
        private const string Wallet = "{\"status\":\"granted\",\"wallet\":{\"gold\":5,\"gems\":1200,\"shards\":0}}";

        private static HttpShopGateway Gateway(FakeAuthHttp http)
            => new HttpShopGateway("https://api.example", http, () => Task.FromResult("bearer"));

        [TestCase(200, GemRedeemOutcome.Granted)]
        [TestCase(202, GemRedeemOutcome.Pending)]
        [TestCase(422, GemRedeemOutcome.Rejected)]
        [TestCase(409, GemRedeemOutcome.Rejected)]
        [TestCase(400, GemRedeemOutcome.Retry)]
        [TestCase(401, GemRedeemOutcome.Retry)]
        [TestCase(500, GemRedeemOutcome.Retry)]
        [TestCase(503, GemRedeemOutcome.Retry)]
        public async Task Status_Maps_To_What_The_Client_Does_Next(int status, GemRedeemOutcome expected)
        {
            var http = new FakeAuthHttp().Reply(status, Wallet);

            GemRedeemResult result = await Gateway(http).RedeemGemsAsync("gems.tier.1200", "tok-1");

            Assert.That(result.Outcome, Is.EqualTo(expected));
        }

        [Test]
        public async Task A_Lost_Response_Is_Retry_Never_Granted()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure();

            GemRedeemResult result = await Gateway(http).RedeemGemsAsync("gems.tier.1200", "tok-1");

            Assert.That(result.Outcome, Is.EqualTo(GemRedeemOutcome.Retry));
        }

        [Test]
        public async Task The_Request_Names_Only_The_Store_Handles_And_The_Wallet_Comes_Back()
        {
            var http = new FakeAuthHttp().Reply(200, Wallet);

            GemRedeemResult result = await Gateway(http).RedeemGemsAsync("gems.tier.1200", "tok-1");

            Assert.That(http.Requests[0].Url, Is.EqualTo("https://api.example/shop/gems"));
            Assert.That(http.Requests[0].Body,
                Is.EqualTo("{\"provider\":\"play\",\"productId\":\"gems.tier.1200\",\"purchaseToken\":\"tok-1\"}"));
            Assert.That(result.Wallet!.Value.Gems, Is.EqualTo(1200));
        }
    }
}
