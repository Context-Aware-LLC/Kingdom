using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Replay;
using Emberfall.Net.Server.Ranking;
using Emberfall.Net.Server.Telemetry;
using Emberfall.Net.Server.Training;

namespace Emberfall.Net.Server
{
    /// <summary>
    /// One live match (dev-docs/architecture/system-overview.md:36): the online sibling of LocalMatch's
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
        private enum SubmitCause { Client, Clock, IdleForfeit }

        private sealed class Seat
        {
            public Seat(PlayerId player, string token, string displayName)
            {
                Player = player;
                ReconnectToken = token;
                DisplayName = displayName;
            }

            public PlayerId Player { get; }
            public string ReconnectToken { get; }

            /// <summary>
            /// What the OPPOSITE seat's nameplate renders (VULN-002 §4.2). Server-authored or
            /// server-checked, never raw peer input: <c>MatchGate</c> has already run
            /// <c>PlayerName.Check</c> and substituted the seat id on failure by the time this is set.
            /// <b>The invariant this field exists to hold is that nothing unvalidated can reach
            /// <c>OpponentInfo</c></b> — if you find yourself assigning to it from a wire message,
            /// that invariant is the thing you are breaking.
            /// </summary>
            public string DisplayName { get; }
            public IServerConnection? Conn;
            public ConnectionPolicyState? Link;
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
        private readonly MatchSetup _setup;
        private readonly ITelemetrySink _telemetry;
        private readonly MatchAttribution _attribution;
        private readonly IRankedMatchSink _rankedMatchSink;
        private readonly ITrainingEpisodeStore? _trainingEpisodes;
        private readonly string _p0Policy;
        private readonly string _p1Policy;
        private readonly Seat[] _seats = new Seat[2];
        private bool _trainingCaptureActive;

        /// <summary>
        /// Card definition ids each seat KEPT at mulligan (telemetry-spec §1 `mulliganKept`).
        /// Captured at submit time because it is unrecoverable afterwards: the core replaces
        /// discarded cards in place, so the post-mulligan hand cannot say what was thrown back.
        /// </summary>
        private readonly List<int>[] _mulliganKept = { new List<int>(), new List<int>() };

        /// <summary>
        /// Per-seat `card_seen` accumulators (telemetry-spec §2). Fed by <see cref="ObserveHands"/>
        /// after the opening deal and after every accepted command; read once, at match end.
        /// </summary>
        private readonly CardSeenLedger[] _cardsSeen = { new CardSeenLedger(), new CardSeenLedger() };

        private long? _mulliganDeadlineMs;
        private long? _turnDeadlineMs;
        /// <summary>When OnTick last ran, for the decision-24 stall guard.</summary>
        private long _lastTickMs;
        private bool _ropeSent;
        private (TurnPhase, PlayerId, int) _turnKey;

