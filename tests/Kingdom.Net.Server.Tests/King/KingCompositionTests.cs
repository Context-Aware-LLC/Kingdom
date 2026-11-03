using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Decks;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Transport;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary>
    /// The King's Favor through the real composition root. The seating reaches the worker that banks
    /// it only while <c>AchievementSink</c> is the gate's telemetry sink, so this is the test that
    /// notices a decorator stacked around it.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class KingCompositionTests
    {
        [Test]
        public void A_gate_with_a_Favor_store_refuses_a_sink_that_cannot_bank_it()
        {
            Assert.Throws<InvalidOperationException>(() => new MatchGate(
                new ServerOptions(), new FakeTime(), SessionHarness.Db, new PrebuiltDeckProvider(),
                new InMemoryReplayStore(), new InMemoryTelemetrySink(),
                favor: new InMemoryFavorStore(), reignOneMs: 0));
        }

        [Test]
        public void A_gate_without_a_Favor_store_takes_any_sink()
        {
            Assert.DoesNotThrow(() => new MatchGate(
                new ServerOptions(), new FakeTime(), SessionHarness.Db, new PrebuiltDeckProvider(),
                new InMemoryReplayStore(), new InMemoryTelemetrySink()));
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_ladder_match_banks_its_King_through_the_real_server()
        {
            // The reign whose commission is Technically, so a conceded match banks the winner's Health.
            int reign = Enumerable.Range(1, CommissionIds.Launch.Count)
                .First(r => KingSchedule.For(r).Commission == CommissionIds.Technically);
            long reignOneMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                              - (reign - 1) * KingCalendar.WeekMs - 60_000;
            var favor = new InMemoryFavorStore();
            string replays = Path.Combine(Path.GetTempPath(), "kingdom-king-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                TokenVerifier = new PerTokenVerifier(),
                AccountStore = new InMemoryAccountStore(new FakeTime()),
                Favor = favor,
                ReignOneMs = reignOneMs,
            });

            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, "token-a");
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, "token-b");

                MatchStartMsg start0 = await ExpectAsync<MatchStartMsg>(c0);
                MatchStartMsg start1 = await ExpectAsync<MatchStartMsg>(c1);
                var matchId = new MatchId(start0.Snapshot.State.Id.Value);

                await SendAsync(c0, new IntentMsg(0, new MulliganIntent(start0.PlayerId, Array.Empty<InstanceId>())), matchId, 1);
                await SendAsync(c1, new IntentMsg(0, new MulliganIntent(start1.PlayerId, Array.Empty<InstanceId>())), matchId, 1);
                await SendAsync(c1, new IntentMsg(0, new ConcedeIntent(start1.PlayerId)), matchId, 2);
                await ExpectAsync<MatchEndMsg>(c0);

                // The bank happens off the match thread; give the worker a moment.
                IReadOnlyList<FavorRow> board = Array.Empty<FavorRow>();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (board.Count == 0 && DateTime.UtcNow < deadline)
                {
                    board = await favor.TopAsync(reign, lowerIsBetter: true, limit: 10);
                    if (board.Count == 0) await Task.Delay(50);
                }

                Assert.That(board, Has.Count.EqualTo(1), "the winner banks; Technically needs a win, so the loser does not");
                Assert.That(board[0].DisplayName, Is.EqualTo("Alice"), "the name the winner played under");
                Assert.That(board[0].Score, Is.GreaterThan(0), "a conceded match is won at the winner's Health");
            }
            finally
            {
                await built.App.StopAsync();
                try { Directory.Delete(replays, recursive: true); } catch (IOException) { }
            }
        }

        /// <summary>Each auth token is its own player, so the two seats are two accounts. A signed-in
        /// seat's name comes from its identity, not its hello.</summary>
        private sealed class PerTokenVerifier : IIdentityTokenVerifier
        {
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(TokenVerificationResult.Ok(
                    new VerifiedIdentity("subject-" + rawToken, rawToken + "@example.com", EmailVerified: true,
                        DisplayName: rawToken == "token-a" ? "Alice" : "Bobby")));
        }

        private static Task SendHelloAsync(ClientWebSocket ws, string authToken)
            => ws.SendAsync(
                ProtocolCodec.Encode(
                    new HelloMsg("device-" + authToken, ProtocolContract.Version, "test", "king-tests",
                        DisplayName: "", AuthToken: authToken),
                    ProtocolCodec.PreMatch, 0),
                WebSocketMessageType.Text, true, CancellationToken.None);

        private static Task SendAsync(ClientWebSocket ws, NetMessage msg, MatchId matchId, ulong seq)
            => ws.SendAsync(ProtocolCodec.Encode(msg, matchId, seq), WebSocketMessageType.Text, true,
                CancellationToken.None);

        private static async Task<T> ExpectAsync<T>(ClientWebSocket ws) where T : NetMessage
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException($"Socket closed while waiting for {typeof(T).Name}.");
                    total += result.Count;
                } while (!result.EndOfMessage);

                var res = ProtocolCodec.Decode(new ReadOnlySpan<byte>(buffer, 0, total).ToArray());
                if (!res.Ok) throw new InvalidOperationException("Server sent an undecodable frame.");
                if (res.Message is T t) return t;
            }
        }
    }
}
