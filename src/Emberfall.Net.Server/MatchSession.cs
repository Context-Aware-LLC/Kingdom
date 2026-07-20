using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Replay;

namespace Emberfall.Net.Server
{
    /// <summary>
    /// One live match (b1-match-server-design.md §3/§4): the online sibling of LocalMatch's
    /// Coordinator. Composes <see cref="LocalMatchHost"/> — zero rules surface here — and adds
    /// the policy gate, the clocks, the replay log, and the wire.
    ///
    /// THREADING CONTRACT: <see cref="Dispatch"/> is synchronous and single-caller — the commit-2
    /// transport wraps it in a single-reader Channel (design §4); tests call it directly. Everything
    /// here, including the <c>CommandProcessor.LastEventVisibility</c> sidecar capture (which the
    /// next Execute clobbers — a4 design §11 finding 1), relies on that serialization.
    /// Outbound is enqueue-only (<see cref="IServerConnection.TryEnqueue"/>): a slow socket drops
    /// to the Disconnected path, never blocks the match (RC F5).
    /// </summary>
    public sealed class MatchSession
    {
        /// <summary>Why a submit happened — maps to the wire's MatchEndReason when it ends the match.</summary>
        private enum SubmitCause { Client, Clock, IdleForfeit, DisconnectTimeout }

        private sealed class Seat
        {
            public Seat(PlayerId player, string token)
            {
                Player = player;
                ReconnectToken = token;
            }

            public PlayerId Player { get; }
            public string ReconnectToken { get; }
            public IServerConnection? Conn;
            public ConnectionPolicyState? Link;
            /// <summary>Armed while the seat is vacant (design §8); expiry concedes the seat.</summary>
            public long? DisconnectDeadlineMs;
            /// <summary>Consecutive clock-fired auto-end-turns; resets on any voluntary accepted intent (§7, RC F11).</summary>
            public int ConsecutiveAutoTurns;
        }

        private readonly MatchId _id;
        private readonly ulong _seed;
        private readonly ServerOptions _opts;
        private readonly ITimeSource _time;
        private readonly IReplayStore _replay;
        private readonly CommandProcessor _processor;
        private readonly LocalMatchHost _host;
        private readonly Seat[] _seats = new Seat[2];

        private long? _mulliganDeadlineMs;
        private long? _turnDeadlineMs;
        private bool _ropeSent;
        private (TurnPhase, PlayerId, int) _turnKey;

        public MatchSession(
            MatchId id, ulong seed, ServerOptions opts, ITimeSource time,
            ICardDatabase db, MatchSetup setup, IReplayStore replay,
            PlayerId p0, PlayerId p1)
        {
            _id = id;
            _seed = seed;
            _opts = opts;
            _time = time;
            _replay = replay;
            _processor = new CommandProcessor(db);
            _host = new LocalMatchHost(_processor, db, setup);
            _seats[0] = new Seat(p0, MintToken());
            _seats[1] = new Seat(p1, MintToken());
        }

        public MatchId Id => _id;
        public bool Ended { get; private set; }
        /// <summary>Authority StateVersion (test/diagnostic surface; state itself never leaves unprojected).</summary>
        public ulong StateVersion => _host.StateVersion;
        /// <summary>Authority-side host, for the replay-determinism harness only (same surface the
        /// golden-replay tests use on LocalMatchHost). Never a wire path.</summary>
        internal LocalMatchHost Host => _host;

        private static string MintToken()
            => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // ── Start ────────────────────────────────────────────────────────────────