        /// <summary>
        /// <paramref name="telemetry"/> and <paramref name="attribution"/> are optional so that the
        /// rules-level tests, which have no identity and no interest in it, keep constructing a
        /// session in one line. The defaults are the honest ones: nothing recorded, nobody
        /// attributed — see <see cref="MatchAttribution.None"/>.
        ///
        /// <para><paramref name="p0Name"/>/<paramref name="p1Name"/> are optional for the same
        /// reason and default to the <c>PlayerId</c>, which is what this sent before EF-073. They
        /// must ALREADY have passed <c>PlayerName.Check</c> — this constructor does not re-check
        /// them, because the caller that reads the wire is the one that knows the value is peer
        /// input. Today that caller is <c>MatchGate</c>; a second one inherits the obligation
        /// (VULN-002 §4.1).</para>
        /// </summary>
        public MatchSession(
            MatchId id, ulong seed, ServerOptions opts, ITimeSource time,
            ICardDatabase db, MatchSetup setup, IReplayStore replay,
            PlayerId p0, PlayerId p1,
            ITelemetrySink? telemetry = null, MatchAttribution? attribution = null,
            string? p0Name = null, string? p1Name = null,
            IRankedMatchSink? rankedMatchSink = null,
            ITrainingEpisodeStore? trainingEpisodes = null,
            string p0Policy = "unspecified",
            string p1Policy = "unspecified")
        {
            _id = id;
            _seed = seed;
            _opts = opts;
            _time = time;
            _replay = replay;
            _setup = setup;
            _telemetry = telemetry ?? NullTelemetrySink.Instance;
            _attribution = attribution ?? MatchAttribution.None;
            _rankedMatchSink = rankedMatchSink ?? NullRankedMatchSink.Instance;
            _trainingEpisodes = trainingEpisodes;
            _p0Policy = p0Policy;
            _p1Policy = p1Policy;
            _processor = new CommandProcessor(db);
            _host = new LocalMatchHost(_processor, db, setup);
            _seats[0] = new Seat(p0, MintToken(), string.IsNullOrEmpty(p0Name) ? p0.Value : p0Name!);
            _seats[1] = new Seat(p1, MintToken(), string.IsNullOrEmpty(p1Name) ? p1.Value : p1Name!);
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
            _lastTickMs = now;
            _seats[0].Conn = conn0;
            _seats[1].Conn = conn1;
            _seats[0].Link = new ConnectionPolicyState(_opts, now);
            _seats[1].Link = new ConnectionPolicyState(_opts, now);

            _replay.Begin(_id, _seed, _seats[0].Player, _seats[1].Player);
            _host.BeginMatch(_id, _seats[0].Player, _seats[1].Player, _seed);
            TryBeginTrainingCapture();
            _turnKey = TurnKey();

            // The `inDeck` denominator, and the opening hands — which are the only draws that
            // happen before any command exists to observe after (telemetry-spec §2).
            _cardsSeen[0].NoteDeck(_setup.P0Deck);
            _cardsSeen[1].NoteDeck(_setup.P1Deck);
            ObserveHands();

            for (int seat = 0; seat < 2; seat++)
            {
                var me = _seats[seat];
                var opp = _seats[1 - seat];
                Send(seat, new MatchStartMsg(
                    me.Player, seat, _opts.WireContractVersion,
                    // VULN-002 §4.2: opp.DisplayName, never a value read off a wire message here.
                    // The Seat holds it precisely so this line cannot be the place a raw name enters.
                    new OpponentInfo(opp.Player, opp.DisplayName),
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
            string? captureBefore = CaptureObservationHash(intent.Player);

            // Read the mulligan keeps BEFORE the core sees the intent — afterwards the hand has
            // already been rewritten and the answer is gone (telemetry-spec §1 `mulliganKept`).
            // Held aside rather than stored, so a REJECTED mulligan records nothing.
            (int seat, List<int> kept)? keeps = CaptureMulliganKeeps(intent);

            // Same reason, same timing: after the play resolves the card is on the board or in the
            // graveyard and its hand slot is gone, so `firstPlayedTurn` has to be read now. Held
            // aside rather than recorded, so a REJECTED play never counts as played.
            (int seat, int defId, int turn)? play = CapturePlay(intent);

            var raw = _host.Submit(intent);
            if (!raw.Accepted) return raw;

            if (keeps is { } k) _mulliganKept[k.seat] = k.kept;
            if (play is { } p) _cardsSeen[p.seat].NotePlayed(p.defId, p.turn);

            // Every accepted command is a draw observation point. Runs BEFORE the EndMatch branch
            // below so the last command's draws are still in the sample.
            ObserveHands();

            _replay.Append(_id, intent);
            TryRecordTrainingDecision(intent.Player, captureBefore);

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
                    var projected = GameStateProjector.ProjectEvents(raw.Events, vis, seat);
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

        /// <summary>
        /// The card definition ids this intent would KEEP, if it is a mulligan resolution.
        /// <c>AutoConfirmIntent</c> keeps the whole hand — that is what auto-confirm means, and a
        /// timed-out mulligan is still a real data point about what the opening hand was.
        /// </summary>
        private (int seat, List<int> kept)? CaptureMulliganKeeps(PlayerIntent intent)
        {
            if (_host.State.Phase != TurnPhase.Mulligan) return null;
            if (intent is not MulliganIntent && intent is not AutoConfirmIntent) return null;

            int seat = _host.State.SeatOf(intent.Player);
            var replaced = intent as MulliganIntent;
            var kept = new List<int>();
            foreach (var card in _host.State.Players[seat].Hand)
            {
                bool thrown = false;
                if (replaced is not null)
                    foreach (var id in replaced.ToReplace)
                        if (id == card.Id) { thrown = true; break; }
                if (!thrown) kept.Add(card.DefinitionId);
            }
            return (seat, kept);
        }

        /// <summary>
        /// The def id behind a <see cref="PlayCardIntent"/>, resolved from the seat's hand while the
        /// card is still in it. Null for anything else, and for an instance id that is not in that
        /// seat's hand — a spoofed or stale id is the core's business to reject, and telemetry must
        /// not record a play the core is about to refuse.
        /// </summary>
        private (int seat, int defId, int turn)? CapturePlay(PlayerIntent intent)
        {
            if (intent is not PlayCardIntent play) return null;
            if (_host.State.Outcome != MatchOutcome.InProgress) return null;

            int seat = _host.State.SeatOf(play.Player);
            foreach (var card in _host.State.Players[seat].Hand)
                if (card.Id == play.Card)
                    return (seat, card.DefinitionId, _host.State.TurnNumber);
            return null;
        }

        /// <summary>
        /// Mark everything currently in either hand as drawn (telemetry-spec §2 `drawnThisMatch`).
        /// Cheap and unconditional: two ≤10-card lists, and skipping it when nothing looks like a
        /// draw would mean re-deriving which effects draw — exactly the coupling to card behaviour
        /// this observational design avoids.
        /// </summary>
        private void ObserveHands()
        {
            for (int seat = 0; seat < 2; seat++)
                _cardsSeen[seat].NoteHand(_host.State.Players[seat].Hand);
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
            // ── Stall guard (decision 24, rule 4) ────────────────────────────────
            // Ticks are ~1 Hz. A gap far past that means the SERVER was asleep — GC pause, host
            // hiccup, or Fly's scale-to-zero suspend/resume — and none of that elapsed time is
            // evidence about the players. Credit the gap back to every clock and every link, then
            // skip this pass entirely.
            //
            // Without this, the first tick after a resume finds BOTH seats past HeartbeatTimeoutMs
            // in the same loop below, vacates both, and the zero-window void destroys a match in
            // which both players were sitting there perfectly healthy. Crediting (rather than
            // merely skipping one pass) is the load-bearing part: a skip would only defer the
            // verdict to the next tick a second later, where the stale LastHeardMs still damns them.
            long gap = now - _lastTickMs;
            _lastTickMs = now;
            if (gap > _opts.StallGuardMs)
            {
                for (int seat = 0; seat < 2; seat++)
                    if (_seats[seat].Link is ConnectionPolicyState link) link.LastHeardMs += gap;
                if (_mulliganDeadlineMs is long mstall) _mulliganDeadlineMs = mstall + gap;
                if (_turnDeadlineMs is long tstall) _turnDeadlineMs = tstall + gap;
                return;
            }

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
                    OnDisconnected(seat);   // vacating the last seat voids in here
                    if (Ended) return;
                }
            }

            // NO disconnect grace (decisions 23 + 24). A single absent seat is not a special case:
            // the match keeps running and the idle rule (2 consecutive auto-ended turns, below)
            // is the ONLY thing that forces a concession. Zero seats connected is the distinct
            // case, and it is a void handled at the moment the last seat vacates — not on a timer,
            // because any timer here is the disconnect grace decision 23 deleted on purpose.

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
                    PlayerId timedPlayer;
                    PlayerIntent timeoutIntent;
                    if (_host.State.PendingChoice is PendingChoice pending)
                    {
                        timedPlayer = pending.Chooser;
                        timeoutIntent = new AutoConfirmIntent(timedPlayer);
                    }
                    else
                    {
                        timedPlayer = _host.State.ActivePlayer;
                        timeoutIntent = new EndTurnIntent(timedPlayer);
                    }

                    int timedSeat = _host.State.SeatOf(timedPlayer);
                    SubmitToCore(timeoutIntent, SubmitCause.Clock, timedSeat);
                    if (Ended) return;
                    if (_seats[timedSeat].ConsecutiveAutoTurns >= _opts.IdleForfeitAutoTurns)
                        SubmitToCore(new ConcedeIntent(_seats[timedSeat].Player), SubmitCause.IdleForfeit, timedSeat);
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
            // One seat away: the match keeps running and the opponent's timers are unaffected
            // (match-lifecycle §7.1). Only the idle rule can force a concession from here.
            VoidIfEmpty();
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

            // Snapshot-then-stream (§8; snapshot-only by ruling RC F8), plus the live turn deadline.
            SendSnapshot(seat);
            if (_turnDeadlineMs is long tdl && _host.State.Phase != TurnPhase.Mulligan)
                Send(seat, new TurnTimerMsg(_host.State.ActivePlayer, (ulong)tdl));
        }

        /// <summary>
        /// Decision 24: the instant ZERO seats are connected the match is over, with no result.
        /// No window — any window here would be the disconnect grace timer decision 23 deleted.
        ///
        /// A void deliberately never reaches the rules core: <c>GameState.Outcome</c> is
        /// hash-canonical surface, so leaving it at <c>InProgress</c> means no new
        /// <c>MatchOutcome</c> member and no golden-fixture rebaseline. And no <c>MatchEndMsg</c>
        /// goes out, because there is by definition nobody left to receive one — a returning player
        /// just gets the ordinary "your previous game cannot be found."
        /// </summary>
        private void VoidIfEmpty()
        {
            if (Ended) return;
            if (_seats[0].Conn is not null || _seats[1].Conn is not null) return;

            _replay.Abandon(_id);   // journal keeps the intents; the ladder gets nothing
            AbandonTrainingCapture();
            Emit(() => _telemetry.MatchVoid(new MatchVoidEvent(
                _id, NowUtc(), _host.State.TurnNumber, _attribution.Source,
                _attribution.P0, _attribution.P1)));
            Ended = true;
        }

        // ── Match end ────────────────────────────────────────────────────────────

        private void EndMatch(SubmitCause cause, PlayerIntent finalIntent)
        {
            var reason = cause switch
            {
                SubmitCause.Client => finalIntent is ConcedeIntent ? MatchEndReason.Concede : MatchEndReason.Win,
                SubmitCause.Clock => MatchEndReason.Timeout,
                SubmitCause.IdleForfeit => MatchEndReason.Timeout,
                _ => MatchEndReason.Win,
            };
            Broadcast(new MatchEndMsg(_host.State.Outcome, reason));
            _replay.Complete(_id, _host.State.Outcome);
            CompleteTrainingCapture();

            // The balance sample (telemetry-spec §1). Emitted BEFORE the seats are torn down below,
            // because the per-seat block reads hero class out of live state — and after the wire
            // messages, because a telemetry problem must never cost a player their match-end screen.
            var outcome = _host.State.Outcome;
            Emit(() => _telemetry.MatchComplete(new MatchCompleteEvent(
                _id, NowUtc(), reason, outcome, _host.State.TurnNumber, _attribution.Source,
                SeatBlock(0, outcome), SeatBlock(1, outcome))));

            // The per-card sample (telemetry-spec §2), in one batched call. Emitted AFTER
            // match_complete so a reader tailing the file sees the result before the cards that
            // explain it, and in its own Emit so a failure in either cannot cost the other.
            Emit(() => _telemetry.CardsSeen(new CardSeenEvent(
                _id, NowUtc(), _attribution.Source, CardRows(outcome))));

            // Ratings are a projection of the authoritative result, never client input. The sink
            // rejects bot-fill, AI soak, self-play and unattributed matches before they can move it.
            try
            {
                _rankedMatchSink.Record(new RankedMatchResult(
                    _id,
                    NowUtc(),
                    _attribution.P0,
                    _seats[0].DisplayName,
                    _attribution.P1,
                    _seats[1].DisplayName,
                    outcome,
                    _attribution.Source));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[ranking] match {0} could not be queued: {1}", _id.Value, ex.Message);
            }

            Ended = true;
            for (int seat = 0; seat < 2; seat++)
            {
                _seats[seat].Conn?.Close(1000, "match over");
                _seats[seat].Conn = null;
                _seats[seat].Link = null;
            }
        }

        // ── Telemetry (C-G1-8 step 2) ────────────────────────────────────────────

        private DateTimeOffset NowUtc() => DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs);

        // ── Optional training capture ────────────────────────────────────────────

        private void TryBeginTrainingCapture()
        {
            if (_trainingEpisodes is null) return;
            try
            {
                _trainingEpisodes.Begin(
                    _id,
                    _seed,
                    _seats[0].Player,
                    _seats[1].Player,
                    _setup,
                    _p0Policy,
                    _p1Policy);
                _trainingCaptureActive = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[training-capture] match {0} could not start capture: {1}",
                    _id.Value,
                    ex.Message);
            }
        }

