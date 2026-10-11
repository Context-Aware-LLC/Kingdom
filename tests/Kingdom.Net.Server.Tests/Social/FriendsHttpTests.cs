using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Social
{
    /// <summary>
    /// The <c>/friends</c> routes on the real app. <see cref="FriendsEndpointTests"/> covers the
    /// decisions; this covers what only the wire has — the JSON a client will parse, and the body
    /// read that happens before authentication.
    /// </summary>
    [TestFixture, NonParallelizable]
    public sealed class FriendsHttpTests
    {
        private static HttpRequestMessage As(string token, HttpMethod method, string path, string? json = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }

        private static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, HttpStatusCode expect)
        {
            using HttpResponseMessage response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.That(response.StatusCode, Is.EqualTo(expect), body);
            return JsonDocument.Parse(body);
        }

        [Test, CancelAfter(30_000)]
        public async Task Two_Players_Add_Each_Other_And_No_Player_Id_Reaches_The_Wire()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                TokenVerifier = new FriendsEndpointTests.SubjectVerifier(),
                AccountStore = accounts,
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

                // Ann's token carries her sign-up name; that is the only way a name gets in.
                using JsonDocument ann = await SendAsync(http, As("ann|Ann Bell", HttpMethod.Get, "/friends"), HttpStatusCode.OK);
                using JsonDocument ben = await SendAsync(http, As("noemail-ben", HttpMethod.Get, "/friends"), HttpStatusCode.OK);
                string annCode = ann.RootElement.GetProperty("code").GetString()!;
                string benCode = ben.RootElement.GetProperty("code").GetString()!;
                Assert.That(ann.RootElement.GetProperty("status").GetString(), Is.EqualTo("known"));
                Assert.That(ann.RootElement.GetProperty("friends").GetArrayLength(), Is.EqualTo(0));

                Assert.That(ann.RootElement.GetProperty("displayName").GetString(), Is.EqualTo("Ann Bell"));
                Assert.That(ben.RootElement.GetProperty("displayName").GetString(), Is.Empty,
                    "no name is the empty string, never null — the client's reader would draw null as a name");

                using JsonDocument sent = await SendAsync(http,
                    As("ann", HttpMethod.Post, "/friends/request", $"{{\"code\":\"{benCode}\"}}"), HttpStatusCode.OK);
                JsonElement outgoing = sent.RootElement.GetProperty("outgoing")[0];
                Assert.That(outgoing.GetProperty("code").GetString(), Is.EqualTo(benCode));
                Assert.That(outgoing.TryGetProperty("displayName", out _), Is.False,
                    "an outgoing request must not carry the holder's name — a code somebody guessed " +
                    "would otherwise look them up");

                using JsonDocument accepted = await SendAsync(http,
                    As("noemail-ben", HttpMethod.Post, "/friends/accept", $"{{\"code\":\"{annCode}\"}}"), HttpStatusCode.OK);
                JsonElement friend = accepted.RootElement.GetProperty("friends")[0];
                Assert.That(accepted.RootElement.GetProperty("status").GetString(), Is.EqualTo("accepted"));
                Assert.That(friend.GetProperty("code").GetString(), Is.EqualTo(annCode));
                Assert.That(friend.GetProperty("displayName").GetString(), Is.EqualTo("Ann Bell"));

                string annId = (await accounts.ResolveOrCreateAsync(
                    "supabase", Kingdom.Net.Server.Identity.CredentialHash.Of("ann"))).PlayerId;
                Assert.That(accepted.RootElement.GetRawText(), Does.Not.Contain(annId),
                    "the code is the only identifier of another player a client is ever given");
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        [Test, CancelAfter(30_000)]
        public async Task A_Refusal_Carries_No_Lists_And_An_Oversized_Body_Is_Not_Read()
        {
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                TokenVerifier = new FriendsEndpointTests.SubjectVerifier(),
                AccountStore = new InMemoryAccountStore(new FakeTime()),
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

                using JsonDocument refused = await SendAsync(http,
                    As("bad-token", HttpMethod.Get, "/friends"), HttpStatusCode.Unauthorized);
                Assert.That(refused.RootElement.TryGetProperty("friends", out _), Is.False,
                    "an absent list cannot be misread as an empty one");

                string huge = "{\"code\":\"" + new string('2', 4096) + "\"}";
                using JsonDocument oversized = await SendAsync(http,
                    As("ann", HttpMethod.Post, "/friends/request", huge), HttpStatusCode.BadRequest);
                Assert.That(oversized.RootElement.GetProperty("status").GetString(), Is.EqualTo("invalidcode"));
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }
}