        /// <summary>BeginMatch on the core (server-minted seed — ADR-0004), open the replay log,
        /// send both match_starts, arm the mulligan clock.</summary>
        public void Start(IServerConnection conn0, IServerConnection conn1)
        {
            long now = _time.NowMs;
            _seats[0].Conn = conn0;
            _seats[1].Conn = conn1;
            _seats[0].Link = new ConnectionPolicyState(_opts, now);
            _seats[1].Link = new ConnectionPolicyState(_opts, now);

            _replay.Begin(_id, _seed, _seats[0].Player, _seats[1].Player);
            _host.BeginMatch(_id, _seats[0].Player, _seats[1].Player, _seed);
            _turnKey = TurnKey();

            for (int seat = 0; seat < 2; seat++)
            {
                var me = _seats[seat];
                var opp = _seats[1 - seat];
                Send(seat, new MatchStartMsg(
                    me.Player, seat, _opts.EngineContractVersion,
                    new OpponentInfo(opp.Player, opp.Player.Value),
                    SnapshotMsg.Of(_host.Snapshot(me.Player)),
                    me.ReconnectToken));
            }

            _mulliganDeadlineMs = now + _opts.MulliganMs;
        }

        // ── The one door ─────────────────────────────────────────────────────────

        public void Dispatch(SessionCommand cmd)
        {
            if (Ended) return;
            switch (cmd)
            {
                case SessionCommand.Frame f: OnFrame(f.Origin, f.Bytes); break;
                case SessionCommand.ClockTick: OnTick(_time.NowMs); break;
                case SessionCommand.Disconnected d:
                    if (SeatOfConnection(d.Origin) is int vacated) OnDisconnected(vacated);
                    break;
                case SessionCommand.Reconnect r: OnReconnect(r.Message, r.Connection); break;
                default: throw new InvalidOperationException($"Unknown session command '{cmd.GetType().Name}'.");
            }
        }

        /// <summary>Seat lookup by PlayerId (stable for the session's life; gate routing surface).</summary>
        public int SeatOf(PlayerId player)
        {
            if (Equals(_seats[0].Player, player)) return 0;
            if (Equals(_seats[1].Player, player)) return 1;
            return -1;
        }

        /// <summary>Reference-identity seat binding: frames/disconnects from a connection that is
        /// not the seat's CURRENT socket are stale or forged — dropped (SessionCommand doc).</summary>
        private int? SeatOfConnection(IServerConnection origin)
        {
            if (ReferenceEquals(_seats[0].Conn, origin)) return 0;
            if (ReferenceEquals(_seats[1].Conn, origin)) return 1;
            return null;
        }

        // ── Inbound frames: the §6 policy pipeline ───────────────────────────────

        private void OnFrame(IServerConnection origin, byte[] bytes)
        {
            if (SeatOfConnection(origin) is not int seat) return;   // unbound socket: drop
            var s = _seats[seat];
            var link = s.Link;
            if (s.Conn is null || link is null) return;   // vacant seat: stale post, drop
            long now = _time.NowMs;
            link.LastHeardMs = now;

            // §6.5 pre-decode frame cap: oversized frames are never decoded.
            if (bytes.Length > _opts.MaxFrameBytes) { Strike(seat); return; }

            var res = ProtocolCodec.Decode(bytes);

            // §6.1 decode gate: failures are strike-and-close, never RejectMsg (RC F3) — except
            // UnknownIntentTag, where the envelope parsed and A5 mandates the UnknownIntent reject.
            if (!res.Ok)
            {
                if (res.Error == ProtocolError.UnknownIntentTag && TrySeq(link, res.Seq))
                    Send(seat, new RejectMsg(RejectReason.UnknownIntent, res.Seq));
                else
                    Strike(seat);
                return;
            }

            if (!string.Equals(res.MatchId.Value, _id.Value, StringComparison.Ordinal)) { Strike(seat); return; }

            // §6.2 per-connection strictly-increasing seq; violations are protocol offenses (a replayed
            // frame is an attack, not a rules mistake — no RejectReason fits, so strike, not reject).
            if (!TrySeq(link, res.Seq)) { Strike(seat); return; }

            switch (res.Message)
            {
                case HeartbeatMsg:
                    Send(seat, new HeartbeatMsg());
                    break;
                case AckMsg ack:
                    link.AckVersion = ack.Version;   // recorded, never consumed in B1 (RC Q2)
                    break;
                case ResyncMsg:
                    SendSnapshot(seat);              // client-initiated resync (§6, two triggers one remedy)
                    break;
                case IntentMsg im:
                    OnIntent(seat, im, res.Seq, now);
                    break;
                default:
                    // Server→client types (or reconnect on an already-bound connection) from a client
                    // cannot be produced by an honest peer: protocol offense.
                    Strike(seat);
                    break;
            }
        }