        private string? CaptureObservationHash(PlayerId actor)
        {
            if (!_trainingCaptureActive) return null;
            try
            {
                return GameStateHasher.Hash(GameStateProjector.Project(_host.State, actor));
            }
            catch (Exception ex)
            {
                DisableTrainingCapture("could not project the before-state", ex);
                return null;
            }
        }

        private void TryRecordTrainingDecision(PlayerId actor, string? beforeHash)
        {
            if (!_trainingCaptureActive || beforeHash is null || _trainingEpisodes is null) return;
            try
            {
                string afterHash = GameStateHasher.Hash(GameStateProjector.Project(_host.State, actor));
                _trainingEpisodes.RecordAccepted(_id, actor, beforeHash, afterHash);
            }
            catch (Exception ex)
            {
                DisableTrainingCapture("could not record an accepted decision", ex);
            }
        }

        private void CompleteTrainingCapture()
        {
            if (!_trainingCaptureActive || _trainingEpisodes is null) return;
            try
            {
                string p0Hash = GameStateHasher.Hash(
                    GameStateProjector.Project(_host.State, _seats[0].Player));
                string p1Hash = GameStateHasher.Hash(
                    GameStateProjector.Project(_host.State, _seats[1].Player));
                _trainingEpisodes.Complete(_id, _host.State.Outcome, p0Hash, p1Hash);
                _trainingCaptureActive = false;
            }
            catch (Exception ex)
            {
                DisableTrainingCapture("could not publish the completed episode", ex);
            }
        }

