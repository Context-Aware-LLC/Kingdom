using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>
    /// C-G1-8 step 1 — guest identity at the connect door (telemetry-spec §0, accounts posture
    /// decision 6a).
    ///
    /// Before this, `MatchGate` minted `guest-&lt;Interlocked.Increment&gt;` from a per-process
    /// counter: identity reset on every server restart, which on a scale-to-zero machine is
    /// near-constant. Two matches by the same tester an hour apart were different players. Any
    /// telemetry emitted against that is unattributable and — unlike the account registry, which
    /// can be rebuilt from emitted tokens later — unrecoverable.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    // Boots real Kestrel hosts on ephemeral ports, same as LoopbackIntegrationTests — which is
    // NonParallelizable for this reason. Run alongside the other socket fixtures these tests failed
    // roughly 1 run in 6 and never once in isolation (8/8 clean).
    [NonParallelizable]
    public sealed class GuestIdentityTests
    {
        private const string TokenA = "8f14e45fce8f4b6ea1c2d3e4f5a6b7c8";
        private const string TokenB = "0123456789abcdef0123456789abcdef";

        // ── Derivation ───────────────────────────────────────────────────────────

        [Test]
        public void Same_Device_Token_Yields_The_Same_Account_Across_Processes()
        {
            // Pure function of the token — no counter, no seed, no process state. This is the
            // whole property `guest-<counter>` lacked.
            var firstDerivation = AccountId.FromDeviceToken(TokenA);
            var secondDerivation = AccountId.FromDeviceToken(TokenA);
            Assert.That(secondDerivation, Is.EqualTo(firstDerivation));
        }

        [Test]
        public void Different_Device_Tokens_Yield_Different_Accounts()
        {
            Assert.That(AccountId.FromDeviceToken(TokenA), Is.Not.EqualTo(AccountId.FromDeviceToken(TokenB)));
        }

        [Test]
        public void The_Account_Id_Is_Not_The_Raw_Device_Token()
        {
            // The token is a bearer identity. Hashing it means a leaked telemetry dump cannot be
            // replayed as somebody's identity, and our own store never holds the raw value.
            var acct = AccountId.FromDeviceToken(TokenA);
            Assert.That(acct.Value, Is.Not.EqualTo(TokenA));
            Assert.That(acct.Value, Does.Not.Contain(TokenA));
            Assert.That(acct.Value, Does.Match("^[0-9a-f]+$"), "account ids are lowercase hex");
        }

        [Test]
        public void An_Unattributed_Account_Is_Distinguishable()
        {
            // Test harnesses and pre-identity paths say so explicitly rather than silently
            // producing a real-looking id. The telemetry emitter keys off this.
            Assert.That(AccountId.Unattributed.IsAttributed, Is.False);
            Assert.That(AccountId.FromDeviceToken(TokenA).IsAttributed, Is.True);
        }

        [Test]
        public void An_Empty_Device_Token_Cannot_Become_An_Account()
        {
            Assert.Throws<ArgumentException>(() => AccountId.FromDeviceToken(""));
        }

        // ── The connect door, over real sockets ──────────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task Hello_Is_Required_Before_A_Fresh_Socket_Is_Seated()
        {
            // The breaking half of this change, asserted deliberately: a client that connects and
            // says nothing is refused, not parked in the pairing queue. Any build that predates
            // HelloMsg must be redistributed before this deploys.
            await WithServerAsync(async wsUri =>
            {
                using var mute = new ClientWebSocket();
                await mute.ConnectAsync(wsUri, CancellationToken.None);

                using var partner = new ClientWebSocket();
                await partner.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(partner, TokenB);

                // If the mute socket had been queued, it would have paired with `partner` here.
                // Instead `partner` waits alone, and the mute socket is closed by the server.
                var closed = await WaitForCloseAsync(mute, TimeSpan.FromSeconds(10));
                Assert.That(closed, Is.True, "a socket that never said hello must not be seated");
            },
            // The one test that wants a short deadline — it is waiting for the refusal.
            helloTimeoutMs: 500);
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task Two_Hellos_Pair_And_The_Session_Records_Both_Accounts()
        {
            await WithServerAsync(async (wsUri, gate) =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, TokenA);
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, TokenB);

                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);

                var accounts = gate.AccountsOf(new MatchId(start0.Snapshot.State.Id.Value));
                Assert.That(accounts, Is.Not.Null, "the gate must retain per-match attribution");
                Assert.That(new[] { accounts!.Value.p0, accounts.Value.p1 }, Is.EquivalentTo(
                    new[] { AccountId.FromDeviceToken(TokenA), AccountId.FromDeviceToken(TokenB) }));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Players_Device_Token_Is_Never_Sent_To_The_Opponent()
        {
            // ADR-0001's plane line applied to identity. PlayerId IS broadcast (OpponentInfo), so
            // identity must never live there — this is why the token is not the PlayerId.
            await WithServerAsync(async wsUri =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, TokenA);
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, TokenB);

                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                var start1 = await ExpectAsync<MatchStartMsg>(c1);

                foreach (var start in new[] { start0, start1 })
                {
                    Assert.That(start.Opponent.PlayerId.Value, Does.Not.Contain(TokenA));
                    Assert.That(start.Opponent.PlayerId.Value, Does.Not.Contain(TokenB));
                    Assert.That(start.Opponent.DisplayName, Does.Not.Contain(TokenA));
                    Assert.That(start.Opponent.DisplayName, Does.Not.Contain(TokenB));
                    Assert.That(start.PlayerId.Value, Does.Not.Contain(TokenA));
                    Assert.That(start.PlayerId.Value, Does.Not.Contain(TokenB));
                }
            });
        }

        // ── Harness ──────────────────────────────────────────────────────────────

        private static Task WithServerAsync(Func<Uri, Task> body, long helloTimeoutMs = DefaultHelloTimeoutMs)
            => WithServerAsync((uri, _) => body(uri), helloTimeoutMs);

        /// <summary>
        /// Generous by default. Only the silent-socket test wants a short deadline, and applying its
        /// 500 ms to the whole fixture made the PAIRING tests flaky (~1 run in 6, never in isolation):
        /// under load a real socket's hello can legitimately arrive later than that, and the server
        /// then correctly closed a client the test still expected to seat.
        /// </summary>
        private const long DefaultHelloTimeoutMs = 30_000;

        private static async Task WithServerAsync(
            Func<Uri, Emberfall.Net.Server.Transport.MatchGate, Task> body,
            long helloTimeoutMs = DefaultHelloTimeoutMs)
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-identity-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Options = new ServerOptions { HelloTimeoutMs = helloTimeoutMs },
            });
            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                await body(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"), built.Gate);
            }
            finally
            {
                await built.App.StopAsync();
                await TryDeleteDirectoryAsync(replays);
            }
        }

        /// <summary>
        /// Best-effort temp cleanup. StopAsync can return before the server's async replay/account
        /// writer has released its .jsonl handle; on Windows a recursive delete then races that handle
        /// and throws IOException (Linux allows the unlink, so CI never sees it). Retry briefly, then
        /// give up — a leaked temp dir is harmless and must never fail a test whose body has passed.
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

        private static Task SendHelloAsync(ClientWebSocket ws, string deviceToken)
        {
            var frame = ProtocolCodec.Encode(
                new HelloMsg(deviceToken, "interfaces.v4", "test", "identity-tests"),
                ProtocolCodec.PreMatch, 0);
            return ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task<bool> WaitForCloseAsync(ClientWebSocket ws, TimeSpan timeout)
        {
            var buffer = new byte[4096];
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(buffer, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return true;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (WebSocketException) { return true; }
        }

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
                if (!res.Ok) throw new InvalidOperationException($"Server sent undecodable frame: {res.Error} {res.Detail}");
                if (res.Message is T t) return t;
            }
        }
    }
}
