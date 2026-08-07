using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Matchmaking;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    [TestFixture]
    [Category("Contract")]
    [NonParallelizable]
    public sealed class MatchmakingGateTests
    {
        private const string TokenA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string TokenB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Test]
        [CancelAfter(60_000)]
        public async Task Gate_Uses_Injected_Queue_Ratings_And_Wake_Contract()
        {
            string replays = Path.Combine(
                Path.GetTempPath(), "emberfall-matchmaking-" + Guid.NewGuid().ToString("N"));
            var queue = new ControlledQueue();
            AccountId accountA = AccountId.FromDeviceToken(TokenA);
            AccountId accountB = AccountId.FromDeviceToken(TokenB);
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                MatchmakingQueue = queue,
                MatchmakingRatingForAccount = account => account == accountA ? 900 :
                    account == accountB ? 1_300 :
                    throw new InvalidOperationException("Unexpected account reached the rating store."),
            });

            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                using var first = new ClientWebSocket();
                using var second = new ClientWebSocket();
                await first.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(first, TokenA);
                await second.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(second, TokenB);

                await WaitUntilAsync(() => queue.Count == 2, TimeSpan.FromSeconds(10));
                Assert.That(queue.Ratings, Is.EquivalentTo(new[] { 900, 1_300 }));
                Assert.That(built.Gate.LiveSessions, Is.Zero,
                    "the injected queue must control when a match is released");

                queue.ReleasePair();
                await ExpectAsync<MatchStartMsg>(first);
                await ExpectAsync<MatchStartMsg>(second);
                Assert.That(built.Gate.LiveSessions, Is.EqualTo(1));
            }
            finally
            {
                await built.App.StopAsync();
                await DeleteDirectoryAsync(replays);
            }
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task Built_In_Queue_Seats_Incompatible_Sockets_After_The_Band_Widens()
        {
            string replays = Path.Combine(
                Path.GetTempPath(), "emberfall-band-widening-" + Guid.NewGuid().ToString("N"));
            AccountId accountA = AccountId.FromDeviceToken(TokenA);
            AccountId accountB = AccountId.FromDeviceToken(TokenB);
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Options = new ServerOptions
                {
                    Matchmaking = new MatchmakingQueueOptions
                    {
                        InitialRatingBand = 0,
                        RatingBandStep = 400,
                        BandWideningIntervalMs = 250,
                    },
                },
                MatchmakingRatingForAccount = account => account == accountA ? 900 :
                    account == accountB ? 1_300 :
                    throw new InvalidOperationException("Unexpected account reached the rating store."),
            });

            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                using var first = new ClientWebSocket();
                using var second = new ClientWebSocket();
                await first.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(first, TokenA);
                await second.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(second, TokenB);

                Assert.That(built.Gate.LiveSessions, Is.Zero,
                    "ratings outside the initial band must not pair immediately");
                await ExpectAsync<MatchStartMsg>(first);
                await ExpectAsync<MatchStartMsg>(second);
                Assert.That(built.Gate.LiveSessions, Is.EqualTo(1),
                    "the real queue wake-up must reevaluate and seat the pair after widening");
            }
            finally
            {
                await built.App.StopAsync();
                await DeleteDirectoryAsync(replays);
            }
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task Gate_Rejects_A_Mismatched_Client_Contract_Before_Queueing()
        {
            string replays = Path.Combine(
                Path.GetTempPath(), "emberfall-contract-gate-" + Guid.NewGuid().ToString("N"));
            var queue = new ControlledQueue();
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                MatchmakingQueue = queue,
            });

            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(socket, TokenA, IncompatibleContractVersion);

                var buffer = new byte[256];
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cts.Token);

                Assert.Multiple(() =>
                {
                    Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                    Assert.That(queue.Count, Is.Zero, "An incompatible client must never enter matchmaking.");
                    Assert.That(built.Gate.LiveSessions, Is.Zero);
                });
            }
            finally
            {
                await built.App.StopAsync();
                await DeleteDirectoryAsync(replays);
            }
        }

        /// <summary>A version the gate must refuse, derived from the live one so it stays a mismatch
        /// after a contract bump. A literal here silently stops testing anything the day
        /// <see cref="ProtocolContract.Version"/> moves onto it.</summary>
        private const string IncompatibleContractVersion = ProtocolContract.Version + ".not-this-one";

        private static Task SendHelloAsync(
            ClientWebSocket ws,
            string deviceToken,
            string contractVersion = ProtocolContract.Version)
        {
            byte[] frame = ProtocolCodec.Encode(
                new HelloMsg(deviceToken, contractVersion, "test", "matchmaking-gate-tests"),
                ProtocolCodec.PreMatch,
                0);
            return ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);
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
                    result = await ws.ReceiveAsync(
                        new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException(
                            $"Socket closed while waiting for {typeof(T).Name}.");
                    total += result.Count;
                } while (!result.EndOfMessage);

                var decoded = ProtocolCodec.Decode(
                    new ReadOnlySpan<byte>(buffer, 0, total).ToArray());
                if (!decoded.Ok)
                    throw new InvalidOperationException(
                        $"Server sent undecodable frame: {decoded.Error} {decoded.Detail}");
                if (decoded.Message is T message) return message;
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (!condition()) await Task.Delay(10, cts.Token);
        }

        private static async Task DeleteDirectoryAsync(string path)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 9)
                {
                    await Task.Delay(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 9)
                {
                    await Task.Delay(50);
                }
            }
        }

        private sealed class ControlledQueue : IMatchmakingQueue
        {
            private readonly object _lock = new object();
            private readonly List<MatchmakingTicket> _waiting = new List<MatchmakingTicket>();
            private readonly TaskCompletionSource _wake =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _released;

            public int Count
            {
                get { lock (_lock) return _waiting.Count; }
            }

            public int[] Ratings
            {
                get { lock (_lock) return _waiting.Select(ticket => ticket.Rating).ToArray(); }
            }

            public void Enqueue(MatchmakingTicket ticket)
            {
                lock (_lock) _waiting.Add(ticket);
            }

            public bool Remove(string ticketId)
            {
                lock (_lock)
                {
                    int index = _waiting.FindIndex(ticket => ticket.Id == ticketId);
                    if (index < 0) return false;
                    _waiting.RemoveAt(index);
                    return true;
                }
            }

            public MatchmakingPair? TryDequeueMatch()
            {
                lock (_lock)
                {
                    if (!_released || _waiting.Count < 2) return null;
                    MatchmakingTicket first = _waiting[0];
                    MatchmakingTicket second = _waiting[1];
                    _waiting.RemoveRange(0, 2);
                    return new MatchmakingPair(first, second);
                }
            }

            public Task WaitForMatchOpportunityAsync(CancellationToken ct) =>
                _wake.Task.WaitAsync(ct);

            public void ReleasePair()
            {
                lock (_lock) _released = true;
                _wake.TrySetResult();
            }
        }
    }
}