        private void AbandonTrainingCapture()
        {
            if (!_trainingCaptureActive || _trainingEpisodes is null) return;
            try
            {
                _trainingEpisodes.Abandon(_id);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "[training-capture] match {0} could not abandon capture cleanly: {1}",
                    _id.Value,
                    ex.Message);
            }
            _trainingCaptureActive = false;
        }

        private void DisableTrainingCapture(string reason, Exception ex)
        {
            Console.Error.WriteLine(
                "[training-capture] match {0} {1}: {2}",
                _id.Value,
                reason,
                ex.Message);
            AbandonTrainingCapture();
        }

        /// <summary>
        /// Two guards around every emit, both load-bearing.
        ///
        /// <para><b>Unattributed matches are dropped, not written blank.</b> A row keyed on an empty
        /// account is worse than a missing row: it silently pollutes the per-class win-rate sample
        /// the C4 exit bar is judged on, and it looks like real data. <see cref="AccountId"/>'s own
        /// contract says the emitter refuses these — this is where that promise is kept.</para>
        ///
        /// <para><b>A throwing sink never kills a finished match.</b> The match is already over and
        /// correct by the time we get here; losing a telemetry row costs one sample, while an
        /// escaping exception would take down the session pump and, with it, whatever else it was
        /// about to do.</para>
        /// </summary>
        private void Emit(System.Action emit)   // qualified: Contracts has its own `Action` (a card verb)
        {
            if (!_attribution.IsAttributed) return;
            try { emit(); }
            catch (Exception) { /* a dropped sample is not worth a dropped match */ }
        }

