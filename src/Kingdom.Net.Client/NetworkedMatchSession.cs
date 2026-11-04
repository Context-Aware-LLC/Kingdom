using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Client
{
    /// <summary>
    /// The B3 client seam (dev-docs/architecture/system-overview.md:17): turns the A5 message stream off an
    /// <see cref="IWireTransport"/> into exactly the <see cref="IMatchSession"/> events
    /// <c>MatchRunner</c> and presentation have consumed from <c>LocalMatchSession</c> since A4,
    /// plus the connection lifecycle a socket makes necessary.
    ///
    /// NOT a second authority (ADR-0002/0004/0006): no prediction, no delta apply, no local
    /// re-simulation. It holds a <b>mirror</b> — a projected <see cref="GameState"/> replaced
    /// WHOLESALE by each applied snapshot and never mutated by the client.
    ///
    /// THREADING: inbound handling runs on one receive-pump task; the public events fire from it.
    /// <see cref="SubmitAsync"/> is called from the runner's loop. Resolution is idempotent
    /// (<c>TrySetResult</c>, RC-F7) so a late reject for an already-resolved seq can never throw.
    /// </summary>
    public sealed class NetworkedMatchSession : IMatchSession, IDisposable
    {
        /// <summary>
        /// The close reason the server sends with status 1013 when a pairing is refused at its
        /// session cap (<c>MatchGate.CapacityRefusal</c>, matched by exact spelling). It surfaces as
        /// <see cref="DeadReason.ServerFull"/>: the court is full, not the player's fault, and the
        /// boot holds them and re-queues instead of failing.
        /// </summary>
        public const string ServerFullReason = "server full";

        /// <summary>The server's close text for a reconnect it will not honour: a wrong token, or a
        /// match that is no longer running (<c>MatchGate</c>, <c>MatchSession.OnReconnect</c>). Matched
        /// by exact spelling, like <see cref="ServerFullReason"/>.</summary>
        public const string ReconnectRefusedReason = "reconnect refused";

        /// <summary>Pauses between reconnect attempts. The first is short because the common drop is
        /// a Wi-Fi band switch that is back inside three seconds (2026-11-04, Sam's Pixel).</summary>
        private static readonly int[] ReconnectBackoffMs = { 250, 500, 1000, 2000, 3000 };

        /// <summary>The wire in use. Replaced by each reconnect attempt, so every read of it is
        /// under <see cref="_lock"/> or of a local copied out from under it.</summary>
        private IWireTransport _transport;
        /// <summary>The close handler subscribed to <see cref="_transport"/>, kept so it can be
        /// unsubscribed. Each transport gets its own, bound to it, so a close from a wire this
        /// session has already replaced is recognised as stale and ignored.</summary>
        private Action<WireCloseReason, string?> _closeHandler;
        private readonly ClientOptions _opts;
        private readonly object _lock = new object();

        private ConnectionState _state = ConnectionState.Idle;
        private DeadReason _deadReason = DeadReason.None;
        private MatchEndReason? _endReason;
        private IReadOnlyList<string> _endFindings = Array.Empty<string>();
        private CommissionProgress? _commission;
        private CommissionFinal? _endCommission;
        // #232: the server's WS close-frame reason text, when the peer supplied one (MatchGate's
        // "deck timeout" / "deck rejected: ..." / "server full" — see WebSocketTransport). DeadReason
        // stays the coarse bucket UI code branches on; this is what a player actually reads.
        private string? _deadReasonDetail;

        private GameState? _current;
        private MatchId _matchId;
        private int _seat = -1;
        private PlayerId _player;
        private string _opponentName = string.Empty;
        private int[] _backIds = { CardBackDefinitions.DefaultId, CardBackDefinitions.DefaultId };

        private ulong _outSeq;
        private long _lastSendMs;
        private long _lastRecvMs;

        // Pending submit (single in flight — §7.1). The TCS resolves on reject or version-advance.
        private TaskCompletionSource<CommandResult>? _pending;
        private ulong _pendingSeq;
        // Last seq we resolved Ok by version-advance inference — used to detect the RC-F7 late reject.
        private ulong _lastOkSeq;
        private bool _haveLastOkSeq;

        // Event ordering (§5.2): batches whose snapshot has not yet applied, keyed by version.
        private readonly Dictionary<ulong, IReadOnlyList<GameEvent>> _pendingEvents = new Dictionary<ulong, IReadOnlyList<GameEvent>>();
        private const int MaxPendingEvents = 8;

        private int _resyncCount;
        private bool _resyncPending;
        // The in-flight resync episode, so the §4.5 deadline can re-send it. Without these the
        // client sends one fire-and-forget frame and waits forever if it is lost.
        private long _resyncSentMs;
        private ResyncReason _resyncReason;
        private ulong _resyncLastVersion;

        private CancellationTokenSource? _pumpCts;
        private Task? _pumpTask;
        private Task? _timerTask;
        private TaskCompletionSource<bool>? _matchStarted;

        // Reconnect (wire-protocol.md "Recovery"; match-lifecycle §7.2). The token is the server's
        // MatchStartMsg.ReconnectToken; the uri is the connect uri, which a reconnect re-targets.
        private string _reconnectToken = string.Empty;
        private Uri? _connectUri;
        private CancellationTokenSource? _reconnectCts;
        private bool _disposed;

        // Inbound-processed high-water for deterministic tests (no Task.Delay): each handled frame
        // bumps the counter and releases waiters at or below it.
        private int _inboundProcessed;
        private readonly List<(int threshold, TaskCompletionSource<bool> tcs)> _inboundWaiters = new List<(int, TaskCompletionSource<bool>)>();

        public NetworkedMatchSession(IWireTransport transport, ClientOptions? options = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _opts = options ?? new ClientOptions();
            _closeHandler = CloseHandlerFor(transport);
            _transport.OnClosed += _closeHandler;
        }

        private Action<WireCloseReason, string?> CloseHandlerFor(IWireTransport transport)
            => (reason, detail) => OnPeerClosed(transport, detail);

        // ── IMatchSession ─────────────────────────────────────────────────────────

        public ulong StateVersion => _current?.StateVersion ?? 0;

        public GameState Current =>
            _current ?? throw new InvalidOperationException("No snapshot yet — await ConnectAsync before reading Current.");

        public event Action<GameState>? OnSnapshot;
        public event GameEventStream? OnEvents;
        public event Action<TurnTimer>? OnTurnTimer;

        public Task<CommandResult> SubmitAsync(PlayerIntent intent)
        {
            if (intent is null) throw new ArgumentNullException(nameof(intent));
            Task<CommandResult> task;
            lock (_lock)
            {
                if (_pending is not null)
                    throw new InvalidOperationException(
                        "A submit is already in flight — the seam is single-in-flight (§7.1). " +
                        "MatchRunner.SubmitLoop enforces this; a second call is programmer error.");
                if (_state != ConnectionState.Live)
                    return Task.FromResult(CommandResult.Rejected(RejectReason.GameOver, StateVersion));

                var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = tcs;
                _pendingSeq = ++_outSeq;
                _pendingLastVersion = _current?.StateVersion ?? 0;
                SendUnlocked(new IntentMsg(_pendingLastVersion, intent), _pendingSeq);
                task = tcs.Task;
            }
            OnPendingChanged?.Invoke(true);
            return task;
        }

        // ── Concrete surface (§7.1 — deliberately not on IMatchSession) ─────────────

        /// <summary>MatchStartMsg.Seat — the local engine seat. Feeds <c>SeatOwnership.Single</c>.</summary>
        public int Seat => _seat;
        public PlayerId Player => _player;
        /// <summary><c>MatchStartMsg.Opponent.DisplayName</c> — for the versus screen's opposite
        /// nameplate. Empty until the match starts.
        ///
        /// <para>SECURITY (VULN-002, armed and mitigated by EF-073): <b>this string originates with
        /// the opponent</b>, who picked it on their own device. It is safe to render for exactly one
        /// reason — the SERVER re-ran <c>PlayerName.Check</c> on it at the handshake and replaced it
        /// with the seat id if it failed, so what arrives here is either a checked name or a
        /// server-authored one. That check is the fix.</para>
        ///
        /// <para>The renderer still hardens: <c>VersusOverlay</c> drops control characters, disables
        /// rich text so markup cannot redraw your layout, and clips the plate in pixels. <b>That is
        /// defense in depth and not the fix</b> — but obligation 4 of VULN-002 §4 is genuinely
        /// client-side, so a NEW renderer of this string owns it and does not inherit it.</para></summary>
        public string OpponentName { get { lock (_lock) return _opponentName; } }
        /// <summary><c>MatchStartMsg.BackIds</c> — the card back engine seat <paramref name="seat"/>
        /// plays under (#421), for both players' hidden cards. The server has already checked the
        /// owner holds it. The default before the match starts and for a seat that is not 0 or 1;
        /// an id this build does not know is the renderer's to resolve (<c>CardBackCatalog</c>).</summary>
        public int CardBackOf(int seat)
        {
            lock (_lock) return seat == 0 || seat == 1 ? _backIds[seat] : CardBackDefinitions.DefaultId;
        }
        public ConnectionState State { get { lock (_lock) return _state; } }
        public DeadReason DeadReason { get { lock (_lock) return _deadReason; } }
        /// <summary><c>MatchEndMsg.Reason</c> — why the match ended, for the result line. Null
        /// until the server says, and for good when the socket died before it could.</summary>
        public MatchEndReason? EndReason { get { lock (_lock) return _endReason; } }
        /// <summary><c>MatchEndMsg.Findings</c> — the Council's Findings this seat committed, as
        /// <c>AchievementTexts</c> ids, for the result screen. Empty until the server says.</summary>
        public IReadOnlyList<string> EndFindings { get { lock (_lock) return _endFindings; } }
        /// <summary>The King's commission card for this seat (#421), off the last applied snapshot,
        /// the match start's included. Null in a match that plays for no King, which is the signal
        /// to hide the card. The server scores it; the client never computes one.</summary>
        public CommissionProgress? Commission { get { lock (_lock) return _commission; } }
        /// <summary><c>MatchEndMsg.Commission</c> — this seat's final score and whether it is a new
        /// best, for the match-end proclamation. Null until the server says, and whenever the score
        /// does not count; the server decides that, so a final here is always one to proclaim.</summary>
        public CommissionFinal? EndCommission { get { lock (_lock) return _endCommission; } }
        /// <summary>The server's close-frame reason text, when it sent one (#232) — null for a local
        /// close, a transport fault, or a peer close that carried no text. Prefer this over
        /// <see cref="DeadReason"/> when presenting a refusal to the player; it is the difference
        /// between "deck rejected: too many copies of X" and every refusal reading as "Forfeited".</summary>
        public string? DeadReasonDetail { get { lock (_lock) return _deadReasonDetail; } }
        public bool HasPendingSubmit { get { lock (_lock) return _pending is not null; } }
        /// <summary>True while a resync we sent is awaiting the server's same-version answer (RC-F8).</summary>
        public bool ResyncPending { get { lock (_lock) return _resyncPending; } }
        /// <summary>Inbound frames fully handled — a deterministic test-sync surface (§11).</summary>
        public int InboundProcessed { get { lock (_lock) return _inboundProcessed; } }
        /// <summary>Resyncs requested this match (§4.5 budget diagnostic).</summary>
        public int ResyncCount { get { lock (_lock) return _resyncCount; } }

        public event Action<bool>? OnPendingChanged;
        public event Action<ConnectionState>? OnConnectionStateChanged;
        /// <summary>Raised from the snapshot path, after <c>OnSnapshot</c>, for each applied snapshot
        /// that carries a card. Like <c>OnSnapshot</c> it does not fire for the match start's
        /// snapshot: read <see cref="Commission"/> when the match starts. Fires off the pump.</summary>
        public event Action<CommissionProgress>? OnCommission;

        // ── Lifecycle ───────────────────────────────────────────────────────────────

        /// <summary>Connect, pair, and resolve when the match has started (first snapshot applied)
        /// — or fault on a terminal <see cref="ConnectionState.Dead"/> (b3 §3, §4).</summary>
        public async Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_opts.DeviceToken))
                throw new InvalidOperationException(
                    "ClientOptions.DeviceToken is required — the server refuses a fresh socket that " +
                    "does not open with a HelloMsg carrying one (C-G1-8). Generate a UUID once per " +
                    "device, persist it, and pass it here; a per-launch token is not an identity.");

            SetState(ConnectionState.Connecting);
            _connectUri = uri;
            _matchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SubmitDeckMsg? deck = _opts.DeckSubmission;
            await _transport.ConnectAsync(deck is null ? uri : WithDeckOptIn(uri), ct).ConfigureAwait(false);

            // The hello is the FIRST frame on a fresh socket — before pairing, before the pump.
            // Sent inline rather than from the pump loop so it cannot be reordered behind a
            // heartbeat on a slow start.
            //
            // PreMatch, not _matchId: there is no match yet, and _matchId's default Value is null,
            // which does not encode into a decodable envelope.
            await _transport.SendAsync(
                ProtocolCodec.Encode(
                    new HelloMsg(_opts.DeviceToken, _opts.WireContractVersion, _opts.Platform,
                                 _opts.BuildId, _opts.DisplayName, _opts.AuthToken),
                    ProtocolCodec.PreMatch, 0),
                ct).ConfigureAwait(false);

            if (deck is not null)
            {
                await _transport.SendAsync(
                    ProtocolCodec.Encode(deck, ProtocolCodec.PreMatch, 1),
                    ct).ConfigureAwait(false);
                _outSeq = 1;
            }

            SetState(ConnectionState.Pairing);
            StartPump(_transport);
            await _matchStarted.Task.ConfigureAwait(false);
        }

        /// <summary>The connect uri re-targeted at the server's reconnect route. Path kept, query
        /// replaced: a deck opt-in or a challenge ticket belongs to pairing, which a reconnect skips.</summary>
        private static Uri ReconnectUriFor(Uri connectUri)
            => new UriBuilder(connectUri) { Query = "reconnect=1" }.Uri;

        private static Uri WithDeckOptIn(Uri uri)
        {
            var builder = new UriBuilder(uri);
            string query = builder.Query;
            if (query.StartsWith("?", StringComparison.Ordinal)) query = query.Substring(1);
            builder.Query = string.IsNullOrEmpty(query) ? "deck=1" : query + "&deck=1";
            return builder.Uri;
        }

        /// <summary>Start the receive pump and the liveness timer on <paramref name="transport"/>.
        /// Each pair belongs to one wire: a reconnect cancels the old pair and starts a new one.</summary>
        private void StartPump(IWireTransport transport)
        {
            var cts = new CancellationTokenSource();
            lock (_lock) _pumpCts = cts;
            _pumpTask = Task.Run(() => PumpLoopAsync(transport, cts.Token));
            _timerTask = Task.Run(() => TimerLoopAsync(transport, cts.Token));
        }

        /// <summary>
        /// The b3 §4.2 liveness loop — the missing half of the b1 §11 heartbeat contract (root
        /// cause of the 2026-07-19 silent-match-death blocker: ClientOptions carried the knobs,
        /// nothing consumed them, so a client thinking for 30s went server-side "dead", was
        /// vacated without a socket close, and the disconnect grace conceded it).
        /// Two duties, both Live-only:
        ///  - send-idle ≥ HeartbeatIdleMs ⇒ send a HeartbeatMsg (the server echoes it, so a
        ///    healthy link is never inbound-silent for long either);
        ///  - inbound-silent ≥ InboundSilenceMs despite those echoes ⇒ the link is truly dead.
        ///    That takes the same OnPeerClosed path a detected socket drop takes: a reconnect when
        ///    <see cref="ClientOptions.ReconnectTransport"/> is set, Dead otherwise.
        /// </summary>
        private async Task TimerLoopAsync(IWireTransport transport, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    long now = _opts.Clock.NowMs;
                    bool linkDead = false;
                    lock (_lock)
                    {
                        if (_state != ConnectionState.Live) continue;
                        if (now - _lastRecvMs >= _opts.InboundSilenceMs)
                            linkDead = true;
                        // A resync nobody answered is re-sent (§4.5). This outranks the heartbeat:
                        // it is the more urgent frame, and it refreshes _lastSendMs anyway. Retries
                        // consume the same budget as fresh requests, so the wait is bounded.
                        else if (_resyncPending && now - _resyncSentMs >= _opts.ResyncTimeoutMs)
                            RequestResyncUnlocked(_resyncReason, _resyncLastVersion);
                        else if (now - _lastSendMs >= _opts.HeartbeatIdleMs)
                            SendUnlocked(new HeartbeatMsg(), ++_outSeq);
                    }
                    if (linkDead)
                    {
                        // CloseAsync raises the transport's OnClosed, which lands in OnPeerClosed.
                        await transport.CloseAsync(WireCloseReason.Fault).ConfigureAwait(false);
                        OnPeerClosed(transport);   // idempotent — no-op if the event already transitioned us
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }

        private async Task PumpLoopAsync(IWireTransport transport, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[]? frame = await transport.ReceiveAsync(ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;   // this wire was replaced; its last read is not news
                    if (frame is null) { OnPeerClosed(transport); break; }
                    HandleFrame(frame);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception) { OnPeerClosed(transport); }
        }

        // ── Inbound ───────────────────────────────────────────────────────────────

        /// <summary>Decode + dispatch one frame. Recoverable corruption requests resync; an unknown
        /// semantic event or envelope version is a terminal contract mismatch.</summary>
        internal void HandleFrame(byte[] frame)
        {
            _lastRecvMs = _opts.Clock.NowMs;
            var res = ProtocolCodec.Decode(frame);
            if (!res.Ok)
            {
                if (res.Error == ProtocolError.UnknownEventKind || res.Error == ProtocolError.VersionMismatch)
                    FailContractMismatch(res.Detail ?? res.Error.ToString());
                else
                    RequestResync(ResyncReason.DecodeError, StateVersion);
                BumpInbound();
                return;
            }

            switch (res.Message)
            {
                case MatchStartMsg ms: OnMatchStart(ms); break;
                case SnapshotMsg s: ApplySnapshot(s, frame, res.Seq); break;
                case EventsMsg e: OnEventsMsg(e); break;
                case RejectMsg r: OnReject(r); break;
                case MatchEndMsg end: OnMatchEnd(end); break;
                case TurnTimerMsg t: OnTurnTimer?.Invoke(new TurnTimer(t.ActivePlayer, (long)t.DeadlineTick)); break;   // E2 visible clock (C1 entry req); fires off the pump — presentation marshals to main thread
                case HeartbeatMsg: break;      // liveness echo (b1 §11); recorded above
                default: break;                // additive server message under a5 §8 unknown-keys — ignore
            }
            BumpInbound();
        }

        private void OnMatchStart(MatchStartMsg ms)
        {
            if (!string.Equals(ms.WireContractVersion, _opts.WireContractVersion, StringComparison.Ordinal))
            {
                FailContractMismatch(
                    $"Server wire contract '{ms.WireContractVersion}' does not match client '{_opts.WireContractVersion}'.");
                return;
            }

            lock (_lock)
            {
                _matchId = new MatchId(ms.Snapshot.State.Id.Value);
                _seat = ms.Seat;
                _player = ms.PlayerId;
                _reconnectToken = ms.ReconnectToken ?? string.Empty;
                _opponentName = ms.Opponent.DisplayName ?? string.Empty;
                _backIds = new[] { ms.BackIdOf(0), ms.BackIdOf(1) };
                _current = ms.Snapshot.State;   // initial mirror; no OnSnapshot for the initial (a4 §3)
                _commission = ms.Snapshot.Commission;
                SetStateUnlocked(ConnectionState.Live);
            }
            _matchStarted?.TrySetResult(true);
        }

        /// <summary>The §5.1 apply — verify-GATED (RC-F8). A snapshot that fails integrity is NEVER
        /// installed as the mirror; the last good state is held and a resync is requested. A
        /// resync-pending snapshot is exempt from the <c>&lt;=</c> drop guard (the server answers a
        /// resync at the SAME version — <c>MatchSession.cs:193</c>).</summary>
        private void ApplySnapshot(SnapshotMsg s, byte[] rawFrame, ulong envelopeSeq)
        {
            GameState? applied = null;
            CommissionProgress? commission = null;
            List<GameEvent>? flush = null;
            bool pendingResolved = false;
            lock (_lock)
            {
                ulong currentVersion = _current?.StateVersion ?? 0;
                bool haveCurrent = _current is not null;
                if (!_resyncPending && haveCurrent && s.StateVersion <= currentVersion)
                    return; // superseded/duplicate — drop (but a resync we asked for is exempt)

                if (!s.VerifyIntegrity())
                {
                    CaptureDesync(s, rawFrame, envelopeSeq);
                    RequestResyncUnlocked(ResyncReason.HashMismatch, currentVersion);
                    return; // mirror UNCHANGED — never install an unverified snapshot
                }

                _resyncPending = false;
                _current = s.State;             // WHOLESALE REPLACE — only ever a verified state
                applied = s.State;
                _commission = s.Commission;     // and the card with it, never ahead of the state it scores
                commission = s.Commission;
                pendingResolved = MaybeResolvePendingOkUnlocked(s.StateVersion);
                if (_pendingEvents.TryGetValue(s.StateVersion, out var batch))
                {
                    _pendingEvents.Remove(s.StateVersion);
                    flush = new List<GameEvent>(batch);
                }
            }

            if (applied is not null) OnSnapshot?.Invoke(applied);
            if (commission is not null) OnCommission?.Invoke(commission);
            if (flush is not null) OnEvents?.Invoke(flush);
            if (pendingResolved) OnPendingChanged?.Invoke(false);
        }

        private void OnEventsMsg(EventsMsg e)
        {
            List<GameEvent>? release = null;
            lock (_lock)
            {
                ulong currentVersion = _current?.StateVersion ?? 0;
                if (e.StateVersion < currentVersion) return; // superseded — nothing to animate toward a passed state
                if (e.StateVersion == currentVersion)
                {
                    release = new List<GameEvent>(e.Events);
                }
                else
                {
                    if (_pendingEvents.Count >= MaxPendingEvents)
                    {
                        RequestResyncUnlocked(ResyncReason.DecodeError, currentVersion); // overflow = protocol violation (§5.2)
                        return;
                    }
                    _pendingEvents[e.StateVersion] = e.Events;
                }
            }
            if (release is not null) OnEvents?.Invoke(release);
        }

        private void OnReject(RejectMsg r)
        {
            TaskCompletionSource<CommandResult>? resolve = null;
            Misattribution? misattribution = null;
            lock (_lock)
            {
                if (_pending is not null && r.IntentSeq == _pendingSeq)
                {
                    resolve = _pending;
                    ClearPendingUnlocked();
                }
                else if (_haveLastOkSeq && r.IntentSeq == _lastOkSeq)
                {
                    // RC-F7 interim: we resolved this seq Ok off a server-clock snapshot; the real
                    // intent was refused. Belief was wrong, the mirror is NOT (it is a verified
                    // server snapshot). LOG ONLY — the forced resync is STRUCK (ruling 2): resyncing
                    // here is a gratuitous board reload near every rope. This log IS the evidence gate.
                    misattribution = new Misattribution(
                        _matchId.Value ?? string.Empty, _seat, r.IntentSeq, r.Reason.ToString(), StateVersion);
                }
                // else: a reject for an unknown/stale seq — ignore.
            }

            // TrySetResult (never SetResult) — idempotent by construction (RC-F7).
            resolve?.TrySetResult(CommandResult.Rejected(r.Reason, StateVersion));
            if (resolve is not null) OnPendingChanged?.Invoke(false);
            if (misattribution is Misattribution m) _opts.OnMisattribution?.Invoke(m);
        }

        private void OnMatchEnd(MatchEndMsg end)
        {
            TaskCompletionSource<CommandResult>? resolve;
            lock (_lock)
            {
                resolve = _pending;
                _endReason = end.Reason;
                _endFindings = end.FindingIds;
                _endCommission = end.Commission;
                ClearPendingUnlocked();
                SetStateUnlocked(ConnectionState.Ended);
            }
            resolve?.TrySetResult(CommandResult.Rejected(RejectReason.GameOver, StateVersion));
        }

        // ── Submit resolution (§4.4) ────────────────────────────────────────────────

        /// <summary>Accept path: the first snapshot with a version past the submit's baseline
        /// resolves the pending task Ok. INFERRED, not stated — sound while turn-based, and the
        /// RC-F7 clock races are contained by the idempotent late-reject handling in
        /// <see cref="OnReject"/>. Must hold <see cref="_lock"/>. Returns true if it resolved.
        /// The TCS is RunContinuationsAsynchronously, so <c>TrySetResult</c> under the lock never
        /// runs a continuation inline.</summary>
        private bool MaybeResolvePendingOkUnlocked(ulong newVersion)
        {
            if (_pending is null) return false;
            if (newVersion <= _pendingLastVersion) return false;
            var tcs = _pending;
            ulong seq = _pendingSeq;
            IReadOnlyList<GameEvent> events = _pendingEvents.TryGetValue(newVersion, out var b) ? b : Array.Empty<GameEvent>();
            ClearPendingUnlocked();
            _lastOkSeq = seq;
            _haveLastOkSeq = true;
            tcs.TrySetResult(CommandResult.Ok(events, newVersion));
            return true;
        }

        private ulong _pendingLastVersion;

        // ── Resync (§4.5) + capture (§6.3) ──────────────────────────────────────────

        private void RequestResync(ResyncReason reason, ulong lastVersion)
        {
            lock (_lock) RequestResyncUnlocked(reason, lastVersion);
        }

        private void RequestResyncUnlocked(ResyncReason reason, ulong lastVersion)
        {
            _resyncCount++;
            if (_resyncCount > _opts.ResyncBudget)
            {
                SetStateUnlocked(ConnectionState.Dead);
                _deadReason = DeadReason.Desync;
                return;
            }
            _resyncPending = true;
            _resyncReason = reason;
            _resyncLastVersion = lastVersion;
            _resyncSentMs = _opts.Clock.NowMs;
            SendUnlocked(new ResyncMsg(lastVersion, reason), ++_outSeq);
        }

        private void CaptureDesync(SnapshotMsg s, byte[] rawFrame, ulong envelopeSeq)
        {
            if (_opts.OnDesyncCapture is null) return;
            _opts.OnDesyncCapture(new DesyncCapture(
                _matchId.Value ?? string.Empty, _seat, s.StateVersion, envelopeSeq,
                s.Hash, Kingdom.Core.Serialization.GameStateHasher.Hash(s.State), rawFrame));
        }

        private void FailContractMismatch(string detail)
        {
            lock (_lock)
            {
                if (_state is ConnectionState.Ended or ConnectionState.Dead) return;
                _deadReason = DeadReason.ContractMismatch;
                SetStateUnlocked(ConnectionState.Dead);
                _resyncPending = false;
                _pendingEvents.Clear();
                _pending?.TrySetResult(CommandResult.Rejected(RejectReason.GameOver, StateVersion));
                ClearPendingUnlocked();
                _matchStarted?.TrySetException(new InvalidOperationException(detail));
            }
        }

        // ── Outbound + state ────────────────────────────────────────────────────────

        private void SendUnlocked(NetMessage msg, ulong seq)
        {
            _lastSendMs = _opts.Clock.NowMs;
            byte[] frame = ProtocolCodec.Encode(msg, _matchId, seq);
            // Observed, not awaited: a send on a wire that has just died throws, and an unobserved
            // fault is noise. The death itself arrives through the pump or the liveness timer.
            try
            {
                _transport.SendAsync(frame, CancellationToken.None)
                    .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception) { /* a transport that throws synchronously once closed — same story */ }
        }

        private void ClearPendingUnlocked()
        {
            _pending = null;
            _pendingSeq = 0;
        }

        private void SetState(ConnectionState s)
        {
            lock (_lock) SetStateUnlocked(s);
        }

        /// <summary>Set state and fire <see cref="OnConnectionStateChanged"/> off the lock (via the
        /// thread pool) so a subscriber — e.g. <c>NetworkedGameBootstrap</c> calling
        /// <c>MatchRunner.StopMatch</c> on Dead (RC-F10) — cannot re-enter under the lock.</summary>
        private bool SetStateUnlocked(ConnectionState s)
        {
            if (_state == s) return false;
            _state = s;
            ThreadPool.QueueUserWorkItem(_ => OnConnectionStateChanged?.Invoke(s));
            return true;
        }

        /// <summary>
        /// A wire closed. From a wire this session has already replaced, nothing. While Reconnecting,
        /// nothing either: the attempt that owns that wire sees its own failure. From a live match
        /// with <see cref="ClientOptions.ReconnectTransport"/> set and a close the server would take
        /// back, <see cref="ConnectionState.Reconnecting"/>. Anything else is terminal, as it always
        /// was: Dead, which stops the runner (RC-F10).
        /// </summary>
        private void OnPeerClosed(IWireTransport from, string? detail = null)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(from, _transport)) return;
                if (_state is ConnectionState.Ended or ConnectionState.Dead or ConnectionState.Reconnecting) return;

                if (_state == ConnectionState.Live && !_disposed && _opts.ReconnectTransport is not null &&
                    _reconnectToken.Length > 0 && _connectUri is not null && IsRecoverableClose(detail))
                {
                    BeginReconnectUnlocked();
                    return;
                }

                SetStateUnlocked(ConnectionState.Dead);
                // A refusal at the server's session cap is the one close a caller should answer
                // by trying again rather than by failing (item 4b), so it gets its own bucket.
                _deadReason = detail == ServerFullReason ? DeadReason.ServerFull : DeadReason.Forfeited;
                _deadReasonDetail = detail;
                _pending?.TrySetResult(CommandResult.Rejected(RejectReason.GameOver, StateVersion));
                ClearPendingUnlocked();
                // A transport close that lands while ConnectAsync is still awaiting the match_start
                // frame (e.g. the server vacates/supersedes the seat before it ever sends one) would
                // otherwise leave ConnectAsync's `await _matchStarted.Task` blocked forever — nothing
                // else resolves that TCS besides OnMatchStart and FailContractMismatch, and neither
                // fires on a plain peer-close. Mirrors FailContractMismatch's own TrySetException; a
                // no-op once OnMatchStart already resolved it (TrySet*, RC-F7-style idempotence).
                string shown = string.IsNullOrEmpty(detail) ? _deadReason.ToString() : detail;
                _matchStarted?.TrySetException(
                    new InvalidOperationException($"Connection closed before the match started ({shown})."));
            }
        }

        /// <summary>A close the server would take back: no reason text at all (the network went,
        /// which is the case this exists for), or one of the two the server sends when IT gave up on
        /// a link that may be perfectly healthy. Every other reason is a decision — superseded,
        /// refused, a protocol strike, the match over — and reconnecting would argue with it.</summary>
        private static bool IsRecoverableClose(string? detail)
            => detail is null || detail == "liveness timeout" || detail == "send overflow";

        // ── Reconnect (wire-protocol.md "Recovery"; match-lifecycle §7.2) ───────────────

        /// <summary>Enter Reconnecting. Must hold <see cref="_lock"/>. The pending submit resolves
        /// rejected — the server may or may not have taken it, and the snapshot the reconnect brings
        /// back is the only honest answer to which. Events waiting on a snapshot are dropped for the
        /// same reason: a reconnect is snapshot-only (match-lifecycle §7.2).</summary>
        private void BeginReconnectUnlocked()
        {
            SetStateUnlocked(ConnectionState.Reconnecting);
            _pending?.TrySetResult(CommandResult.Rejected(RejectReason.GameOver, StateVersion));
            ClearPendingUnlocked();
            _pendingEvents.Clear();
            try { _pumpCts?.Cancel(); } catch (ObjectDisposedException) { }
            IWireTransport dead = _transport;
            _ = dead.CloseAsync(WireCloseReason.Fault)
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            _ = Task.Run(() => ReconnectLoopAsync(cts.Token));
        }

        private enum AttemptResult { Resumed, Failed, Refused }

        /// <summary>Try, pause, try again, until the match is back, the server refuses, or
        /// <see cref="ClientOptions.ReconnectBudgetMs"/> runs out. The budget is measured on the
        /// client's clock from the drop; an attempt already under way when it runs out finishes.</summary>
        private async Task ReconnectLoopAsync(CancellationToken ct)
        {
            long deadline = _opts.Clock.NowMs + _opts.ReconnectBudgetMs;
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    (AttemptResult result, string? detail) = await TryReconnectOnceAsync(ct).ConfigureAwait(false);
                    if (result == AttemptResult.Resumed) return;
                    if (result == AttemptResult.Refused)
                    {
                        FailReconnect(detail == ReconnectRefusedReason ? DeadReason.MatchGone : DeadReason.Forfeited, detail);
                        return;
                    }
                    if (_opts.Clock.NowMs >= deadline)
                    {
                        FailReconnect(DeadReason.ConnectionLost, null);
                        return;
                    }
                    await _opts.ReconnectDelay(ReconnectBackoffMs[Math.Min(failures, ReconnectBackoffMs.Length - 1)], ct)
                        .ConfigureAwait(false);
                    failures++;
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
        }

        /// <summary>
        /// One attempt: a fresh wire on the reconnect route, the <see cref="ReconnectMsg"/> as its
        /// first frame, then the server's first answer, all inside
        /// <see cref="ClientOptions.ReconnectAttemptTimeoutMs"/>. The answer is the current projected
        /// snapshot (match-lifecycle §7.2 step 5), which applies through the ordinary verified path.
        /// It may carry the version this client already holds, so the attempt marks a resync pending
        /// first: that is exactly the exemption a same-version answer needs, and the one
        /// <see cref="ApplySnapshot"/> already has.
        /// </summary>
        private async Task<(AttemptResult, string?)> TryReconnectOnceAsync(CancellationToken ct)
        {
            IWireTransport wire;
            try { wire = _opts.ReconnectTransport!(); }
            catch (Exception) { return (AttemptResult.Failed, null); }

            string? closeDetail = null;
            Action<WireCloseReason, string?> handler = (reason, detail) =>
            {
                if (detail is not null) closeDetail = detail;
                OnPeerClosed(wire, detail);
            };
            wire.OnClosed += handler;

            Uri uri;
            lock (_lock)
            {
                if (_state != ConnectionState.Reconnecting || ct.IsCancellationRequested)
                {
                    wire.OnClosed -= handler;
                    return (AttemptResult.Failed, null);
                }
                _transport.OnClosed -= _closeHandler;
                _transport = wire;
                _closeHandler = handler;
                uri = ReconnectUriFor(_connectUri!);
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await WithinAttemptAsync(wire.ConnectAsync(uri, attempt.Token), attempt).ConfigureAwait(false);

                byte[] hello;
                lock (_lock)
                {
                    hello = ProtocolCodec.Encode(new ReconnectMsg(_player, _reconnectToken, StateVersion), _matchId, ++_outSeq);
                    _resyncPending = true;
                    _resyncSentMs = _opts.Clock.NowMs;
                }
                await WithinAttemptAsync(wire.SendAsync(hello, attempt.Token), attempt).ConfigureAwait(false);

                Task<byte[]?> answer = wire.ReceiveAsync(attempt.Token);
                await WithinAttemptAsync(answer, attempt).ConfigureAwait(false);
                byte[]? first = await answer.ConfigureAwait(false);
                if (first is null)
                    return (IsRecoverableClose(closeDetail) ? AttemptResult.Failed : AttemptResult.Refused, closeDetail);

                lock (_lock)
                {
                    long now = _opts.Clock.NowMs;
                    _lastRecvMs = now;
                    _lastSendMs = now;
                }
                HandleFrame(first);
                lock (_lock)
                {
                    if (_state != ConnectionState.Reconnecting) return (AttemptResult.Resumed, null);   // the frame ended it
                    SetStateUnlocked(ConnectionState.Live);
                }
                StartPump(wire);
                return (AttemptResult.Resumed, null);
            }
            catch (Exception)
            {
                attempt.Cancel();
                try { await wire.CloseAsync(WireCloseReason.Local).ConfigureAwait(false); }
                catch (Exception) { /* already gone */ }
                return (AttemptResult.Failed, closeDetail);
            }
        }

        /// <summary>Await <paramref name="step"/>, or throw <see cref="TimeoutException"/> when the
        /// attempt's time runs out first. A real socket's connect does not observe its token, so the
        /// bound has to live out here.</summary>
        private async Task WithinAttemptAsync(Task step, CancellationTokenSource attempt)
        {
            Task timeout = Task.Delay(TimeSpan.FromMilliseconds(_opts.ReconnectAttemptTimeoutMs), attempt.Token);
            Task done = await Task.WhenAny(step, timeout).ConfigureAwait(false);
            if (done != step)
            {
                _ = step.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("reconnect attempt timed out");
            }
            await step.ConfigureAwait(false);   // rethrows a fault
        }

        private void FailReconnect(DeadReason reason, string? detail)
        {
            IWireTransport wire;
            lock (_lock)
            {
                if (_state != ConnectionState.Reconnecting) return;
                _deadReason = reason;
                _deadReasonDetail = detail;
                _resyncPending = false;
                SetStateUnlocked(ConnectionState.Dead);
                wire = _transport;
            }
            _ = wire.CloseAsync(WireCloseReason.Local)
                .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        // ── Deterministic test synchronization (no Task.Delay — §11) ─────────────────

        private void BumpInbound()
        {
            List<TaskCompletionSource<bool>>? release = null;
            lock (_lock)
            {
                _inboundProcessed++;
                for (int i = _inboundWaiters.Count - 1; i >= 0; i--)
                {
                    if (_inboundWaiters[i].threshold <= _inboundProcessed)
                    {
                        (release ??= new List<TaskCompletionSource<bool>>()).Add(_inboundWaiters[i].tcs);
                        _inboundWaiters.RemoveAt(i);
                    }
                }
            }
            if (release is not null) foreach (var t in release) t.TrySetResult(true);
        }

        /// <summary>Await until at least <paramref name="count"/> inbound frames have been fully
        /// handled. Lets a loopback test drive the server, then await the client catching up —
        /// deterministic, no sleeps.</summary>
        public Task WaitInboundAsync(int count)
        {
            lock (_lock)
            {
                if (_inboundProcessed >= count) return Task.CompletedTask;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inboundWaiters.Add((count, tcs));
                return tcs.Task;
            }
        }

        /// <summary>
        /// Stop the pumps and close the socket. Closing is the load-bearing half: a queued player
        /// who leaves (the at-capacity wait's Leave button, a scene change) must leave the SERVER's
        /// queue too, and before 2026-10-23 this only stopped reading — the socket stayed open
        /// with nobody behind it, so the server could still seat it and hand an opponent a ghost.
        /// Unsubscribed first, so the local close does not report itself as a peer's.
        /// </summary>
        public void Dispose()
        {
            IWireTransport wire;
            lock (_lock)
            {
                _disposed = true;
                try { _pumpCts?.Cancel(); } catch { /* ignore */ }
                try { _reconnectCts?.Cancel(); } catch { /* ignore */ }
                wire = _transport;
                wire.OnClosed -= _closeHandler;
            }
            try { _ = wire.CloseAsync(WireCloseReason.Local); } catch { /* already gone */ }
        }
    }
}
