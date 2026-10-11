using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Core.Collection;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
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
                using var spares = await http.PostAsync("/crafting/spares/reduce", null);
                Assert.Multiple(() =>
                {
                    Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(render.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(reduce.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                    Assert.That(spares.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
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
                Assert.That(collection.RootElement.GetProperty("wallet").GetProperty("shards").GetInt64(),
                    Is.EqualTo(OnboardingGrant.StarterShardCount));
                // Once the separate starter credit is spent, collection must not replenish it.
                await ledger.GrantAsync(new EntitlementGrant(owner.PlayerId,
                    ShardValues.LedgerItem, -OnboardingGrant.StarterShardCount,
                    "test", "craft-test-spent-starter-credit",
                    DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));
                using JsonDocument zeroCollection = JsonDocument.Parse(await http.GetStringAsync("/collection"));
                Assert.That(zeroCollection.RootElement.GetProperty("wallet").GetProperty("shards").GetInt64(), Is.Zero);
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

        private static StringContent Json(string body) => new StringContent(body, Encoding.UTF8, "application/json");

        private static Task Grant(InMemoryEntitlementLedger ledger, Account owner, int cardId, int copies)
            => ledger.GrantAsync(new EntitlementGrant(owner.PlayerId,
                BoosterPackGrantService.CardItemPrefix + cardId, copies, "test", "craft-test-grant-" + cardId,
                DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));

        [Test, CancelAfter(30_000)]
        public async Task Reduce_takes_a_count_and_is_all_or_nothing()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            var owner = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("craft-player"), "craft@example.com");
            var rare = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir()).AllCollectible()
                .First(c => c.Rarity == Rarity.RARE);
            await Grant(ledger, owner, rare.Id, 5);
            string cardItem = BoosterPackGrantService.CardItemPrefix + rare.Id;
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
                string route = "/crafting/" + rare.Id + "/reduce";
                long before = await ledger.BalanceAsync(owner.PlayerId, ShardValues.LedgerItem);

                using var tooMany = await http.PostAsync(route, Json("{\"count\":6}"));
                using JsonDocument tooManyBody = JsonDocument.Parse(await tooMany.Content.ReadAsStringAsync());
                Assert.Multiple(() =>
                {
                    Assert.That(tooMany.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                    Assert.That(tooManyBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("notenoughcopies"));
                    Assert.That(tooManyBody.RootElement.GetProperty("copiesOwned").GetInt32(), Is.EqualTo(5));
                });
                Assert.That(await ledger.BalanceAsync(owner.PlayerId, cardItem), Is.EqualTo(5),
                    "Asking for more than is owned must not reduce the copies that are.");

                foreach (string bad in new[] { "{\"count\":0}", "{\"count\":-2}", "{\"count\":1.5}", "{\"count\":\"3\"}" })
                {
                    using var refused = await http.PostAsync(route, Json(bad));
                    Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), bad);
                }
                Assert.That(await ledger.BalanceAsync(owner.PlayerId, cardItem), Is.EqualTo(5));

                using var three = await http.PostAsync(route, Json("{\"count\":3}"));
                using JsonDocument threeBody = JsonDocument.Parse(await three.Content.ReadAsStringAsync());
                Assert.Multiple(() =>
                {
                    Assert.That(three.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(threeBody.RootElement.GetProperty("copiesOwned").GetInt32(), Is.EqualTo(2));
                    Assert.That(threeBody.RootElement.GetProperty("shards").GetInt64(),
                        Is.EqualTo(before + 3 * ShardValues.ForRarity(Rarity.RARE)));
                });

                using var bodiless = await http.PostAsync(route, null);
                Assert.That(bodiless.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await ledger.BalanceAsync(owner.PlayerId, cardItem), Is.EqualTo(1),
                    "A Reduce with no body is still one copy.");
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        [Test, CancelAfter(30_000)]
        public async Task Reduce_spares_takes_every_copy_past_the_deck_cap_and_legendaries_only_when_asked()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            var owner = await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("craft-player"), "craft@example.com");
            var pool = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir()).AllCollectible();
            var common = pool.First(c => c.Rarity == Rarity.COMMON);
            var atCap = pool.First(c => c.Rarity == Rarity.COMMON && c.Id != common.Id);
            var epic = pool.First(c => c.Rarity == Rarity.EPIC);
            var legendary = pool.First(c => c.Rarity == Rarity.LEGENDARY);
            await Grant(ledger, owner, common.Id, 5);
            await Grant(ledger, owner, atCap.Id, 2);
            await Grant(ledger, owner, epic.Id, 3);
            await Grant(ledger, owner, legendary.Id, 3);
            Task<long> Copies(int cardId) => ledger.BalanceAsync(owner.PlayerId, BoosterPackGrantService.CardItemPrefix + cardId);
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
                long before = await ledger.BalanceAsync(owner.PlayerId, ShardValues.LedgerItem);
                long commonsAndEpic = 3 * ShardValues.ForRarity(Rarity.COMMON) + ShardValues.ForRarity(Rarity.EPIC);

                // No body at all: the default leaves Legendaries alone.
                using var first = await http.PostAsync("/crafting/spares/reduce", null);
                using JsonDocument firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
                Assert.Multiple(() =>
                {
                    Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(firstBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("reduced"));
                    Assert.That(firstBody.RootElement.GetProperty("copiesReduced").GetInt32(), Is.EqualTo(4));
                    Assert.That(firstBody.RootElement.GetProperty("shardsGained").GetInt64(), Is.EqualTo(commonsAndEpic));
                    Assert.That(firstBody.RootElement.GetProperty("shards").GetInt64(), Is.EqualTo(before + commonsAndEpic));
                });
                Assert.Multiple(async () =>
                {
                    Assert.That(await Copies(common.Id), Is.EqualTo(2));
                    Assert.That(await Copies(atCap.Id), Is.EqualTo(2));
                    Assert.That(await Copies(epic.Id), Is.EqualTo(2));
                    Assert.That(await Copies(legendary.Id), Is.EqualTo(3), "Legendaries stay unless the request opts in.");
                });

                using var again = await http.PostAsync("/crafting/spares/reduce", Json("{\"includeLegendary\":false}"));
                using JsonDocument againBody = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
                Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(againBody.RootElement.GetProperty("status").GetString(), Is.EqualTo("nospares"));

                // Anything but a JSON true is not consent to destroy a Legendary.
                using var truthy = await http.PostAsync("/crafting/spares/reduce", Json("{\"includeLegendary\":\"true\"}"));
                Assert.That(truthy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(await Copies(legendary.Id), Is.EqualTo(3));

                using var withLegendary = await http.PostAsync("/crafting/spares/reduce", Json("{\"includeLegendary\":true}"));
                using JsonDocument legendaryBody = JsonDocument.Parse(await withLegendary.Content.ReadAsStringAsync());
                Assert.Multiple(() =>
                {
                    Assert.That(withLegendary.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(legendaryBody.RootElement.GetProperty("copiesReduced").GetInt32(), Is.EqualTo(2));
                    Assert.That(legendaryBody.RootElement.GetProperty("shardsGained").GetInt64(),
                        Is.EqualTo(2 * ShardValues.ForRarity(Rarity.LEGENDARY)));
                });
                Assert.That(await Copies(legendary.Id), Is.EqualTo(1));
                Assert.That(await ledger.BalanceAsync(owner.PlayerId, ShardValues.LedgerItem),
                    Is.EqualTo(before + commonsAndEpic + 2 * ShardValues.ForRarity(Rarity.LEGENDARY)));
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }
}
