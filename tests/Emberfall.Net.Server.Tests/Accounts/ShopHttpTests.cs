using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    [TestFixture, NonParallelizable]
    public sealed class ShopHttpTests
    {
        private sealed class Clock : ITimeSource { public long NowMs => 1_700_000_000_000; }
        private sealed class Verifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string token, CancellationToken ct = default)
                => Task.FromResult(token == "valid"
                    ? TokenVerificationResult.Ok(new VerifiedIdentity("shop-http", "buyer@example.com", true))
                    : TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));
        }

        [Test, CancelAfter(30_000)]
        public async Task Purchase_route_charges_once_and_adds_an_unopened_vault_pack()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            Account buyer = await accounts.ResolveOrCreateAsync("supabase",
                CredentialHash.Of("shop-http"), "buyer@example.com");
            await ledger.GrantAsync(new EntitlementGrant(buyer.PlayerId, EconomyItems.Gold, 400,
                "test", "shop-http-gold", DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));
            await ledger.GrantAsync(new EntitlementGrant(buyer.PlayerId,
                OnboardingGrant.StarterPack().Id, 2, "test", "shop-http-vault",
                DateTimeOffset.FromUnixTimeMilliseconds(new Clock().NowMs)));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                TokenVerifier = new Verifier(),
                AccountStore = accounts,
                EntitlementLedger = ledger,
                PackSeedSource = new FixedPackSeedSource(1),
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
                using var shop = await http.GetAsync("/shop");
                Assert.That(shop.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using JsonDocument catalog = JsonDocument.Parse(await shop.Content.ReadAsStringAsync());
                Assert.That(catalog.RootElement.GetProperty("packs").GetArrayLength(), Is.GreaterThan(0));
                long beforeVaultCount = catalog.RootElement.GetProperty("vaultCount").GetInt64();
                Assert.That(beforeVaultCount, Is.GreaterThanOrEqualTo(2),
                    "The badge includes both existing packs and any onboarding gift.");

                const string body = "{\"packId\":\"pack.emberfall\",\"currency\":\"gold\"," +
                                    "\"requestId\":\"e448172b-6775-41d3-9f6b-89a3cd93a925\"}";
                using var first = await http.PostAsync("/shop/purchase",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                using var replay = await http.PostAsync("/shop/purchase",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using JsonDocument receipt = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
                Assert.That(receipt.RootElement.GetProperty("status").GetString(), Is.EqualTo("purchased"));
                Assert.That(receipt.RootElement.GetProperty("unopenedCount").GetInt64(), Is.EqualTo(1));
                Assert.That(receipt.RootElement.GetProperty("vaultCount").GetInt64(),
                    Is.EqualTo(beforeVaultCount + 1));
                using JsonDocument replayReceipt = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
                Assert.That(replayReceipt.RootElement.GetProperty("vaultCount").GetInt64(),
                    Is.EqualTo(beforeVaultCount + 1), "a replay must not increment the badge twice");
                Assert.That(receipt.RootElement.TryGetProperty("awards", out _), Is.False,
                    "purchase must not reveal cards");
                Assert.That(await ledger.BalanceAsync(buyer.PlayerId, EconomyItems.Gold), Is.EqualTo(200));
                Assert.That(await ledger.BalanceAsync(buyer.PlayerId, "pack.emberfall"), Is.EqualTo(1));
                using JsonDocument collection = JsonDocument.Parse(await http.GetStringAsync("/collection"));
                Assert.That(collection.RootElement.GetProperty("packs").GetProperty("pack.emberfall")
                    .GetInt64(), Is.EqualTo(1), "the existing Vault shelf reads this balance");
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }
}
