using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Core.Collection;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    [TestFixture, NonParallelizable]
    public sealed class CraftingHttpTests
    {
        private sealed class Clock : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        private sealed class Verifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(rawToken == "valid"
                    ? TokenVerificationResult.Ok(new VerifiedIdentity("craft-player", "craft@example.com", true))
                    : TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));
        }

        [Test, CancelAfter(30_000)]
        public async Task Craft_routes_require_authentication()
        {
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                TokenVerifier = new Verifier(),
                AccountStore = new InMemoryAccountStore(new Clock()),
                EntitlementLedger = new InMemoryEntitlementLedger(),
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                using var read = await http.GetAsync("/crafting/1");
                using var render = await http.PostAsync("/crafting/1/render", null);
                using var reduce = await http.PostAsync("/crafting/1/reduce", null);
                Assert.Multiple(() =>
                {
                    Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(render.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(reduce.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                });
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        [TestCase(false), TestCase(true), CancelAfter(30_000)]
        public async Task Zero_shard_account_can_reduce_and_render_even_with_an_older_signup_grant(bool olderSignup)
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            var owner = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("craft-player"), "craft@example.com");
            var probeCard = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir()).AllCollectible()
                .First(c => c.Rarity == Rarity.COMMON);
            if (olderSignup)
            {
                await ledger.ApplyBatchAsync(new EntitlementBatch(owner.PlayerId,
                    OnboardingGrantService.TxnPrefix + owner.PlayerId,
                    new[] { new EntitlementDelta(BoosterPackGrantService.CardItemPrefix + probeCard.Id, 1, "signup") },
                    null, DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));
            }
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                TokenVerifier = new Verifier(),
                AccountStore = accounts,
                EntitlementLedger = ledger,
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
                int originalRows = (await ledger.HistoryAsync(owner.PlayerId)).Count;
                using JsonDocument craftFirst = JsonDocument.Parse(await http.GetStringAsync("/crafting/" + probeCard.Id));
                Assert.That(craftFirst.RootElement.GetProperty("copiesOwned").GetInt32(), Is.EqualTo(olderSignup ? 1 : 0));
                Assert.That(craftFirst.RootElement.GetProperty("shards").GetInt64(), Is.Zero);
                Assert.That((await ledger.HistoryAsync(owner.PlayerId)).Count, Is.EqualTo(originalRows),
                    "Opening crafting must not issue or recheck a signup grant.");
                using JsonDocument collection = JsonDocument.Parse(await http.GetStringAsync("/collection"));
                Assert.That(collection.RootElement.GetProperty("wallet").GetProperty("shards").GetInt64(), Is.Zero);
                int cardId = int.Parse(collection.RootElement.GetProperty("cards").EnumerateObject().First().Name);
                string route = "/crafting/" + cardId;
                using JsonDocument initial = JsonDocument.Parse(await http.GetStringAsync(route));
                Assert.That(initial.RootElement.GetProperty("shards").GetInt64(), Is.Zero);
                Assert.That(initial.RootElement.GetProperty("copiesOwned").GetInt32(), Is.EqualTo(1));

                using var unaffordable = await http.PostAsync(route + "/render", null);
                Assert.That(unaffordable.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Account account = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("craft-player"), "craft@example.com");
                await ledger.GrantAsync(new EntitlementGrant(account.PlayerId,
                    BoosterPackGrantService.CardItemPrefix + cardId, 4, "test", "craft-test-cards",
                    DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));
                for (int i = 0; i < 5; i++)
                {
                    using var reduced = await http.PostAsync(route + "/reduce", null);
                    Assert.That(reduced.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                }
                using JsonDocument afterReduce = JsonDocument.Parse(await http.GetStringAsync(route));
                Assert.That(afterReduce.RootElement.GetProperty("copiesOwned").GetInt32(), Is.Zero);
                Assert.That(afterReduce.RootElement.GetProperty("shards").GetInt64(), Is.EqualTo(50));

                using var nothingOwned = await http.PostAsync(route + "/reduce", null);
                Assert.That(nothingOwned.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                HttpResponseMessage[] concurrent = await Task.WhenAll(
                    http.PostAsync(route + "/render", null), http.PostAsync(route + "/render", null));
                try
                {
                    Assert.That(concurrent.Count(r => r.StatusCode == HttpStatusCode.OK), Is.EqualTo(1));
                    Assert.That(concurrent.Count(r => r.StatusCode == HttpStatusCode.Conflict), Is.EqualTo(1));
                }
                finally { foreach (var response in concurrent) response.Dispose(); }

                using JsonDocument afterRender = JsonDocument.Parse(await http.GetStringAsync(route));
                Assert.That(afterRender.RootElement.GetProperty("shards").GetInt64(), Is.Zero);
                Assert.That(afterRender.RootElement.GetProperty("copiesOwned").GetInt32(), Is.EqualTo(1));
                using JsonDocument refreshed = JsonDocument.Parse(await http.GetStringAsync("/collection"));
                Assert.That(refreshed.RootElement.GetProperty("cards").GetProperty(cardId.ToString()).GetInt32(), Is.EqualTo(1));
                Assert.That(await ledger.BalanceAsync(account.PlayerId, ShardValues.LedgerItem), Is.Zero);

                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "rejected");
                using var rejected = await http.PostAsync(route + "/reduce", null);
                Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(await ledger.BalanceAsync(account.PlayerId, BoosterPackGrantService.CardItemPrefix + cardId), Is.EqualTo(1));
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }
}
