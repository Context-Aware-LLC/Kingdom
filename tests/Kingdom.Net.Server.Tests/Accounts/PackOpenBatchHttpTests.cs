using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    [TestFixture, NonParallelizable]
    public sealed class PackOpenBatchHttpTests
    {
        private sealed class Clock : ITimeSource
        {
            public long NowMs => 1_700_000_000_000L;
        }

        private sealed class Verifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(
                string rawToken, CancellationToken ct = default)
                => Task.FromResult(rawToken == "valid"
                    ? TokenVerificationResult.Ok(
                        new VerifiedIdentity("batch-player", "batch@example.com", true))
                    : TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));
        }

        [Test, CancelAfter(30_000)]
        public async Task Open_many_route_returns_all_receipts_from_one_post()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                TokenVerifier = new Verifier(),
                AccountStore = accounts,
                EntitlementLedger = ledger,
                PackSeedSource = new FixedPackSeedSource(0xB005_7E12_3456_789AUL),
            });

            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", "valid");
                using var content = new StringContent(
                    "{\"packId\":\"pack.emberfall.starter\",\"count\":3}",
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await http.PostAsync("/packs/open-many", content);
                string responseBody = await response.Content.ReadAsStringAsync();
                using JsonDocument body = JsonDocument.Parse(responseBody);

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody);
                JsonElement[] opens = body.RootElement.GetProperty("opens").EnumerateArray().ToArray();
                Assert.That(opens, Has.Length.EqualTo(3));
                Assert.That(opens.Select(open => open.GetProperty("packInstanceId").GetString())
                                  .Distinct().Count(), Is.EqualTo(3));
                Assert.That(opens.All(open =>
                    open.GetProperty("awards").GetArrayLength() == OnboardingGrant.StarterPackCardCount),
                    Is.True);

                Account account = await accounts.ResolveOrCreateAsync(
                    "supabase", CredentialHash.Of("batch-player"), "batch@example.com");
                Assert.That(await ledger.BalanceAsync(account.PlayerId, OnboardingGrant.StarterPack().Id),
                    Is.EqualTo(OnboardingGrant.StarterPackCount - 3));
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }
}