        private static bool TrySeq(ConnectionPolicyState link, ulong seq)
        {
            if (seq <= link.SeqHighWater) return false;
            link.SeqHighWater = seq;
            return true;
        }

        private void OnIntent(int seat, IntentMsg im, ulong seq, long now)
        {
            var s = _seats[seat];
            var link = s.Link!;
            var intent = im.Intent;

            // Seat identity: an intent carrying someone else's PlayerId cannot come from an honest
            // client (state-sync §6.4 spirit) — protocol offense, not a rules reject.
            if (!Equals(intent.Player, s.Player)) { Strike(seat); return; }

            // §6.3 allowlist — the second gate. Load-bearing for AutoConfirmIntent (RC F6).
            if (!IntentAllowlist.IsClientLegal(intent))
            {
                Send(seat, new RejectMsg(RejectReason.UnknownIntent, seq));
                return;
            }

            // §6.5 token bucket; out-of-turn costs more (§6.4 feeds the limiter).
            bool outOfTurn = IntentAllowlist.IsTurnScoped(intent)
                && _host.State.Phase != TurnPhase.Mulligan
                && !Equals(_host.State.ActivePlayer, s.Player);
            int cost = outOfTurn ? _opts.OutOfTurnIntentCost : _opts.InTurnIntentCost;
            if (!link.Bucket.TryTake(cost, now))
            {
                Send(seat, new RejectMsg(RejectReason.RateLimited, seq));
                link.EmptyRejects++;
                if (link.EmptyRejects >= _opts.EmptyRejectsToDisconnect)
                {
                    s.Conn?.Close(1008, "rate limit");
                    OnDisconnected(seat);
                }
                return;
            }
            link.EmptyRejects = 0;

            // §6.4 turn-ownership pre-check — defense-in-depth before the core sees it.
            if (outOfTurn)
            {
                Send(seat, new RejectMsg(RejectReason.NotYourTurn, seq));
                MaybeServerResync(seat, im.LastVersion);
                return;
            }

            var result = SubmitToCore(intent, SubmitCause.Client, seat);
            if (!result.Accepted)
            {
                Send(seat, new RejectMsg(result.Reason!.Value, seq));
                MaybeServerResync(seat, im.LastVersion);
            }
        }

        /// <summary>Server-initiated resync (§6): a rejected intent from a stale client gets a fresh
        /// snapshot alongside the reject — same remedy as a client-initiated resync.</summary>
        private void MaybeServerResync(int seat, ulong claimedLastVersion)
        {
            if (claimedLastVersion < _host.StateVersion) SendSnapshot(seat);
        }

        // ── The core submit path (client and synthetic share it — §3) ────────────

        private CommandResult SubmitToCore(PlayerIntent intent, SubmitCause cause, int? originSeat)
        {
            var raw = _host.Submit(intent);
            if (!raw.Accepted) return raw;

            _replay.Append(_id, intent);

            // Capture the emit-time visibility sidecar NOW — index-parallel to raw.Events and
            // clobbered by the next Execute (a4 §11 finding 1).
            var vis = _processor.LastEventVisibility;

            // Idle-forfeit bookkeeping (§7): clock-fired auto-ends count up; any voluntary accepted
            // intent resets its seat's run.
            if (originSeat is int seatIdx)
            {
                if (cause == SubmitCause.Client) _seats[seatIdx].ConsecutiveAutoTurns = 0;
                else if (cause == SubmitCause.Clock && intent is EndTurnIntent) _seats[seatIdx].ConsecutiveAutoTurns++;
            }

            // Snapshot before events, per viewer (Coordinator ordering; netcode §3.3 — every applied
            // command ships a fresh projected snapshot).
            for (int seat = 0; seat < 2; seat++)
            {
                if (_seats[seat].Conn is null) continue;
                SendSnapshot(seat);
                if (raw.Events.Count > 0)
                {
                    var projected = new List<GameEvent>(raw.Events.Count);
                    for (int i = 0; i < raw.Events.Count; i++)
                        projected.Add(GameStateProjector.ProjectEvent(raw.Events[i], seat, vis[i]));
                    Send(seat, new EventsMsg(raw.StateVersion, projected));
                }
            }

            if (_host.State.Outcome != MatchOutcome.InProgress)
            {
                EndMatch(cause, intent);
                return raw;
            }

            // Turn rollover → re-arm the turn clock + broadcast the deadline (§7).
            var key = TurnKey();
            if (key != _turnKey)
            {
                _turnKey = key;
                if (_host.State.Phase != TurnPhase.Mulligan)
                    ArmTurnClock(_time.NowMs);
            }

            return raw;
        }