        private SeatOutcome SeatBlock(int seat, MatchOutcome outcome)
        {
            var deck = seat == 0 ? _setup.P0Deck : _setup.P1Deck;
            var @class = _host.State.Players[seat].Hero.Class;
            return new SeatOutcome(
                seat == 0 ? _attribution.P0 : _attribution.P1,
                @class,
                DeckIdentity.IdFor(@class),
                DeckIdentity.HashOf(deck),
                ResultFor(seat, outcome),
                _mulliganKept[seat]);
        }

        /// <summary>
        /// Both seats' `card_seen` rows, seat 0 first (telemetry-spec §2).
        ///
        /// <para><b>A draw is not a win for either seat.</b> `matchWon` is the win-when-drawn
        /// NUMERATOR, and a draw is a match nobody won — crediting it to both would inflate every
        /// card in it past the 58% centralization threshold §3 gates on.</para>
        /// </summary>
        private IReadOnlyList<CardSeenRow> CardRows(MatchOutcome outcome)
        {
            var rows = new List<CardSeenRow>();
            for (int seat = 0; seat < 2; seat++)
            {
                rows.AddRange(_cardsSeen[seat].Rows(
                    seat == 0 ? _attribution.P0 : _attribution.P1,
                    _host.State.Players[seat].Hero.Class,
                    ResultFor(seat, outcome) == SeatResult.Win,
                    _mulliganKept[seat]));
            }
            return rows;
        }

        private static SeatResult ResultFor(int seat, MatchOutcome outcome) => outcome switch
        {
            MatchOutcome.Player0Win => seat == 0 ? SeatResult.Win : SeatResult.Loss,
            MatchOutcome.Player1Win => seat == 1 ? SeatResult.Win : SeatResult.Loss,
            _ => SeatResult.Draw,
        };

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
