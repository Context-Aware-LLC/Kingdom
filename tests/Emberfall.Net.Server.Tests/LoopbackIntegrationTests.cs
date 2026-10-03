using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>
    /// The §14.2 loopback test: the REAL Kestrel app (ServerApp.Build) on an ephemeral port, two
    /// real ClientWebSockets played to a decided match. Deliberately a self-match — the FIFO gate
    /// allows it by design (§5, RC F11) and it is exactly this test's mechanism.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class LoopbackIntegrationTests
    {
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(10);

        [Test]
        [CancelAfter(60_000)]
        public async Task Two_Clients_Pair_Play_And_Reach_Match_End_Over_Real_Sockets()
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-loopback-" + Guid.NewGuid().ToString("N"));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
            });
            await app.StartAsync();
            try
            {
                var baseUri = new Uri(app.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");

                using var http = new HttpClient();
                string health = await http.GetStringAsync(new Uri(baseUri, "/healthz"));
                Assert.That(health, Does.Contain("\"status\":\"ok\""));

                string gate = await http.GetStringAsync(new Uri(baseUri, "/update-gate"));
                Assert.That(gate, Is.EqualTo(
                    "{\"contract\":\"" + ProtocolContract.Version +
                    "\",\"minBuildAndroid\":0,\"minBuildIos\":0}"));

                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await c1.ConnectAsync(wsUri, CancellationToken.None);

                // A fresh socket opens with a hello (C-G1-8); the connect is no longer the hello.
                await SendAsync(c0, ProtocolCodec.Encode(
                    new HelloMsg("loopback-device-0", ProtocolContract.Version, "test", "loopback"), ProtocolCodec.PreMatch, 0));
                await SendAsync(c1, ProtocolCodec.Encode(
                    new HelloMsg("loopback-device-1", ProtocolContract.Version, "test", "loopback"), ProtocolCodec.PreMatch, 0));

                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                var start1 = await ExpectAsync<MatchStartMsg>(c1);
                Assert.That(start0.Snapshot.VerifyIntegrity(), Is.True);
                Assert.That(new[] { start0.Seat, start1.Seat }, Is.EquivalentTo(new[] { 0, 1 }));
                Assert.That(start0.ReconnectToken, Is.Not.Empty);

                // Keep both hands, then seat 1 concedes: a decided match, end to end.
                var matchId = new MatchId(start0.Snapshot.State.Id.Value);
                await SendAsync(c0, ProtocolCodec.Encode(
                    new IntentMsg(0, new MulliganIntent(start0.PlayerId, Array.Empty<InstanceId>())), matchId, 1));
                await SendAsync(c1, ProtocolCodec.Encode(
                    new IntentMsg(0, new MulliganIntent(start1.PlayerId, Array.Empty<InstanceId>())), matchId, 1));
                await SendAsync(c1, ProtocolCodec.Encode(
                    new IntentMsg(0, new ConcedeIntent(start1.PlayerId)), matchId, 2));

                var end0 = await ExpectAsync<MatchEndMsg>(c0);
                var end1 = await ExpectAsync<MatchEndMsg>(c1);
                Assert.That(end0.Reason, Is.EqualTo(MatchEndReason.Concede));
                Assert.That(end0.Outcome, Is.EqualTo(
                    start1.Seat == 0 ? MatchOutcome.Player1Win : MatchOutcome.Player0Win),
                    "the conceding seat loses");
                Assert.That(end1.Outcome, Is.EqualTo(end0.Outcome));

                // The replay log reached disk with the outcome footer (design §9).
                string[] logs = Directory.GetFiles(replays, "*.jsonl");
                Assert.That(logs, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(logs[0]), Does.Contain("\"outcome\""));
            }
            finally
            {
                await app.StopAsync();
                // Training capture copies a completed game's .jsonl on a background task that
                // StopAsync does not await, so the file can still be open here. A temp dir we
                // could not clean is not a test failure -- the same guard the bot-fill
                // fixtures already carry. Identified 2026-09-02: this is the
                // ClientDeckSubmissionTests flake, an IOException out of Directory.Delete.
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
            }
        }

        // ── Wire helpers ─────────────────────────────────────────────────────────

        private static Task SendAsync(ClientWebSocket ws, byte[] frame)
            => ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        /// <summary>Read frames (skipping others) until a T arrives or the timeout trips.</summary>
        private static async Task<T> ExpectAsync<T>(ClientWebSocket ws) where T : NetMessage
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(FrameTimeout);
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
                if (!res.Ok) throw new InvalidOperationException($"Server sent undecodable frame: {res.Error} {res.Detail}");
                if (res.Message is T t) return t;
            }
        }
    }
}