        private (TurnPhase, PlayerId, int) TurnKey()
            => (_host.State.Phase, _host.State.ActivePlayer, _host.State.TurnNumber);

        private void ArmTurnClock(long now)
        {
            _turnDeadlineMs = now + _opts.TurnMs;
            _ropeSent = false;
            Broadcast(new TurnTimerMsg(_host.State.ActivePlayer, (ulong)_turnDeadlineMs.Value));
        }

        // ── Clocks (§7): expiries are synthetic intents through the same door ────

        private void OnTick(long now)
        {
            // Heartbeat liveness (§11): silence past the timeout is a disconnect. CLOSE the socket
            // when policy vacates the seat — the TCP link may be perfectly healthy (2026-07-19
            // silent-match-death: an alive client left unclosed kept believing Live forever while
            // its snapshots silently stopped).
            for (int seat = 0; seat < 2; seat++)
            {
                var link = _seats[seat].Link;
                if (_seats[seat].Conn is not null && link is not null
                    && now - link.LastHeardMs > _opts.HeartbeatTimeoutMs)
                {
                    _seats[seat].Conn!.Close(1001, "liveness timeout");
                    OnDisconnected(seat);
                }
            }

            // Disconnect grace (§8): expiry concedes the absent seat. First-vacated fires first
            // on double-disconnect by construction (earlier deadline).
            for (int seat = 0; seat < 2; seat++)
            {
                var s = _seats[seat];
                if (s.Conn is null && s.DisconnectDeadlineMs is long dl && now >= dl)
                {
                    s.DisconnectDeadlineMs = null;
                    SubmitToCore(new ConcedeIntent(s.Player), SubmitCause.DisconnectTimeout, seat);
                    if (Ended) return;
                }
            }

            // Mulligan clock: auto-confirm every unconfirmed seat.
            if (_mulliganDeadlineMs is long mdl && now >= mdl)
            {
                _mulliganDeadlineMs = null;
                var st = _host.State;
                if (st.Phase == TurnPhase.Mulligan)
                {
                    if (!st.Mulligan0Done) SubmitToCore(new AutoConfirmIntent(_seats[0].Player), SubmitCause.Clock, 0);
                    if (!Ended && !_host.State.Mulligan1Done && _host.State.Phase == TurnPhase.Mulligan)
                        SubmitToCore(new AutoConfirmIntent(_seats[1].Player), SubmitCause.Clock, 1);
                    if (Ended) return;
                }
            }

            // Turn clock + rope.
            if (_turnDeadlineMs is long tdl && _host.State.Phase != TurnPhase.Mulligan)
            {
                if (!_ropeSent && now >= tdl - _opts.RopeMs)
                {
                    _ropeSent = true;
                    Broadcast(new TurnTimerMsg(_host.State.ActivePlayer, (ulong)tdl));
                }
                if (now >= tdl)
                {
                    var active = _host.State.ActivePlayer;
                    int activeSeat = _host.State.SeatOf(active);
                    SubmitToCore(new EndTurnIntent(active), SubmitCause.Clock, activeSeat);
                    if (Ended) return;
                    if (_seats[activeSeat].ConsecutiveAutoTurns >= _opts.IdleForfeitAutoTurns)
                        SubmitToCore(new ConcedeIntent(_seats[activeSeat].Player), SubmitCause.IdleForfeit, activeSeat);
                }
            }
        }

