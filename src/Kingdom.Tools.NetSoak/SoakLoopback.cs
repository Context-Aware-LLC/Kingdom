using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Loopback;
using Kingdom.Net.Server;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// The offline soak peer: B1's REAL <see cref="MatchSession"/> driven through its REAL
    /// <see cref="MatchSession.Dispatch"/> path (RC-F9 — no retyped receive loop), with the client
    /// end on <see cref="LoopbackTransport"/>. Deliberately a near-twin of the test tree's
    /// LoopbackServerPeer; the shared-harness extraction is a Phase-3 cleanup that lands with the
    /// F6 AI port (until then the soak drives a scripted sequence, not the real AI).
    /// </summary>
    public sealed class SoakLoopback : IDisposable
    {
        /// <summary>Where the synthetic clock stands for the whole match: it never advances, so
        /// every telemetry row a soak writes carries this stamp.</summary>
        public const long HarnessEpochMs = 1_000_000;

        /// <summary>Both ends' clock. The clients need it as much as the server: on the wall clock a
        /// client hears nothing while a seat thinks, because this peer relays only on a submit, and
        /// one decision longer than <see cref="ClientOptions.InboundSilenceMs"/> ruled the link dead
        /// and refused the seat's own next intent as a reject (285 of a 2800-game capture,
        /// 2026-10-21, in bursts whenever decisions slowed).</summary>
        private sealed class Clock : ITimeSource, IClientClock
        {
            public long NowMs => HarnessEpochMs;
        }

        private sealed class SeatConn : IServerConnection
        {
            private readonly LoopbackTransport _t;
            public int Count { get; private set; }
            public SeatConn(LoopbackTransport t) => _t = t;
            public bool TryEnqueue(byte[] frame) { Count++; _t.EnqueueInbound(frame); return true; }
            public void Close(int closeCode, string reason) => _t.ServerClose(WireCloseReason.PeerClosed, reason);
        }

        /// <summary>Keeps the existing in-memory verification view while optionally mirroring the
        /// exact same replay calls to a durable adapter. IReplayStore itself stays unchanged.</summary>
        private sealed class MirroredReplayStore : IReplayStore
        {
            private readonly IReplayStore _first;
            private readonly IReplayStore _second;

            public MirroredReplayStore(IReplayStore first, IReplayStore second)
            {
                _first = first;
                _second = second;
            }

            public void Begin(MatchId id, ulong seed, PlayerId p0, PlayerId p1)
            {
                _first.Begin(id, seed, p0, p1);
                _second.Begin(id, seed, p0, p1);
            }

            public void Append(MatchId id, PlayerIntent intent)
            {
                _first.Append(id, intent);
                _second.Append(id, intent);
            }

            public void Complete(MatchId id, MatchOutcome outcome)
            {
                _first.Complete(id, outcome);
                _second.Complete(id, outcome);
            }

            public void Abandon(MatchId id)
            {
                _first.Abandon(id);
                _second.Abandon(id);
            }
        }

        private readonly Clock _clock = new Clock();
        private readonly SeatConn _conn0, _conn1;
        private readonly LoopbackTransport _transport0 = new LoopbackTransport();
        private readonly LoopbackTransport _transport1 = new LoopbackTransport();
        public InMemoryReplayStore Replay { get; } = new InMemoryReplayStore();
        public MatchId Id { get; }
        public PlayerId P0 { get; } = new PlayerId("p0");
        public PlayerId P1 { get; } = new PlayerId("p1");
        public MatchSession Session { get; }

        private NetworkedMatchSession? _c0, _c1;

        /// <param name="setup">Decks and classes; null keeps the slice default (p0 EMBERWARD).</param>
        /// <param name="telemetry">
        /// Where `match_complete` goes. Null leaves the session on <c>NullTelemetrySink</c> — correct
        /// for the transport soak, which measures integrity and would otherwise pollute the balance
        /// sample with turn-1 concedes.
        /// </param>
        /// <param name="attribution">
        /// Required alongside <paramref name="telemetry"/>: <c>MatchSession.Emit</c> DROPS every
        /// unattributed match on purpose, so a sink passed without this writes nothing and looks
        /// exactly like a broken sink.
        /// </param>
        public SoakLoopback(
            ICardDatabase db, ulong seed, string matchId,
            MatchSetup? setup = null,
            ITelemetrySink? telemetry = null,
            MatchAttribution? attribution = null,
            IReplayStore? durableReplay = null)
        {
            Id = new MatchId(matchId);
            _conn0 = new SeatConn(_transport0);
            _conn1 = new SeatConn(_transport1);
            // The loopback clock never advances, so the policy gate's token bucket never refills.
            // A scripted two-intent match never notices; a real AI submits several intents per turn
            // across dozens of turns and would starve on rejects after the tenth. Zero-cost intents
            // remove a harness artifact, not a server behaviour — rate limiting is exercised by its
            // own tests, and leaving it armed here would measure the bucket, not the balance.
            var opts = new ServerOptions
            {
                HeartbeatTimeoutMs = long.MaxValue / 4,
                InTurnIntentCost = 0,
                OutOfTurnIntentCost = 0,
            };
            IReplayStore replay = durableReplay is null
                ? Replay
                : new MirroredReplayStore(Replay, durableReplay);
            Session = new MatchSession(
                Id, seed, opts, _clock, db,
                setup ?? PrebuiltDecks.EmberwardVsTidecaller(),
                replay, P0, P1, telemetry, attribution);
        }

        /// <summary>A client for <paramref name="seat"/> on this peer's transport and clock. The
        /// transports are private so that no client can be built on the wall clock.</summary>
        public NetworkedMatchSession Client(int seat, ClientOptions options) => seat switch
        {
            0 => new NetworkedMatchSession(_transport0, options with { Clock = _clock }),
            1 => new NetworkedMatchSession(_transport1, options with { Clock = _clock }),
            _ => throw new ArgumentOutOfRangeException(nameof(seat), seat, "A match has seats 0 and 1."),
        };

        public void Bind(NetworkedMatchSession c0, NetworkedMatchSession c1) { _c0 = c0; _c1 = c1; }
        public void Start() => Session.Start(_conn0, _conn1);

        public Task RelayAsync() => WithDeltaAsync(() =>
        {
            while (_transport0.TryDequeueOutbound(out var b)) Session.Dispatch(new SessionCommand.Frame(_conn0, b));
            while (_transport1.TryDequeueOutbound(out var b)) Session.Dispatch(new SessionCommand.Frame(_conn1, b));
        });

        private async Task WithDeltaAsync(System.Action serverAction)
        {
            int before0 = _c0?.InboundProcessed ?? 0, count0 = _conn0.Count;
            int before1 = _c1?.InboundProcessed ?? 0, count1 = _conn1.Count;
            serverAction();
            var tasks = new List<Task>(2);
            if (_c0 is not null) tasks.Add(_c0.WaitInboundAsync(before0 + (_conn0.Count - count0)));
            if (_c1 is not null) tasks.Add(_c1.WaitInboundAsync(before1 + (_conn1.Count - count1)));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _c0?.Dispose();
            _c1?.Dispose();
        }
    }
}
