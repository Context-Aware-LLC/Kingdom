using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Loopback;
using Emberfall.Net.Server;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Replay;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests.Support
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
            public int Count { get; private set; }
            public int? CloseCode { get; private set; }
            public Func<byte[], byte[]>? Intercept;   // tamper server→client frames in transit (RC-F8 tests)

            public SeatConnection(LoopbackTransport transport) => _transport = transport;

            public bool TryEnqueue(byte[] frame)
            {
                Count++;
                _transport.EnqueueInbound(Intercept is null ? frame : Intercept(frame));
                return true;
            }

            public void Close(int closeCode, string reason)
            {
                CloseCode ??= closeCode;
                _transport.ServerClose();
            }
        }

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

        public LoopbackServerPeer(ServerOptions? options = null)
        {
            var opts = options ?? new ServerOptions { HeartbeatTimeoutMs = long.MaxValue / 4 };
            _conn0 = new SeatConnection(Transport0);
            _conn1 = new SeatConnection(Transport1);
            Session = new MatchSession(Id, 0xE17BEEF5EEDUL, opts, Time, Db, PrebuiltDecks.EmberwardVsTidecaller(), Replay, P0, P1);
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

        private async Task WithDeltaAsync(System.Action serverAction)
        {
            int before0 = _client0?.InboundProcessed ?? 0, count0 = _conn0.Count;
            int before1 = _client1?.InboundProcessed ?? 0, count1 = _conn1.Count;
            serverAction();
            var tasks = new List<Task>(2);
            if (_client0 is not null) tasks.Add(_client0.WaitInboundAsync(before0 + (_conn0.Count - count0)));
            if (_client1 is not null) tasks.Add(_client1.WaitInboundAsync(before1 + (_conn1.Count - count1)));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private void DrainOutbound(LoopbackTransport t, SeatConnection origin)
        {
            while (t.TryDequeueOutbound(out var bytes))
                Session.Dispatch(new SessionCommand.Frame(origin, bytes));
        }

        public static string CardDataDir()
        {
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "unity", "Emberfall", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Could not locate unity/Emberfall/Assets/CardData.");
        }

        public void Dispose()
        {
            _client0?.Dispose();
            _client1?.Dispose();
        }
    }
}
