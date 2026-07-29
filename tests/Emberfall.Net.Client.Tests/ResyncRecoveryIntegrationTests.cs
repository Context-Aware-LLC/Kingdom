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
        [CancelAfter(90_000)]
        public async Task A_Resync_Lost_On_The_Wire_Is_Retried_And_The_Client_Recovers()
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-resync-" + Guid.NewGuid().ToString("N"));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = LoopbackServerPeer.CardDataDir(),
                ReplaysDir = replays,
            });
            await app.StartAsync();

            var fault = new FaultInjectingTransport(new WebSocketTransport());
            var c0 = new NetworkedMatchSession(fault, new ClientOptions { DeviceToken = "test-device-0", ResyncTimeoutMs = 2_000 });
            var c1 = new NetworkedMatchSession(new WebSocketTransport(), new ClientOptions { DeviceToken = "test-device-1" });

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
                // resync trigger), and the resync it provokes never reaches the socket.
                fault.CorruptNextInbound(1);
                fault.DropNextResync(1);

                // Drive exactly one state change so the server sends c0 exactly one snapshot — the
                // one that gets corrupted. After this the match is quiet, which is what makes the
                // wedge permanent rather than self-healing.
                NetworkedMatchSession active = Equals(c0.Current.ActivePlayer, c0.Player) ? c0 : c1;
                _ = active.SubmitAsync(new EndTurnIntent(active.Player));

                await WaitUntil(() => fault.ResyncFramesDropped >= 1, TimeSpan.FromSeconds(15),
                    "the corrupted frame to provoke a resync that we then drop");

                Assert.That(c0.ResyncPending, Is.True, "precondition: the client is waiting on a resync nobody will answer");

                // THE ASSERTION. The client must re-send the unanswered resync.
                await WaitUntil(() => fault.ResyncFramesDelivered >= 1, TimeSpan.FromSeconds(20),
                    "a RETRY to reach the real socket after ResyncTimeoutMs");

                await WaitUntil(() => !c0.ResyncPending, TimeSpan.FromSeconds(20),
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
