using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>Each case is a Play answer a real purchase can produce, and the one verdict it must
    /// map to. A wrong mapping either gives Gems away or tells a paying player their receipt is fake.</summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class PlayStoreReceiptValidatorTests
    {
        private const string Payload = "{\"productId\":\"gems.tier.550\",\"purchaseToken\":\"tok-1\"}";

        private sealed class FakePlay : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _body;
            public List<(HttpMethod Method, string Url)> Requests { get; } = new List<(HttpMethod, string)>();

            public FakePlay(HttpStatusCode status, string body = "")
            {
                _status = status;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add((request.Method, request.RequestUri!.ToString()));
                return Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                });
            }
        }

        private static PlayStoreReceiptValidator Validator(FakePlay play)
            => new PlayStoreReceiptValidator(new HttpClient(play), "com.contextaware.kobi",
                _ => Task.FromResult("access-token"));

        private static StoreReceipt Receipt(string payload = Payload) => new StoreReceipt("play", payload);

        [Test]
        public async Task A_Paid_Purchase_Is_Accepted_With_The_Order_And_Player_Google_Holds()
        {
            var play = new FakePlay(HttpStatusCode.OK,
                "{\"purchaseState\":0,\"orderId\":\"GPA.1\",\"obfuscatedExternalAccountId\":\"player-1\"}");

            StoreReceiptValidation result = await Validator(play).ValidateAsync(Receipt());

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(StoreReceiptStatus.Accepted));
                Assert.That(result.Purchase!.StoreTransactionId, Is.EqualTo("GPA.1"));
                Assert.That(result.Purchase.ProductKey, Is.EqualTo("gems.tier.550"));
                Assert.That(result.Purchase.PlayerId, Is.EqualTo("player-1"));
                Assert.That(play.Requests[0].Url, Is.EqualTo(
                    "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/com.contextaware.kobi/purchases/products/gems.tier.550/tokens/tok-1"));
            });
        }

        [Test]
        public async Task A_Pending_Purchase_Is_Deferred_Not_Rejected()
        {
            var play = new FakePlay(HttpStatusCode.OK,
                "{\"purchaseState\":2,\"obfuscatedExternalAccountId\":\"player-1\"}");

            StoreReceiptValidation result = await Validator(play).ValidateAsync(Receipt());

            Assert.That(result.Status, Is.EqualTo(StoreReceiptStatus.Deferred));
        }

        [Test]
        public async Task A_Canceled_Purchase_Is_Rejected()
        {
            var play = new FakePlay(HttpStatusCode.OK, "{\"purchaseState\":1,\"orderId\":\"GPA.1\"}");

            StoreReceiptValidation result = await Validator(play).ValidateAsync(Receipt());

            Assert.That(result.Status, Is.EqualTo(StoreReceiptStatus.Rejected));
        }

        [Test]
        public async Task A_Token_Google_Does_Not_Know_Is_Rejected()
        {
            StoreReceiptValidation result = await Validator(new FakePlay(HttpStatusCode.NotFound)).ValidateAsync(Receipt());

            Assert.That(result.Status, Is.EqualTo(StoreReceiptStatus.Rejected));
        }

        [Test]
        public void A_Refused_Service_Account_Is_Unavailable_Not_A_Fake_Receipt()
        {
            Assert.ThrowsAsync<StoreUnavailableException>(
                () => Validator(new FakePlay(HttpStatusCode.Unauthorized)).ValidateAsync(Receipt()));
            Assert.ThrowsAsync<StoreUnavailableException>(
                () => Validator(new FakePlay(HttpStatusCode.InternalServerError)).ValidateAsync(Receipt()));
        }

        [Test]
        public async Task A_Product_Outside_The_Catalog_Is_Rejected_Without_Asking_Google()
        {
            var play = new FakePlay(HttpStatusCode.OK, "{\"purchaseState\":0,\"orderId\":\"GPA.1\"}");

            StoreReceiptValidation result = await Validator(play).ValidateAsync(
                Receipt("{\"productId\":\"gems.tier.free\",\"purchaseToken\":\"tok-1\"}"));

            Assert.That(result.Status, Is.EqualTo(StoreReceiptStatus.Rejected));
            Assert.That(play.Requests, Is.Empty);
        }

        [Test]
        public async Task A_Multi_Quantity_Purchase_Is_Rejected_Rather_Than_Granted_Once()
        {
            var play = new FakePlay(HttpStatusCode.OK,
                "{\"purchaseState\":0,\"orderId\":\"GPA.1\",\"quantity\":3,\"obfuscatedExternalAccountId\":\"player-1\"}");

            StoreReceiptValidation result = await Validator(play).ValidateAsync(Receipt());

            Assert.That(result.Status, Is.EqualTo(StoreReceiptStatus.Rejected));
        }

        [Test]
        public async Task A_Service_Account_Key_Signs_An_RS256_Assertion_And_Caches_The_Token()
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            string json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["client_email"] = "iap@kobi.iam.gserviceaccount.com",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
                ["token_uri"] = "https://oauth2.googleapis.com/token",
            });
            string? form = null;
            var google = new CapturingHandler(async request =>
            {
                form = await request.Content!.ReadAsStringAsync();
                return "{\"access_token\":\"ya29.x\",\"expires_in\":3600}";
            });
            var token = GoogleServiceAccountToken.FromJson(json, "scope-x", new HttpClient(google));

            string first = await token.GetAsync(CancellationToken.None);
            string second = await token.GetAsync(CancellationToken.None);

            Assert.That(first, Is.EqualTo("ya29.x"));
            Assert.That(second, Is.EqualTo("ya29.x"));
            Assert.That(google.Calls, Is.EqualTo(1));
            string assertion = Uri.UnescapeDataString(form!.Split("assertion=")[1]);
            string header = Encoding.UTF8.GetString(Convert.FromBase64String(
                Pad(assertion.Split('.')[0].Replace('-', '+').Replace('_', '/'))));
            Assert.That(header, Does.Contain("\"RS256\""));
            Assert.That(form, Does.Contain("grant-type%3Ajwt-bearer"));
        }

        private static string Pad(string b64) => b64 + new string('=', (4 - b64.Length % 4) % 4);

        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<string>> _answer;
            public int Calls { get; private set; }

            public CapturingHandler(Func<HttpRequestMessage, Task<string>> answer) => _answer = answer;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(await _answer(request), Encoding.UTF8, "application/json"),
                };
            }
        }

        [Test]
        public async Task Acknowledge_Posts_To_The_Purchase_Token()
        {
            var play = new FakePlay(HttpStatusCode.NoContent);

            await Validator(play).AcknowledgeAsync(Receipt());

            Assert.That(play.Requests, Has.Count.EqualTo(1));
            Assert.That(play.Requests[0].Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(play.Requests[0].Url, Does.EndWith("/tokens/tok-1:acknowledge"));
        }
    }
}
