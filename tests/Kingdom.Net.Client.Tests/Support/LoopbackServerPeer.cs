using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Loopback;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Replay;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Support
{
    /// <summary>Deterministic server clock (b3 §11 — no Task.Delay).</summary>
    public sealed class FakeServerClock : ITimeSource
    {
        public long NowMs { get; private set; } = 1_000_000;
        public void Advance(long ms) => NowMs += ms;
    }

    /// <summary>Deterministic client clock.</summary>
    public sealed class FakeClientClock : IClientClock
    {
        public long NowMs { get; set; } = 1_000_000;
    }

    /// <summary>
    /// The RC-F9 answer in code: the loopback peer is B1's REAL <see cref="MatchSession"/>, driven
    /// through its REAL <see cref="MatchSession.Dispatch"/> path — the exact entry Kestrel's receive
    /// loop funnels into (decode happens inside Dispatch). Nothing here retypes a receive loop; the
    /// only thing not exercised is WebSocket framing/socket plumbing, covered by commit 2's live
    /// droplet run and commit 3's in-Unity ClientWebSocket verify (b3 §8, §11.1).
    ///
    /// Each server→client frame is enqueued onto the matching <see cref="LoopbackTransport"/>; the
    /// client's pump handles it and bumps its inbound high-water, which lets the tests await
    /// catch-up deterministically instead of sleeping.
    /// </summary>
    public sealed class LoopbackServerPeer : IDisposable
    {
        private sealed class SeatConnection : IServerConnection
        {
            private readonly LoopbackTransport _transport;
            private readonly LoopbackServerPeer _peer;
            private readonly int _seat;
            public int? CloseCode { get; private set; }
            public Func<byte[], byte[]>? Intercept;   // tamper server→client frames in transit (RC-F8 tests)

            public SeatConnection(LoopbackServerPeer peer, int seat, LoopbackTransport transport)
            {
                _peer = peer;
                _seat = seat;
                _transport = transport;
            }

            public bool TryEnqueue(byte[] frame)
            {
                Interlocked.Increment(ref _peer._sentTo[_seat]);
                _transport.EnqueueInbound(Intercept is null ? frame : Intercept(frame));
                return true;
            }

            /// <summary>Carries the reason text through, as a real socket's close frame does — a
            /// reconnecting client reads "reconnect refused" off it.</summary>
            public void Close(int closeCode, string reason)
            {
                CloseCode ??= closeCode;
                _transport.ServerClose(WireCloseReason.PeerClosed, reason);
            }
        }

        /// <summary>
        /// What a reconnecting client is handed by <see cref="ReconnectWireFor"/>: a fresh loopback
        /// wire that relays each frame the moment it is sent, standing in for the gate's
        /// <c>HandleReconnectAsync</c>. The first frame is decoded the way the gate decodes it and
        /// posted as <see cref="SessionCommand.Reconnect"/>; every later one is an ordinary frame on
        /// the connection the session bound, or dropped if it bound none (a refused token).
        /// </summary>
        private sealed class ReconnectingWire : IWireTransport
        {
            private readonly LoopbackServerPeer _peer;
            private readonly LoopbackTransport _inner = new LoopbackTransport();
            private readonly SeatConnection _conn;
            private bool _sentFirst;

            public ReconnectingWire(LoopbackServerPeer peer, int seat)
            {
                _peer = peer;
                _conn = new SeatConnection(peer, seat, _inner);
            }

            public event Action<WireCloseReason, string?>? OnClosed
            {
                add => _inner.OnClosed += value;
                remove => _inner.OnClosed -= value;
            }

            public Task ConnectAsync(Uri uri, CancellationToken ct)
            {
                _peer.ReconnectUris.Enqueue(uri);
                if (_peer.NetworkDown) throw new System.IO.IOException("network unreachable (test)");
                return Task.CompletedTask;
            }

            public async Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
            {
                await _inner.SendAsync(utf8, ct).ConfigureAwait(false);
                while (_inner.TryDequeueOutbound(out var bytes))
                {
                    lock (_peer._dispatchGate)
                    {
                        if (!_sentFirst)
                        {
                            _sentFirst = true;
                            var decoded = ProtocolCodec.Decode(bytes);
                            if (decoded.Ok && decoded.Message is ReconnectMsg msg)
                                _peer.Session.Dispatch(new SessionCommand.Reconnect(msg, _conn));
                            else
                                _conn.Close(1008, "expected reconnect");
                            continue;
                        }
                        _peer.Session.Dispatch(new SessionCommand.Frame(_conn, bytes));
                    }
                }
            }

            public Task<byte[]?> ReceiveAsync(CancellationToken ct) => _inner.ReceiveAsync(ct);

            public Task CloseAsync(WireCloseReason reason) => _inner.CloseAsync(reason);
        }

        private readonly object _dispatchGate = new object();
        private readonly int[] _sentTo = new int[2];

        /// <summary>While true, every reconnect wire's connect throws, as a phone with no network
        /// does. Flip it back to let the next attempt through.</summary>
        public volatile bool NetworkDown;

        /// <summary>Every uri a reconnect wire was asked to connect to, in order.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<Uri> ReconnectUris { get; } =
            new System.Collections.Concurrent.ConcurrentQueue<Uri>();

        /// <summary>The <see cref="ClientOptions.ReconnectTransport"/> for the client in
        /// <paramref name="seat"/>: each call is one fresh reconnect wire into this peer's session.</summary>
        public Func<IWireTransport> ReconnectWireFor(int seat) => () => new ReconnectingWire(this, seat);

        public FakeServerClock Time { get; } = new FakeServerClock();
        public InMemoryReplayStore Replay { get; } = new InMemoryReplayStore();
        public MatchId Id { get; } = new MatchId("m-loop");
        public PlayerId P0 { get; } = new PlayerId("p0");
        public PlayerId P1 { get; } = new PlayerId("p1");
        public MatchSession Session { get; }

        public LoopbackTransport Transport0 { get; } = new LoopbackTransport();
        public LoopbackTransport Transport1 { get; } = new LoopbackTransport();
        private readonly SeatConnection _conn0;
        private readonly SeatConnection _conn1;
        private NetworkedMatchSession? _client0;
        private NetworkedMatchSession? _client1;

        private static FileCardDatabase? _db;
        public static FileCardDatabase Db => _db ??= FileCardDatabase.LoadDirectory(CardDataDir());

        /// <param name="king">The King the match plays for, or null for a friend match (#421).</param>
        /// <param name="cardBacks">Each seat's card back as the gate would have settled it, or null
        /// for the default on both (#421).</param>
        public LoopbackServerPeer(ServerOptions? options = null, KingSeating? king = null,
            (int P0, int P1)? cardBacks = null)
        {
            // These tests jump the clock a whole mulligan/turn in ONE tick to drive a server clock.
            // The decision-24 stall guard is built to read exactly that shape as "the server was
            // asleep" and credit the time back, so it must be disabled here for the same reason the
            // heartbeat timeout is — the elapsed time is the point of the test, not a suspend.
            var opts = options ?? new ServerOptions
            {
                HeartbeatTimeoutMs = long.MaxValue / 4,
                StallGuardMs = long.MaxValue / 4,
            };
            _conn0 = new SeatConnection(this, 0, Transport0);
            _conn1 = new SeatConnection(this, 1, Transport1);
            Session = new MatchSession(Id, 0xE17BEEF5EEDUL, opts, Time, Db, PrebuiltDecks.EmberwardVsTidecaller(), Replay, P0, P1,
                king: king,
                p0CardBack: cardBacks?.P0 ?? CardBackDefinitions.DefaultId,
                p1CardBack: cardBacks?.P1 ?? CardBackDefinitions.DefaultId);
        }

        public void BindClients(NetworkedMatchSession c0, NetworkedMatchSession c1)
        {
            _client0 = c0;
            _client1 = c1;
        }

        /// <summary>Server-side tamper hook for the seat-0 → client stream (RC-F8 4a).</summary>
        public void InterceptToClient0(Func<byte[], byte[]> f) => _conn0.Intercept = f;
        public void ClearInterceptToClient0() => _conn0.Intercept = null;

        /// <summary>BeginMatch + send both match_starts (synchronous; frames land on the transports).</summary>
        public void Start() => Session.Start(_conn0, _conn1);

        /// <summary>Drain both clients' outbound frames into the real Dispatch, then await both
        /// clients catching up to everything the server enqueued <b>during this op</b>. The wait is
        /// a per-op DELTA (server frames enqueued now, added to the client's processed count now) so
        /// it stays correct even after a test injects frames directly (RC-F8 4a–4c).</summary>
        public Task RelayAsync() => WithDeltaAsync(() =>
        {
            DrainOutbound(Transport0, _conn0);
            DrainOutbound(Transport1, _conn1);
        });

        /// <summary>Advance the server clock and pulse it (auto-confirm / turn / disconnect timers),
        /// then await catch-up. The RC-F7 clock-race lever.</summary>
        public Task AdvanceAndTickAsync(long ms) => WithDeltaAsync(() =>
        {
            Time.Advance(ms);
            Session.Dispatch(new SessionCommand.ClockTick());
        });

        public Task TickAsync() => WithDeltaAsync(() => Session.Dispatch(new SessionCommand.ClockTick()));

        /// <summary>The frames the server has enqueued to <paramref name="seat"/>, on whichever wire
        /// it held at the time.</summary>
        public int SentTo(int seat) => Volatile.Read(ref _sentTo[seat]);

        /// <summary>Counts per SEAT, not per connection, so the wait stays right across a
        /// reconnect. It assumes a seat's client is reading the wire the frames went to; a seat
        /// that is away receives nothing to wait for, which is why a test holding one away must
        /// not use these for that seat.</summary>
        private async Task WithDeltaAsync(System.Action serverAction)
        {
            int before0 = _client0?.InboundProcessed ?? 0, count0 = SentTo(0);
            int before1 = _client1?.InboundProcessed ?? 0, count1 = SentTo(1);
            lock (_dispatchGate) serverAction();
            var tasks = new List<Task>(2);
            if (_client0 is not null) tasks.Add(_client0.WaitInboundAsync(before0 + (SentTo(0) - count0)));
            if (_client1 is not null) tasks.Add(_client1.WaitInboundAsync(before1 + (SentTo(1) - count1)));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// This peer stands in for BOTH the gate and the session, so it swallows the connect
        /// <c>HelloMsg</c> exactly as <c>MatchGate</c> does (C-G1-8). Forwarding it would hand the
        /// session a message it never sees in production — the gate consumes the hello before a
        /// session exists — and the firewall would (correctly) strike the connection for it.
        /// </summary>
        private void DrainOutbound(LoopbackTransport t, SeatConnection origin)
        {
            while (t.TryDequeueOutbound(out var bytes))
            {
                var decoded = ProtocolCodec.Decode(bytes);
                if (decoded.Ok && decoded.Message is HelloMsg hello)
                {
                    DeviceTokens[origin == _conn0 ? 0 : 1] = hello.DeviceToken;
                    continue;
                }
                lock (_dispatchGate) Session.Dispatch(new SessionCommand.Frame(origin, bytes));
            }
        }

        /// <summary>The network under seat <paramref name="seat"/>'s ORIGINAL wire goes: the client
        /// sees its socket close with no reason, and the session hears the disconnect, as the gate's
        /// receive loop posts it when a real socket dies.</summary>
        public void DropOriginalWire(int seat)
        {
            SeatConnection conn = seat == 0 ? _conn0 : _conn1;
            (seat == 0 ? Transport0 : Transport1).ServerClose();
            lock (_dispatchGate) Session.Dispatch(new SessionCommand.Disconnected(conn));
        }

        /// <summary>Device tokens seen at the door, seat-indexed — the gate's attribution, mirrored
        /// so a client-seam test can assert the hello actually left the client.</summary>
        public string[] DeviceTokens { get; } = { "", "" };

        public static string CardDataDir()
        {
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "unity", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Could not locate unity/Assets/CardData.");
        }

        public void Dispose()
        {
            _client0?.Dispose();
            _client1?.Dispose();
        }
    }
}
