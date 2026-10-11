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
            var reply = await new HttpCraftingGateway("https://example.test", http).ReduceAsync(1, 1, "token");
            Assert.That(reply.Status, Is.EqualTo("offline"));
            Assert.That(http.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task Reduce_sends_the_count_and_nothing_else()
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"reduced\",\"cardId\":1,\"copiesOwned\":2,\"shards\":30}");
            var reply = await new HttpCraftingGateway("https://example.test", http).ReduceAsync(1, 3, "token");
            Assert.Multiple(() =>
            {
                Assert.That(reply.Applied, Is.True);
                Assert.That(http.Requests[0].Url, Is.EqualTo("https://example.test/crafting/1/reduce"));
                Assert.That(http.Requests[0].Body, Is.EqualTo("{\"count\":3}"));
            });
        }

        [Test]
        public async Task Reduce_of_no_copies_never_reaches_the_server()
        {
            var http = new FakeAuthHttp();
            var reply = await new HttpCraftingGateway("https://example.test", http).ReduceAsync(1, 0, "token");
            Assert.That(reply.Status, Is.EqualTo("invalidcount"));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task Too_few_copies_is_a_refusal_with_the_authoritative_count()
        {
            var http = new FakeAuthHttp().Reply(409, "{\"status\":\"notenoughcopies\",\"cardId\":1,\"copiesOwned\":2,\"shards\":0}");
            var reply = await new HttpCraftingGateway("https://example.test", http).ReduceAsync(1, 3, "token");
            Assert.That(reply.HasSnapshot, Is.True);
            Assert.That(reply.Applied, Is.False);
            Assert.That(reply.CopiesOwned, Is.EqualTo(2));
        }

        [TestCase(false, "{\"includeLegendary\":false}")]
        [TestCase(true, "{\"includeLegendary\":true}")]
        public async Task Reduce_spares_names_no_card_and_reports_what_the_server_paid(bool legendary, string body)
        {
            var http = new FakeAuthHttp().Reply(200, "{\"status\":\"reduced\",\"copiesReduced\":7,\"shardsGained\":145,\"shards\":200}");
            var reply = await new HttpCraftingGateway("https://example.test", http).ReduceSparesAsync(legendary, "token");
            Assert.Multiple(() =>
            {
                Assert.That(reply.Applied, Is.True);
                Assert.That(reply.CopiesReduced, Is.EqualTo(7));
                Assert.That(reply.ShardsGained, Is.EqualTo(145));
                Assert.That(reply.Shards, Is.EqualTo(200));
                Assert.That(http.Requests[0].Url, Is.EqualTo("https://example.test/crafting/spares/reduce"));
                Assert.That(http.Requests[0].Body, Is.EqualTo(body));
            });
        }

        [TestCase(409, "{\"status\":\"nospares\",\"copiesReduced\":0,\"shardsGained\":0,\"shards\":12}", "nospares")]
        [TestCase(200, "{\"status\":\"reduced\",\"copiesReduced\":0,\"shardsGained\":0,\"shards\":12}", "malformed")]
        [TestCase(200, "{\"status\":\"reduced\",\"copiesReduced\":7,\"shards\":12}", "malformed")]
        [TestCase(200, "{\"status\":\"nospares\",\"copiesReduced\":0,\"shardsGained\":0,\"shards\":12}", "malformed")]
        [TestCase(500, "", "offline")]
        public async Task Reduce_spares_never_claims_a_mutation_it_was_not_told_about(int http, string body, string status)
        {
            var fake = new FakeAuthHttp().Reply(http, body);
            var reply = await new HttpCraftingGateway("https://example.test", fake).ReduceSparesAsync(false, "token");
            Assert.That(reply.Status, Is.EqualTo(status));
            Assert.That(reply.Applied, Is.False);
            Assert.That(fake.Requests.Count, Is.EqualTo(1));
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