        // ── Disconnect / reconnect (§8) ──────────────────────────────────────────

        private void OnDisconnected(int seat)
        {
            var s = _seats[seat];
            if (s.Conn is null) return;
            s.Conn = null;
            s.Link = null;
            s.DisconnectDeadlineMs = _time.NowMs + _opts.DisconnectGraceMs;
            // Match keeps running: opponent's timers unaffected (match-lifecycle §7.1).
        }

        private void OnReconnect(ReconnectMsg msg, IServerConnection conn)
        {
            // Token + seat binding (state-sync §7.2): the token is bound to the PlayerId's seat —
            // a seat-0 token can never resume seat-1. Constant close, no detail leak.
            int seat = -1;
            for (int i = 0; i < 2; i++)
                if (Equals(_seats[i].Player, msg.PlayerId)) { seat = i; break; }
            if (seat < 0 || !string.Equals(_seats[seat].ReconnectToken, msg.Token, StringComparison.Ordinal))
            {
                conn.Close(1008, "reconnect refused");
                return;
            }

            var s = _seats[seat];
            s.Conn?.Close(1000, "superseded");   // zombie socket: the re-auth wins (match-lifecycle §7.2)
            long now = _time.NowMs;
            s.Conn = conn;
            s.Link = new ConnectionPolicyState(_opts, now);   // fresh link = seq baseline + bucket reset (RC F7)
            s.DisconnectDeadlineMs = null;

            // Snapshot-then-stream (§8; snapshot-only by ruling RC F8), plus the live turn deadline.
            SendSnapshot(seat);
            if (_turnDeadlineMs is long tdl && _host.State.Phase != TurnPhase.Mulligan)
                Send(seat, new TurnTimerMsg(_host.State.ActivePlayer, (ulong)tdl));
        }

        // ── Match end ────────────────────────────────────────────────────────────

        private void EndMatch(SubmitCause cause, PlayerIntent finalIntent)
        {
            var reason = cause switch
            {
                SubmitCause.Client => finalIntent is ConcedeIntent ? MatchEndReason.Concede : MatchEndReason.Win,
                SubmitCause.Clock => MatchEndReason.Timeout,
                SubmitCause.IdleForfeit => MatchEndReason.Timeout,
                SubmitCause.DisconnectTimeout => MatchEndReason.Disconnect,
                _ => MatchEndReason.Win,
            };
            Broadcast(new MatchEndMsg(_host.State.Outcome, reason));
            _replay.Complete(_id, _host.State.Outcome);
            Ended = true;
            for (int seat = 0; seat < 2; seat++)
            {
                _seats[seat].Conn?.Close(1000, "match over");
                _seats[seat].Conn = null;
                _seats[seat].Link = null;
            }
        }

        // ── Outbound (enqueue-only — RC F5) ──────────────────────────────────────

        private void SendSnapshot(int seat)
            => Send(seat, SnapshotMsg.Of(_host.Snapshot(_seats[seat].Player)));

        private void Broadcast(NetMessage msg)
        {
            for (int seat = 0; seat < 2; seat++) Send(seat, msg);
        }

        private void Send(int seat, NetMessage msg)
        {
            var s = _seats[seat];
            if (s.Conn is null || s.Link is null) return;
            byte[] frame = ProtocolCodec.Encode(msg, _id, ++s.Link.OutSeq);
            if (!s.Conn.TryEnqueue(frame))
            {
                // Dead/overflowed writer stalls only itself — but CLOSE it, so an overflowed-yet-
                // alive client gets a close frame instead of an eternally quiet socket.
                s.Conn.Close(1001, "send overflow");
                OnDisconnected(seat);
            }
        }

        private void Strike(int seat)
        {
            var s = _seats[seat];
            if (s.Link is null) return;
            s.Link.Strikes++;
            if (s.Link.Strikes >= _opts.MaxStrikes)
            {
                s.Conn?.Close(1008, "protocol violations");
                OnDisconnected(seat);
            }
        }
    }
}
