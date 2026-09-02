using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Tests.Support;
using Emberfall.Net.Server;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests
{
    /// <summary>
    /// The commit-2 wss proof, network-independent: the REAL <see cref="ServerApp"/> Kestrel host on
    /// an ephemeral loopback port, two <see cref="NetworkedMatchSession"/> over REAL
    /// <see cref="WebSocketTransport"/> (ClientWebSocket), paired by B1's FIFO gate and played to a
    /// decided outcome. This is the local twin of the live-droplet verify (b3 §11.1 row 1 / §14 step
    /// 2 / §8): same client stack, same server, only the box differs. Everything §11's loopback
    /// tests cannot prove about real socket framing is proven here except NAT/TLS, which is the
    /// droplet's job.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class WebSocketTransportIntegrationTests
    {
        [Test]
        [CancelAfter(60_000)]
        public async Task Two_Clients_Over_Real_Sockets_Pair_Play_And_Reach_A_Decided_End()
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-client-ws-" + Guid.NewGuid().ToString("N"));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = LoopbackServerPeer.CardDataDir(),
                ReplaysDir = replays,
            });
            await app.StartAsync();

            int misattributions = 0;
            // (a clean, promptly-played match races no clocks — the evidence-gate counter stays zero)
            var c0 = new NetworkedMatchSession(new WebSocketTransport(),
                new ClientOptions { DeviceToken = "test-device-0", OnMisattribution = _ => System.Threading.Interlocked.Increment(ref misattributions) });
            var c1 = new NetworkedMatchSession(new WebSocketTransport(), new ClientOptions { DeviceToken = "test-device-1" });

            try
            {
                var baseUri = new Uri(app.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");

                // FIFO gate pairs the first two sockets — connect both concurrently.
                await Task.WhenAll(c0.ConnectAsync(wsUri), c1.ConnectAsync(wsUri));

                Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
                Assert.That(c1.State, Is.EqualTo(ConnectionState.Live));
                Assert.That(new[] { c0.Seat, c1.Seat }, Is.EquivalentTo(new[] { 0, 1 }));
                Assert.That(c0.Current.Phase, Is.EqualTo(TurnPhase.Mulligan));

                Task ended0 = WaitForState(c0, ConnectionState.Ended);
                Task ended1 = WaitForState(c1, ConnectionState.Ended);

                // Keep both hands, then seat 1 concedes → a decided match end to end over real wss.
                CommandResult m0 = await c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
                CommandResult m1 = await c1.SubmitAsync(new MulliganIntent(c1.Player, Array.Empty<InstanceId>()));
                Assert.That(m0.Accepted, Is.True);
                Assert.That(m1.Accepted, Is.True);

                _ = c1.SubmitAsync(new ConcedeIntent(c1.Player));
                await Task.WhenAll(ended0, ended1).WaitAsync(TimeSpan.FromSeconds(20));

                Assert.That(c0.Current.Outcome, Is.Not.EqualTo(MatchOutcome.InProgress), "the match is decided");
                Assert.That(c0.ResyncCount, Is.EqualTo(0), "no integrity failures over the real socket");
                Assert.That(misattributions, Is.EqualTo(0), "no clock race in a promptly-played match");

                // The server's replay log reached disk with the outcome footer (proves the real core ran).
                //
                // POLLED, not read once, because the client reaching Ended does NOT imply the footer
                // is on disk: MatchSession broadcasts MatchEndMsg and only writes _replay.Complete on
                // the NEXT line. So the ordering under test is "the server tells you, THEN it records"
                // — and the two clients above are awaiting the telling. Read immediately and a loaded
                // runner loses that race: the file holds the header and every intent, but no footer.
                // Observed under the Stage 3 four-assembly batch on CI, green 5/5 locally, which is
                // exactly the starvation signature ade48e2 dealt with for the sibling fixture.
                //
                // Widening the window cannot mask a real regression: a server that never completes
                // the replay still fails, 5s later. What it removes is a scheduling assumption the
                // test never meant to make.
                string[] logs = Directory.GetFiles(replays, "*.jsonl");
                Assert.That(logs, Has.Length.EqualTo(1));

                string replayText = "";
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    replayText = File.ReadAllText(logs[0]);
                    if (replayText.Contains("\"outcome\"")) break;
                    await Task.Delay(50);
                }
                Assert.That(replayText, Does.Contain("\"outcome\""),
                    "the server must record the outcome footer after announcing the match end");
            }
            finally
            {
                c0.Dispose();
                c1.Dispose();
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

        private static Task WaitForState(NetworkedMatchSession s, ConnectionState target)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.OnConnectionStateChanged += st => { if (st == target) tcs.TrySetResult(true); };
            if (s.State == target) tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
