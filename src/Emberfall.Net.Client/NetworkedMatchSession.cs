using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Client
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
        private readonly IWireTransport _transport;
        private readonly ClientOptions _opts;
        private readonly object _lock = new object();

        private ConnectionState _state = ConnectionState.Idle;
        private DeadReason _deadReason = DeadReason.None;

        private GameState? _current;
        private MatchId _matchId;
        private int _seat = -1;
        private PlayerId _player;
        private string _opponentName = string.Empty;

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

        // Inbound-processed high-water for deterministic tests (no Task.Delay): each handled frame
        // bumps the counter and releases waiters at or below it.
        private int _inboundProcessed;
        private readonly List<(int threshold, TaskCompletionSource<bool> tcs)> _inboundWaiters = new List<(int, TaskCompletionSource<bool>)>();

        public NetworkedMatchSession(IWireTransport transport, ClientOptions? options = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _opts = options ?? new ClientOptions();
            _transport.OnClosed += OnTransportClosed;
        }

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
        public ConnectionState State { get { lock (_lock) return _state; } }
        public DeadReason DeadReason { get { lock (_lock) return _deadReason; } }
        public bool HasPendingSubmit { get { lock (_lock) return _pending is not null; } }
        /// <summary>True while a resync we sent is awaiting the server's same-version answer (RC-F8).</summary>
        public bool ResyncPending { get { lock (_lock) return _resyncPending; } }
        /// <summary>Inbound frames fully handled — a deterministic test-sync surface (§11).</summary>
        public int InboundProcessed { get { lock (_lock) return _inboundProcessed; } }
        /// <summary>Resyncs requested this match (§4.5 budget diagnostic).</summary>
        public int ResyncCount { get { lock (_lock) return _resyncCount; } }

        public event Action<bool>? OnPendingChanged;
        public event Action<ConnectionState>? OnConnectionStateChanged;

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
            StartPump();
            await _matchStarted.Task.ConfigureAwait(false);
        }

        private static Uri WithDeckOptIn(Uri uri)
        {
            var builder = new UriBuilder(uri);
            string query = builder.Query;
            if (query.StartsWith("?", StringComparison.Ordinal)) query = query.Substring(1);
            builder.Query = string.IsNullOrEmpty(query) ? "deck=1" : query + "&deck=1";
            return builder.Uri;
        }

        private void StartPump()
        {
            _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            _pumpTask = Task.Run(() => PumpLoopAsync(_pumpCts.Token));
            _timerTask = Task.Run(() => TimerLoopAsync(_pumpCts.Token));
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
        ///    The §4.3 reconnect machine is not built (commit-2), so this is terminal via the
        ///    same OnPeerClosed path a detected socket drop takes — no more ghost-ship UI.
        /// </summary>
        private async Task TimerLoopAsync(CancellationToken ct)
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
                        await _transport.CloseAsync(WireCloseReason.Fault).ConfigureAwait(false);
                        OnPeerClosed();   // idempotent — no-op if the event already transitioned us
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }

        private async Task PumpLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[]? frame = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
                    if (frame is null) { OnPeerClosed(); break; }
                    HandleFrame(frame);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception) { OnPeerClosed(); }
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
                _opponentName = ms.Opponent.DisplayName ?? string.Empty;
                _current = ms.Snapshot.State;   // initial mirror; no OnSnapshot for the initial (a4 §3)
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
                pendingResolved = MaybeResolvePendingOkUnlocked(s.StateVersion);
                if (_pendingEvents.TryGetValue(s.StateVersion, out var batch))
                {
                    _pendingEvents.Remove(s.StateVersion);
                    flush = new List<GameEvent>(batch);
                }
            }

            if (applied is not null) OnSnapshot?.Invoke(applied);
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
                s.Hash, Emberfall.Core.Serialization.GameStateHasher.Hash(s.State), rawFrame));
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
            _ = _transport.SendAsync(frame, CancellationToken.None);
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

        private void OnPeerClosed()
        {
            lock (_lock)
            {
                if (_state is ConnectionState.Ended or ConnectionState.Dead) return;
                // Commit 1: a socket drop with no reconnect wiring is terminal. The reconnect state
                // machine (§4.3) is commit-2 timer work; the terminal transition + StopMatch signal
                // (RC-F10) is what commit 1 must get right so the runner stops.
                SetStateUnlocked(ConnectionState.Dead);
                _deadReason = DeadReason.Forfeited;
                _pending?.TrySetResult(CommandResult.Rejected(RejectReason.GameOver, StateVersion));
                ClearPendingUnlocked();
            }
        }

        private void OnTransportClosed(WireCloseReason reason) => OnPeerClosed();

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

        public void Dispose()
        {
            try { _pumpCts?.Cancel(); } catch { /* ignore */ }
            _transport.OnClosed -= OnTransportClosed;
        }
    }
}
