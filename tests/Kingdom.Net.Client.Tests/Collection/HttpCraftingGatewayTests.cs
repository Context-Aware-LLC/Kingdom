using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;
using Kingdom.Net.Client.Collection;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Collection
{
    [TestFixture]
    public sealed class HttpCraftingGatewayTests
    {
        [Test]
        public async Task Read_preserves_a_real_zero_balance_and_uncapped_ownership()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"ready\",\"cardId\":1,\"copiesOwned\":47,\"shards\":0}");
            var reply = await new HttpCraftingGateway("https://example.test", http).ReadAsync(1, "token");
            Assert.Multiple(() =>
            {
                Assert.That(reply.HasSnapshot, Is.True);
                Assert.That(reply.Shards, Is.Zero);
                Assert.That(reply.CopiesOwned, Is.EqualTo(47));
                Assert.That(reply.Applied, Is.False);
                Assert.That(http.Requests[0].Verb, Is.EqualTo("GET"));
            });
        }

        [Test]
        public async Task Render_waits_for_the_server_and_sends_no_client_balances_or_transaction_id()
        {
            var http = new FakeAuthHttp();
            var held = http.HoldReply();
            Task<CraftingReply> pending = new HttpCraftingGateway("https://example.test/", http).RenderAsync(1, "token");
            Assert.That(pending.IsCompleted, Is.False);
            held.SetResult(AuthHttpResponse.Completed(200,
                "{\"status\":\"rendered\",\"cardId\":1,\"copiesOwned\":1,\"shards\":0}"));
            var reply = await pending;
            Assert.That(reply.Applied, Is.True);
            Assert.That(http.Requests[0].Url, Is.EqualTo("https://example.test/crafting/1/render"));
            Assert.That(http.Requests[0].Body, Is.EqualTo("{}"));
        }

        [TestCase("{\"status\":\"rendered\",\"cardId\":2,\"copiesOwned\":1,\"shards\":0}")]
        [TestCase("{\"status\":\"rendered\",\"cardId\":1,\"copiesOwned\":-1,\"shards\":0}")]
        [TestCase("{\"status\":\"rendered\",\"cardId\":1,\"copiesOwned\":1}")]
        [TestCase("{\"status\":\"reduced\",\"cardId\":1,\"copiesOwned\":1,\"shards\":0}")]
        public async Task Malformed_or_mismatched_receipts_never_claim_success(string body)
        {
            var http = new FakeAuthHttp().Reply(200, body);
            var reply = await new HttpCraftingGateway("https://example.test", http).RenderAsync(1, "token");
            Assert.That(reply.Status, Is.EqualTo("malformed"));
            Assert.That(reply.Applied, Is.False);
            Assert.That(reply.HasSnapshot, Is.False);
        }

        [Test]
        public async Task Lost_response_is_not_retried()
        {
            var http = new FakeAuthHttp().Reply(500, "");
            var reply = await new HttpCraftingGateway("https://example.test", http).ReduceAsync(1, "token");
            Assert.That(reply.Status, Is.EqualTo("offline"));
            Assert.That(http.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Refusal_reports_the_authoritative_balance_without_claiming_a_mutation()
        {
            var http = new FakeAuthHttp().Reply(409, "{\"status\":\"insufficientshards\",\"cardId\":1,\"copiesOwned\":0,\"shards\":10}");
            var reply = await new HttpCraftingGateway("https://example.test", http).RenderAsync(1, "token");
            Assert.That(reply.HasSnapshot, Is.True);
            Assert.That(reply.Applied, Is.False);
            Assert.That(reply.Shards, Is.EqualTo(10));
        }
    }
}
