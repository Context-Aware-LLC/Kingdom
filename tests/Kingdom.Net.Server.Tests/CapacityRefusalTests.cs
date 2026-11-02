using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Tests.Support;
using Kingdom.Net.Server.Transport;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// Item 4b of the first-session plan: a pairing at <see cref="ServerOptions.MaxSessions"/> is
    /// refused with 1013 <see cref="MatchGate.CapacityRefusal"/>, which the client answers by holding
    /// the player and re-queueing — and the refusal is logged as <c>reason=capacity</c>, the token
    /// the first-session funnel's <c>capacity</c> drop reason reads.
    ///
    /// <para>The second test is the reason this fixture exists: a player who LEFT the queue used to
    /// be logged as "server full" too, because <c>PairAsync</c> returned the same null for both.
    /// Counted from the log, every walk-away would have read as the box being full.</para>
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    // Boots real Kestrel hosts and swaps Console.Error, which is process-global.
    [NonParallelizable]
    public sealed class CapacityRefusalTests
    {
        private const string TokenA = "aa14e45fce8f4b6ea1c2d3e4f5a6b7c8";
        private const string TokenB = "bb23456789abcdef0123456789abcdef";
        private const string TokenC = "cc3456789abcdef0123456789abcdef0";

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Pairing_At_The_Cap_Is_Refused_With_1013_And_Logged_As_Capacity()
        {
            using var log = new CapturedStderr();
            await WithServerAsync(maxSessions: 1, async wsUri =>
            {
                // Fill the one slot with a real match.
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, TokenA);
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, TokenB);
                await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);

                using var c2 = new ClientWebSocket();
                await c2.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c2, TokenC);
                await AwaitCloseAsync(c2);

                Assert.That((int?)c2.CloseStatus, Is.EqualTo(1013));
                Assert.That(c2.CloseStatusDescription, Is.EqualTo(MatchGate.CapacityRefusal));
            });

            string refused = AccountId.FromDeviceToken(TokenC).Value;
            Assert.That(log.Text, Does.Contain($"refused player_id={refused} reason=capacity"),
                "the funnel's `capacity` reason is read off this token");
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Player_Who_Leaves_The_Queue_Is_Not_Logged_As_Capacity()
        {
            using var log = new CapturedStderr();
            string leaver = AccountId.FromDeviceToken(TokenA).Value;
            string cancelled = $"left before seating player_id={leaver} reason=cancelled";

            await WithServerAsync(maxSessions: 64, async wsUri =>
            {
                using var c0 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, TokenA);
                // Queued alone: no opponent, no bot pool. Walk away.
                await Task.Delay(300);
                c0.Abort();

                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!log.Text.Contains(cancelled) && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
            });

            Assert.That(log.Text, Does.Contain(cancelled), "the walk-away is logged as what it is");
            Assert.That(log.Text, Does.Not.Contain($"player_id={leaver} reason=capacity"));
            Assert.That(log.Text, Does.Not.Contain($"player_id={leaver}: {MatchGate.CapacityRefusal}"));
        }

        // ── Harness ──────────────────────────────────────────────────────────────

        /// <summary>Tees Console.Error for the test's duration; the gate logs there.</summary>
        private sealed class CapturedStderr : IDisposable
        {
            private readonly TextWriter _original = Console.Error;
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly TextWriter _writer;

            public CapturedStderr()
            {
                _writer = TextWriter.Synchronized(new StringWriter(_sb));
                Console.SetError(_writer);
            }

            public string Text { get { lock (_writer) return _sb.ToString(); } }

            public void Dispose() => Console.SetError(_original);
        }

        private static async Task WithServerAsync(int maxSessions, Func<Uri, Task> body)
        {
            string replays = Path.Combine(Path.GetTempPath(), "kingdom-capacity-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Options = new ServerOptions
                {
                    HelloTimeoutMs = 30_000,
                    AllowTokenlessMatches = true,
                    MaxSessions = maxSessions,
                },
            });
            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                await body(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"));
            }
            finally
            {
                await built.App.StopAsync();
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static Task SendHelloAsync(ClientWebSocket ws, string deviceToken)
        {
            var frame = ProtocolCodec.Encode(
                new HelloMsg(deviceToken, ProtocolContract.Version, "test", "capacity-tests", "", ""),
                ProtocolCodec.PreMatch, 0);
            return ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task AwaitCloseAsync(ClientWebSocket ws)
        {
            var buffer = new byte[64 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            WebSocketReceiveResult result;
            do result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            while (result.MessageType != WebSocketMessageType.Close);
        }

        private static async Task<T> ExpectAsync<T>(ClientWebSocket ws) where T : NetMessage
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
                if (res.Ok && res.Message is T hit) return hit;
            }
        }
    }
}
