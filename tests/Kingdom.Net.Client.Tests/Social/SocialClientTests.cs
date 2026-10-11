using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Social;
using Kingdom.Net.Client.Tests.Support;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Microsoft.AspNetCore.Builder;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Social
{
    /// <summary>
    /// The client half of <c>/social</c> (#409), over the real <see cref="WebSocketTransport"/>
    /// against the REAL server — the same reasoning as the friends gateway's fixture: the likeliest
    /// fault is in the frames between the two, which neither side's own suite can see.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class SocialClientTests
    {
        private sealed class Clock : ITimeSource { public long NowMs => 1_700_000_000_000; }

        /// <summary>The token is the subject; one starting "bad" fails verification.</summary>
        private sealed class Verifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string token, CancellationToken ct = default)
                => Task.FromResult(token.StartsWith("bad", StringComparison.Ordinal)
                    ? TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid)
                    : TokenVerificationResult.Ok(new VerifiedIdentity(token, "", false, "")));
        }

        /// <summary>Retries without sleeping, so a test never waits out a real backoff.</summary>
        private static async Task NoWait(int ms, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        private WebApplication _app = null!;
        private HttpClient _http = null!;
        private Uri _social = null!;

        [SetUp]
        public async Task StartServer()
        {
            _app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = LoopbackServerPeer.CardDataDir(),
                TokenVerifier = new Verifier(),
                AccountStore = new InMemoryAccountStore(new Clock()),
            });
            await _app.StartAsync();
            string baseUrl = _app.Urls.First();
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _social = SocialClient.UriFor(baseUrl);
        }

        [TearDown]
        public async Task StopServer()
        {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        [Test]
        public void The_Route_Is_Derived_From_The_Http_Base()
        {
            Assert.That(SocialClient.UriFor("https://emberfall.fly.dev").ToString(), Is.EqualTo("wss://emberfall.fly.dev/social"));
            Assert.That(SocialClient.UriFor("http://127.0.0.1:8080/").ToString(), Is.EqualTo("ws://127.0.0.1:8080/social"));
        }

        [Test, CancelAfter(30_000)]
        public async Task A_Friend_Is_Seen_Arriving_And_Leaving()
        {
            string annCode = await CodeAsync("ann"), benCode = await CodeAsync("ben");
            await PostCodeAsync("ben", "/friends/request", annCode);
            await PostCodeAsync("ann", "/friends/accept", benCode);

            using var stop = new CancellationTokenSource();
            var ann = new SocialClient(_social, _ => Task.FromResult("ann"), delay: NoWait);
            Task annRun = ann.RunAsync(stop.Token);
            await UntilAsync(() => ann.State == SocialState.Live, "Ann's first full presence list arrives");
            Assert.That(ann.PresenceOf(benCode), Is.EqualTo(PresenceState.Offline));

            using var benStop = new CancellationTokenSource();
            var ben = new SocialClient(_social, _ => Task.FromResult("ben"), delay: NoWait);
            Task benRun = ben.RunAsync(benStop.Token);

            await UntilAsync(() => ann.PresenceOf(benCode) == PresenceState.Online, "Ann sees Ben come online");
            await UntilAsync(() => ben.PresenceOf(annCode) == PresenceState.Online, "Ben's snapshot has Ann in it");

            benStop.Cancel();
            await benRun;
            Assert.That(ben.State, Is.EqualTo(SocialState.Idle));
            await UntilAsync(() => ann.PresenceOf(benCode) == PresenceState.Offline, "Ann sees Ben leave");

            stop.Cancel();
            await annRun;
        }

        [Test, CancelAfter(30_000)]
        public async Task A_Chat_Line_Crosses_The_Real_Server_Cleaned()
        {
            string annCode = await CodeAsync("ann"), benCode = await CodeAsync("ben");
            await PostCodeAsync("ben", "/friends/request", annCode);
            await PostCodeAsync("ann", "/friends/accept", benCode);

            ChatMsg? heard = null, echoed = null;
            using var stop = new CancellationTokenSource();
            var ann = new SocialClient(_social, _ => Task.FromResult("ann"), delay: NoWait);
            var ben = new SocialClient(_social, _ => Task.FromResult("ben"), delay: NoWait);
            ann.Received += msg => { if (msg is ChatMsg c) echoed = c; };
            ben.Received += msg => { if (msg is ChatMsg c) heard = c; };
            Task annRun = ann.RunAsync(stop.Token), benRun = ben.RunAsync(stop.Token);
            await UntilAsync(() => ann.PresenceOf(benCode) == PresenceState.Online, "both are live");

            Assert.That(await ann.SendAsync(new ChatSendMsg(benCode, "gg\n‮wp")), Is.True);
            await UntilAsync(() => heard != null && echoed != null, "the line arrives and comes back");

            Assert.That(heard!.Text, Is.EqualTo("gg wp"));
            Assert.That(heard.Code, Is.EqualTo(annCode));
            Assert.That(heard.Mine, Is.False);
            Assert.That(echoed!.Mine, Is.True);

            stop.Cancel();
            await Task.WhenAll(annRun, benRun);
        }

        [Test, CancelAfter(30_000)]
        public async Task A_Change_To_The_Friends_Graph_Is_Pushed()
        {
            string benCode = await CodeAsync("ben");
            await CodeAsync("ann");

            int changes = 0;
            using var stop = new CancellationTokenSource();
            var ann = new SocialClient(_social, _ => Task.FromResult("ann"), delay: NoWait);
            ann.Received += msg => { if (msg is FriendsChangedMsg) Interlocked.Increment(ref changes); };
            Task run = ann.RunAsync(stop.Token);
            await UntilAsync(() => ann.State == SocialState.Live, "Ann is live");

            await PostCodeAsync("ben", "/friends/request", await CodeAsync("ann"));
            await UntilAsync(() => Volatile.Read(ref changes) > 0, "a request to Ann tells Ann to re-read the list");

            stop.Cancel();
            await run;
        }

        [Test, CancelAfter(30_000)]
        public async Task A_Second_Login_Stops_The_First_Instead_Of_Fighting_It()
        {
            await CodeAsync("ann");

            using var stop = new CancellationTokenSource();
            var first = new SocialClient(_social, _ => Task.FromResult("ann"), delay: NoWait);
            Task firstRun = first.RunAsync(stop.Token);
            await UntilAsync(() => first.State == SocialState.Live, "the first device is live");

            var second = new SocialClient(_social, _ => Task.FromResult("ann"), delay: NoWait);
            Task secondRun = second.RunAsync(stop.Token);

            await firstRun;
            Assert.That(first.State, Is.EqualTo(SocialState.Stopped));
            Assert.That(first.StopReason, Is.EqualTo(SocialClient.ReplacedReason));
            await UntilAsync(() => second.State == SocialState.Live, "the newer device keeps the connection");

            stop.Cancel();
            await secondRun;
        }

        [Test, CancelAfter(30_000)]
        public async Task An_Out_Of_Date_Build_Stops_Rather_Than_Retrying()
        {
            int attempts = 0;
            var client = new SocialClient(_social, _ => { Interlocked.Increment(ref attempts); return Task.FromResult("ann"); },
                delay: NoWait, contractVersion: "0.0.0-old");

            await client.RunAsync(CancellationToken.None);

            Assert.That(client.State, Is.EqualTo(SocialState.Stopped));
            Assert.That(client.StopReason, Is.EqualTo(SocialClient.ContractMismatchReason));
            Assert.That(attempts, Is.EqualTo(1));
        }

        [Test, CancelAfter(30_000)]
        public async Task Nobody_Signed_In_Never_Opens_A_Socket()
        {
            bool built = false;
            var client = new SocialClient(_social, _ => Task.FromResult(""),
                transports: () => { built = true; return new WebSocketTransport(); }, delay: NoWait);

            await client.RunAsync(CancellationToken.None);

            Assert.That(client.State, Is.EqualTo(SocialState.Stopped));
            Assert.That(built, Is.False);
        }

        [Test, CancelAfter(30_000)]
        public async Task A_Rejected_Token_Retries_With_A_Fresh_One()
        {
            await CodeAsync("ann");

            // The first token this device holds has expired; the provider refreshes it on the next ask.
            int asks = 0;
            using var stop = new CancellationTokenSource();
            var client = new SocialClient(_social,
                _ => Task.FromResult(Interlocked.Increment(ref asks) == 1 ? "bad-expired" : "ann"), delay: NoWait);
            Task run = client.RunAsync(stop.Token);

            await UntilAsync(() => client.State == SocialState.Live, "the second attempt, with a good token, goes live");
            Assert.That(asks, Is.GreaterThanOrEqualTo(2));

            stop.Cancel();
            await run;
        }

        [Test]
        public async Task Sending_Without_A_Connection_Fails_Where_The_Caller_Can_See_It()
        {
            var client = new SocialClient(_social, _ => Task.FromResult("ann"), delay: NoWait);
            Assert.That(await client.SendAsync(new ChallengeSendMsg("ABCDE-FGHJK")), Is.False);
        }

        private async Task<string> CodeAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/friends");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _http.SendAsync(request);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("code").GetString()!;
        }

        private async Task PostCodeAsync(string token, string path, string code)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent($"{{\"code\":\"{code}\"}}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _http.SendAsync(request);
            Assert.That((int)response.StatusCode, Is.EqualTo(200), path);
        }

        private static async Task UntilAsync(Func<bool> condition, string because)
        {
            for (int i = 0; i < 500; i++)
            {
                if (condition()) return;
                await Task.Delay(20);
            }
            Assert.Fail("Timed out waiting until " + because);
        }
    }
}
