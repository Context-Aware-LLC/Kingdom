using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Ranking;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Social
{
    /// <summary>
    /// #409's acceptance, on the real app over real sockets: two accounts, one with no email, add
    /// each other by code, see each other's presence, and play a private match that pays Gold and
    /// leaves both ratings where they were — with a ticket nobody else can use.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class FriendChallengeEndToEndTests
    {
        private const string Ann = "ann", Ben = "noemail-ben", Cy = "cy";

        [Test]
        [CancelAfter(90_000)]
        public async Task Friends_Challenge_Play_A_Private_Match_That_Pays_Gold_And_Moves_No_Rating()
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-challenge-" + Guid.NewGuid().ToString("N"));
            var accounts = new InMemoryAccountStore(new FakeTime());
            var ledger = new InMemoryEntitlementLedger();
            var ratings = new InMemoryRatingStore();
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                TokenVerifier = new FriendsEndpointTests.SubjectVerifier(),
                AccountStore = accounts,
                EntitlementLedger = ledger,
                RatingStore = ratings,
            });
            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                string ws = $"ws://127.0.0.1:{baseUri.Port}";
                using var http = new HttpClient { BaseAddress = baseUri };

                // ── friends, by code ────────────────────────────────────────────────────
                string annCode = await CodeAsync(http, Ann), benCode = await CodeAsync(http, Ben);
                await PostCodeAsync(http, Ann, "/friends/request", benCode);

                using ClientWebSocket annSocial = await SocialAsync(ws, Ann);
                using ClientWebSocket benSocial = await SocialAsync(ws, Ben);
                Assert.That((await ExpectSocialAsync<PresenceMsg>(annSocial)).Friends, Is.Empty,
                    "not friends yet, so Ben's presence is not Ann's to see");

                await PostCodeAsync(http, Ben, "/friends/accept", annCode);

                // The accept is pushed: Ann is told to re-read, and now sees Ben online.
                await ExpectSocialAsync<FriendsChangedMsg>(annSocial);
                FriendPresence benSeen = (await ExpectSocialAsync<PresenceMsg>(annSocial)).Friends.Single();
                Assert.That(benSeen.Code, Is.EqualTo(benCode));
                Assert.That(benSeen.State, Is.EqualTo(PresenceState.Online));

                // ── the challenge ───────────────────────────────────────────────────────
                await SendSocialAsync(annSocial, new ChallengeSendMsg(benCode));
                ChallengeIncomingMsg incoming = await ExpectSocialAsync<ChallengeIncomingMsg>(benSocial);
                Assert.That(incoming.Code, Is.EqualTo(annCode));
                await SendSocialAsync(benSocial, new ChallengeAcceptMsg(incoming.ChallengeId));
                string annTicket = (await ExpectSocialAsync<ChallengeReadyMsg>(annSocial)).Ticket;
                string benTicket = (await ExpectSocialAsync<ChallengeReadyMsg>(benSocial)).Ticket;

                // ── a ticket is no use to anybody else ──────────────────────────────────
                using (ClientWebSocket thief = await MatchSocketAsync(ws, Cy, annTicket))
                    Assert.That(await ClosedAsync(thief), Is.True,
                        "Ann's ticket presented by another signed-in account must seat nobody");
                using (ClientWebSocket guest = await MatchSocketAsync(ws, authToken: "", benTicket))
                    Assert.That(await ClosedAsync(guest), Is.True, "nor by a guest");

                // ── the private match ───────────────────────────────────────────────────
                using ClientWebSocket annMatch = await MatchSocketAsync(ws, Ann, annTicket);
                using ClientWebSocket benMatch = await MatchSocketAsync(ws, Ben, benTicket);
                MatchStartMsg annStart = await ExpectAsync<MatchStartMsg>(annMatch);
                MatchStartMsg benStart = await ExpectAsync<MatchStartMsg>(benMatch);
                var matchId = new MatchId(annStart.Snapshot.State.Id.Value);
                Assert.That(benStart.Snapshot.State.Id.Value, Is.EqualTo(matchId.Value), "the same match");

                string annId = await PlayerIdAsync(accounts, Ann), benId = await PlayerIdAsync(accounts, Ben);
                var seats = built.Gate.AccountsOf(matchId)!.Value;
                Assert.That(new[] { seats.p0.Value, seats.p1.Value }, Is.EquivalentTo(new[] { annId, benId }));

                PresenceMsg inMatch = await ExpectSocialAsync<PresenceMsg>(annSocial);
                Assert.That(inMatch.Friends.Single().State, Is.EqualTo(PresenceState.InMatch));

                await SendAsync(annMatch, ProtocolCodec.Encode(
                    new IntentMsg(0, new MulliganIntent(annStart.PlayerId, Array.Empty<InstanceId>())), matchId, 1));
                await SendAsync(benMatch, ProtocolCodec.Encode(
                    new IntentMsg(0, new MulliganIntent(benStart.PlayerId, Array.Empty<InstanceId>())), matchId, 1));
                await SendAsync(benMatch, ProtocolCodec.Encode(
                    new IntentMsg(0, new ConcedeIntent(benStart.PlayerId)), matchId, 2));
                await ExpectAsync<MatchEndMsg>(annMatch);

                // ── Sam's ruling, 2026-10-01: quests and Gold pay, ranking does not ──────
                await UntilAsync(async () =>
                    (await ledger.HistoryAsync(annId)).Any(g => g.Source == "match_reward") &&
                    (await ledger.HistoryAsync(benId)).Any(g => g.Source == "match_reward"),
                    "both seats of a friend match must be paid");
                Assert.That(await ratings.GetLeaderboardAsync(20), Is.Empty,
                    "a friend match must not move a rating");
                Assert.That(await ratings.GetRatingAsync(AccountId.FromAuthenticatedAccount(annId)),
                    Is.EqualTo(await ratings.GetRatingAsync(AccountId.FromAuthenticatedAccount(benId))));
            }
            finally
            {
                await built.App.StopAsync();
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────

        private static async Task<string> PlayerIdAsync(InMemoryAccountStore accounts, string subject)
            => (await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of(subject))).PlayerId;

        private static async Task<string> CodeAsync(HttpClient http, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/friends");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await http.SendAsync(request);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("code").GetString()!;
        }

        private static async Task PostCodeAsync(HttpClient http, string token, string path, string code)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent($"{{\"code\":\"{code}\"}}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await http.SendAsync(request);
            Assert.That((int)response.StatusCode, Is.EqualTo(200), path);
        }

        private static async Task<ClientWebSocket> SocialAsync(string ws, string token)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(ws + "/social"), CancellationToken.None);
            await SendSocialAsync(socket, new SocialHelloMsg(token, ProtocolContract.Version));
            return socket;
        }

        private static async Task<ClientWebSocket> MatchSocketAsync(string ws, string authToken, string ticket)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"{ws}/ws?challenge={ticket}"), CancellationToken.None);
            await SendAsync(socket, ProtocolCodec.Encode(
                new HelloMsg("challenge-e2e-device-" + authToken, ProtocolContract.Version, "test", "challenge-e2e",
                    DisplayName: "", AuthToken: authToken),
                ProtocolCodec.PreMatch, 0));
            return socket;
        }

        private static Task SendAsync(ClientWebSocket socket, byte[] frame)
            => socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        private static Task SendSocialAsync(ClientWebSocket socket, SocialMsg msg)
            => SendAsync(socket, SocialCodec.Encode(msg));

        private static async Task<byte[]?> ReceiveAsync(ClientWebSocket socket, TimeSpan timeout)
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(timeout);
            int total = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                total += result.Count;
            } while (!result.EndOfMessage);
            return new ReadOnlySpan<byte>(buffer, 0, total).ToArray();
        }

        /// <summary>Read social frames, skipping others, until a T arrives.</summary>
        private static async Task<T> ExpectSocialAsync<T>(ClientWebSocket socket) where T : SocialMsg
        {
            while (true)
            {
                byte[] frame = await ReceiveAsync(socket, TimeSpan.FromSeconds(10))
                    ?? throw new InvalidOperationException($"Social socket closed while waiting for {typeof(T).Name}.");
                if (!SocialCodec.TryDecode(frame, out SocialMsg? msg))
                    throw new InvalidOperationException("Server sent an undecodable social frame.");
                if (msg is T wanted) return wanted;
            }
        }

        private static async Task<T> ExpectAsync<T>(ClientWebSocket socket) where T : NetMessage
        {
            while (true)
            {
                byte[] frame = await ReceiveAsync(socket, TimeSpan.FromSeconds(10))
                    ?? throw new InvalidOperationException($"Socket closed while waiting for {typeof(T).Name}.");
                var decoded = ProtocolCodec.Decode(frame);
                if (!decoded.Ok) throw new InvalidOperationException($"Undecodable frame: {decoded.Error} {decoded.Detail}");
                if (decoded.Message is T wanted) return wanted;
            }
        }

        private static async Task<bool> ClosedAsync(ClientWebSocket socket)
        {
            try
            {
                while (true)
                    if (await ReceiveAsync(socket, TimeSpan.FromSeconds(10)) is null) return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (WebSocketException) { return true; }
        }

        private static async Task UntilAsync(Func<Task<bool>> condition, string because)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (await condition()) return;
                await Task.Delay(50);
            }
            Assert.Fail(because);
        }
    }
}
