using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;
using Kingdom.Net.Client.Social;
using Kingdom.Net.Client.Tests.Auth;
using Kingdom.Net.Client.Tests.Support;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Social
{
    /// <summary>
    /// The friends gateway (#409). The first fixture runs it against the REAL server, because the
    /// thing most likely to be wrong is not either side but the JSON between them — the client's
    /// reader is hand-rolled and keeps a bare literal as text, so a server that wrote
    /// <c>"displayName":null</c> would produce a friend called "null" with both suites green.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class HttpFriendsGatewayAgainstTheServerTests
    {
        private sealed class Clock : ITimeSource { public long NowMs => 1_700_000_000_000; }

        /// <summary>The token is the subject; <c>subject|Name</c> carries a sign-up name.</summary>
        private sealed class Verifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string token, CancellationToken ct = default)
            {
                string[] parts = token.Split('|');
                return Task.FromResult(TokenVerificationResult.Ok(
                    new VerifiedIdentity(parts[0], "", false, parts.Length > 1 ? parts[1] : "")));
            }
        }

        /// <summary><see cref="IAuthHttp"/> over a real <see cref="HttpClient"/>.</summary>
        private sealed class RealHttp : IAuthHttp
        {
            private readonly HttpClient _http = new HttpClient();

            public Task<AuthHttpResponse> GetAsync(string url, IReadOnlyList<KeyValuePair<string, string>> headers,
                CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Get, url, null, headers);

            public Task<AuthHttpResponse> PostJsonAsync(string url, string jsonBody,
                IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken = default)
                => SendAsync(HttpMethod.Post, url, jsonBody, headers);

            public Task<AuthHttpResponse> PutJsonAsync(string url, string jsonBody,
                IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken = default)
                => SendAsync(HttpMethod.Put, url, jsonBody, headers);

            public Task<AuthHttpResponse> DeleteAsync(string url, IReadOnlyList<KeyValuePair<string, string>> headers,
                CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, url, null, headers);

            private async Task<AuthHttpResponse> SendAsync(HttpMethod method, string url, string? body,
                IReadOnlyList<KeyValuePair<string, string>> headers)
            {
                using var request = new HttpRequestMessage(method, url);
                foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await _http.SendAsync(request);
                return AuthHttpResponse.Completed((int)response.StatusCode, await response.Content.ReadAsStringAsync());
            }
        }

        [Test, CancelAfter(30_000)]
        public async Task Two_Players_Become_Friends_Through_The_Gateway()
        {
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = LoopbackServerPeer.CardDataDir(),
                TokenVerifier = new Verifier(),
                AccountStore = new InMemoryAccountStore(new Clock()),
            });
            await app.StartAsync();
            try
            {
                var gateway = new HttpFriendsGateway(app.Urls.First(), new RealHttp());

                FriendsSnapshot ann = await gateway.FetchAsync("ann");
                FriendsSnapshot ben = await gateway.FetchAsync("ben");
                Assert.That(ann.Status, Is.EqualTo(FriendsCallStatus.Ok));
                Assert.That(FriendCode.TryNormalize(ann.Code, out _), Is.True, "the server's code passes the client's check");
                Assert.That(ann.DisplayName, Is.Empty, "no name chosen must not read as a name");

                Assert.That((await gateway.FetchAsync("ann|Ann Bell")).DisplayName, Is.EqualTo("Ann Bell"),
                    "the sign-up name in the token is the name, with no call to set it");

                FriendsSnapshot sent = await gateway.RequestAsync("ann", FriendCode.Display(ben.Code));
                Assert.That(sent.Outgoing.Single().Code, Is.EqualTo(ben.Code));

                FriendsSnapshot accepted = await gateway.AcceptAsync("ben", ann.Code);
                Assert.That(accepted.Status, Is.EqualTo(FriendsCallStatus.Ok));
                Assert.That(accepted.Friends.Single().Code, Is.EqualTo(ann.Code));
                Assert.That(accepted.Friends.Single().DisplayName, Is.EqualTo("Ann Bell"));

                FriendsSnapshot annNow = await gateway.FetchAsync("ann");
                Assert.That(annNow.Friends.Single().DisplayName, Is.Empty,
                    "Ben chose no name: the list must show that as absent, never as the word null");

                Assert.That((await gateway.AcceptAsync("ann", FriendCode.FromValue(5))).Status,
                    Is.EqualTo(FriendsCallStatus.NotFound));
                Assert.That((await gateway.RequestAsync("ann", "not-a-code")).Status,
                    Is.EqualTo(FriendsCallStatus.InvalidCode));

                Assert.That((await gateway.BlockAsync("ben", ann.Code)).Blocked.Single().Code, Is.EqualTo(ann.Code));
                Assert.That((await gateway.UnblockAsync("ben", ann.Code)).Blocked, Is.Empty);
                Assert.That((await gateway.RemoveAsync("ann", ben.Code)).Status, Is.EqualTo(FriendsCallStatus.Ok));
                Assert.That((await gateway.DeclineAsync("ann", ben.Code)).Status, Is.EqualTo(FriendsCallStatus.Ok));
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }

    /// <summary>What the gateway does when the server is not the happy path.</summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class HttpFriendsGatewayTests
    {
        private const string Base = "https://emberfall.fly.dev";

        [Test]
        public async Task A_Signed_Out_Device_Never_Reaches_The_Network()
        {
            var http = new FakeAuthHttp();

            FriendsSnapshot snapshot = await new HttpFriendsGateway(Base, http).FetchAsync("");

            Assert.That(snapshot.Status, Is.EqualTo(FriendsCallStatus.Unauthenticated));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public async Task No_Network_Is_Offline_And_Carries_No_Lists()
        {
            var http = new FakeAuthHttp().ReplyTransportFailure();

            FriendsSnapshot snapshot = await new HttpFriendsGateway(Base, http).FetchAsync("jwt");

            Assert.That(snapshot.Status, Is.EqualTo(FriendsCallStatus.Offline));
            Assert.That(snapshot.Code, Is.Empty);
        }

        [TestCase(401, "{}", FriendsCallStatus.Unauthenticated)]
        [TestCase(503, "{}", FriendsCallStatus.Unavailable)]
        [TestCase(429, "not json", FriendsCallStatus.RateLimited)]
        [TestCase(409, "{\"status\":\"atcapacity\"}", FriendsCallStatus.AtCapacity)]
        [TestCase(400, "{\"status\":\"invalidcode\"}", FriendsCallStatus.InvalidCode)]
        public async Task Each_Refusal_Keeps_Its_Own_Status(int code, string body, FriendsCallStatus expected)
        {
            var http = new FakeAuthHttp().Reply(code, body);

            FriendsSnapshot snapshot = await new HttpFriendsGateway(Base, http).RequestAsync("jwt", "2222222222");

            Assert.That(snapshot.Status, Is.EqualTo(expected));
        }

        [TestCase("{\"status\":\"known\"}")]
        [TestCase("{\"status\":\"known\",\"code\":\"2222222222\",\"friends\":[],\"incoming\":[],\"outgoing\":[]}")]
        [TestCase("{\"status\":\"known\",\"code\":\"2222222222\",\"friends\":[{\"displayName\":\"x\"}],\"incoming\":[],\"outgoing\":[],\"blocked\":[]}")]
        public async Task A_200_Missing_Part_Of_The_Graph_Is_Malformed_Not_A_Smaller_Graph(string body)
        {
            var http = new FakeAuthHttp().Reply(200, body);

            FriendsSnapshot snapshot = await new HttpFriendsGateway(Base, http).FetchAsync("jwt");

            Assert.That(snapshot.Status, Is.EqualTo(FriendsCallStatus.Malformed));
            Assert.That(snapshot.Friends, Is.Empty);
        }
    }
}
