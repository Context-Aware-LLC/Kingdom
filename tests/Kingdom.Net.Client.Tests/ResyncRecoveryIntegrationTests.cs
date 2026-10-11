using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Tests.Support;
using Kingdom.Net.Server;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests
{
    /// <summary>
    /// The resync soft-lock repro, at the REAL transport seam (b3 §4.5 / §11 test 4a's blind spot).
    ///
    /// <para>Test 4a proves the resync path over <c>LoopbackTransport</c>, whose <c>SendAsync</c>
    /// enqueues synchronously under a lock and returns <c>Task.CompletedTask</c>. A lost outbound
    /// frame is structurally impossible there, so 4a was green throughout the 2026-07-21 soft-lock.
    /// It was never evidence about this path. This fixture reproduces the loss the only place it can
    /// happen: around the real <see cref="WebSocketTransport"/>.</para>
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class ResyncRecoveryIntegrationTests
    {
        /// <summary>
        /// One resync frame is lost on the wire. The client must notice it was never answered and
        /// re-send — not wait forever.
        ///
        /// <para>RED until <c>ResyncTimeoutMs</c> is consumed: today <c>RequestResyncUnlocked</c>
        /// sets <c>_resyncPending</c>, sends exactly once fire-and-forget, and nothing ever re-drives
        /// it. The assertion that fails first is <c>ResyncFramesDelivered</c> — a retry must actually
        /// reach the socket. Recovery alone is not enough to pass: a later unrelated snapshot would
        /// clear the pending flag on its own (the <c>&lt;=</c> drop guard is pending-exempt), which
        /// would mask the bug.</para>
        /// </summary>
        [Test]
        [CancelAfter(120_000)]
        public async Task A_Resync_Lost_On_The_Wire_Is_Retried_And_The_Client_Recovers()
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-resync-" + Guid.NewGuid().ToString("N"));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = LoopbackServerPeer.CardDataDir(),
                ReplaysDir = replays,
                // Widen the SERVER liveness window far past this test's whole duration. This fixture
                // deliberately makes the match go QUIET after the wedge (no inbound to c0, and while a
                // resync is pending the client sends no heartbeats), so the only thing keeping c0's
                // seat alive is the resync retry reaching the server. Under the Stage 3 four-assembly
                // batch the client's 1s timer loop is CPU-starved; if its first post-wedge wake lands
                // past the default 30s HeartbeatTimeoutMs, the server vacates c0 and closes the socket
                // BEFORE the retry can fire — which killed the session (→ Dead) and made the retry we
                // are testing structurally impossible. That is a starvation artifact of the test host,
                // not a product fault: in real play the 2s/5s retry always beats the 30s window. We
                // isolate the resync-retry behaviour under test from that unrelated liveness race by
                // making the window unreachable within the test; nothing about the retry/recover
                // assertions is relaxed. (b3 §11: don't let a wall-clock the test can't schedule decide
                // the outcome.)
                Options = new ServerOptions { HeartbeatTimeoutMs = 600_000 },
            });
            await app.StartAsync();

            var fault = new FaultInjectingTransport(new WebSocketTransport());
            // ResyncTimeoutMs kept tight (2s) so the retry is DUE quickly — the behaviour under test.
            // InboundSilenceMs widened to match the server: the same starved-timer-loop race can trip
            // the CLIENT's own 30s link-death check first (it is evaluated BEFORE the resync-retry
            // branch in TimerLoopAsync), declaring the link dead and skipping the retry. Both windows
            // must be unreachable within the test for the retry to be deterministic under load.
            var c0 = new NetworkedMatchSession(fault, new ClientOptions { DeviceToken = "test-device-0", ResyncTimeoutMs = 2_000, InboundSilenceMs = 600_000 });
            var c1 = new NetworkedMatchSession(new WebSocketTransport(), new ClientOptions { DeviceToken = "test-device-1", InboundSilenceMs = 600_000 });

            try
            {
                var baseUri = new Uri(app.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                await Task.WhenAll(c0.ConnectAsync(wsUri), c1.ConnectAsync(wsUri));

                Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
                Assert.That(c1.State, Is.EqualTo(ConnectionState.Live));

                // Settle into a live turn: keep both hands.
                Assert.That((await c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()))).Accepted, Is.True);
                Assert.That((await c1.SubmitAsync(new MulliganIntent(c1.Player, Array.Empty<InstanceId>()))).Accepted, Is.True);
                await WaitUntil(() => c0.Current.Phase != TurnPhase.Mulligan, TimeSpan.FromSeconds(10), "mulligan to close");

                // Arm both halves of the fault: the next inbound frame fails to decode (the §4.5
                // resync trigger), and the first resync it provokes never reaches the socket.
                fault.CorruptNextInbound(1);
                fault.DropNextResync(1);

                // Drive exactly one state change so the server sends c0 exactly one snapshot — the
                // one that gets corrupted. After this the match is quiet, which is what makes the
                // wedge permanent rather than self-healing.
                NetworkedMatchSession active = Equals(c0.Current.ActivePlayer, c0.Player) ? c0 : c1;
                _ = active.SubmitAsync(new EndTurnIntent(active.Player));

                // Wait for the wedge to FORM, event-driven: FirstResyncDropped completes the instant
                // the first resync is swallowed. The client only ever sends a resync AFTER it has set
                // the pending flag, so a swallowed resync is itself proof the client is wedged and
                // waiting on an answer nobody will send — the precondition, captured with no wall-clock
                // window for the Stage 3 batch to starve us past. (The old code polled for the drop
                // and then read ResyncPending live; under load that poll could return only after the
                // auto-delivered retry had already cleared the flag — the ~40% precondition flake.)
                await fault.FirstResyncDropped.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.That(fault.ResyncFramesDropped, Is.GreaterThanOrEqualTo(1),
                    "precondition: a resync was requested and then lost — the client is waiting on an answer nobody will send");

                // THE ASSERTION. The client must re-send the unanswered resync, and the retry must
                // actually reach the socket. Awaited event-driven, exactly as the precondition above:
                // FirstResyncDelivered completes the instant the retry passes the fault decorator to
                // the real socket, however late the starved 1s timer loop was in firing it. The 45s
                // ceiling is a failsafe, not a race — far larger than ResyncTimeoutMs (2s) and the
                // handful of seconds the retry takes even under the full Stage 3 batch, so only a
                // genuinely absent retry (a real product wedge) trips it. Robust margin, not a tight
                // real-time deadline (b3 §11).
                try
                {
                    await fault.FirstResyncDelivered.WaitAsync(TimeSpan.FromSeconds(45));
                }
                catch (TimeoutException)
                {
                    // Turn the opaque WaitAsync timeout into the state that explains it. State=Dead here
                    // means a liveness path (client link-death or a server seat-vacate) killed the
                    // session before the retry — the starvation race the widened windows above exist to
                    // prevent; anything else is a genuine resync-retry wedge worth escalating.
                    Assert.Fail($"retry never reached the socket within 45s. state={c0.State} " +
                        $"resyncPending={c0.ResyncPending} resyncCount={c0.ResyncCount} " +
                        $"dropped={fault.ResyncFramesDropped} delivered={fault.ResyncFramesDelivered}");
                }
                Assert.That(fault.ResyncFramesDelivered, Is.GreaterThanOrEqualTo(1),
                    "the client must re-send the unanswered resync and the retry must reach the socket");

                // Recovery is driven by that retry: the delivered resync is answered and the flag
                // clears. A spurious snapshot cannot mask the bug — we already proved, in order, that
                // a real retry reached the wire above. One server round-trip; 30s is generous headroom
                // under load, not a deadline the recovery races.
                await WaitUntil(() => !c0.ResyncPending, TimeSpan.FromSeconds(30),
                    "the client to recover once the retry is answered");

                Assert.That(c0.State, Is.EqualTo(ConnectionState.Live), "recovery is not a disconnect");
                Assert.That(c0.ResyncCount, Is.LessThanOrEqualTo(3), "a retry must not burn the §4.5 budget per attempt");
            }
            finally
            {
                c0.Dispose();
                c1.Dispose();
                await app.StopAsync();
                await TryDeleteDirectoryAsync(replays);
            }
        }

        /// <summary>
        /// Best-effort temp cleanup. StopAsync can return before the server's async replay writer has
        /// released its .jsonl handle; on Windows a recursive delete then races that handle and throws
        /// IOException (Linux allows the unlink, so CI never sees it). Retry briefly, then give up — a
        /// leaked temp dir is harmless and must never fail a test whose body has passed.
        /// </summary>
        private static async Task TryDeleteDirectoryAsync(string dir)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(100).ConfigureAwait(false);
            }
            Assert.Fail($"timed out after {timeout.TotalSeconds:0}s waiting for {what}");
        }
    }
}
