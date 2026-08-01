using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Decks;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Replay;
using Emberfall.Net.Server.Telemetry;

namespace Emberfall.Net.Server.Transport
{
    /// <summary>
    /// B1's throwaway pairing door (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §5) — replaced by B2 queue; do not
    /// grow. FIFO: the first two fresh sockets that have said hello form a match. Reconnects skip
    /// the queue via `/ws?reconnect=1` — a transport routing hint only; authentication is the
    /// ReconnectMsg token, judged by the session (§8). Also owns the RC Q4 session cap: at
    /// capacity, new pairings are refused with close 1013 before queueing.
    ///
    /// <para><b>C-G1-8:</b> the socket connect is no longer the hello. A fresh socket must open
    /// with a <see cref="HelloMsg"/> carrying a device token, which becomes the seat's
    /// <see cref="AccountId"/> — stable across restarts, unlike the <c>guest-&lt;counter&gt;</c>
    /// it replaces. A socket that says nothing is refused rather than queued, which also means a
    /// dead waiter can no longer occupy the pairing slot.</para>
    ///
    /// <para><b>Breaking for clients that predate the hello.</b> Any distributed build must be
    /// replaced before this deploys.</para>
    /// </summary>
    public sealed class MatchGate
    {
        private readonly ServerOptions _opts;
        private readonly ITimeSource _time;
        private readonly ICardDatabase _db;
        private readonly IDeckProvider _decks;
        private readonly IReplayStore _replay;
        private readonly ITelemetrySink _telemetry;

        private readonly object _lock = new object();
        private readonly Dictionary<string, SessionPump> _sessions = new Dictionary<string, SessionPump>(StringComparer.Ordinal);

        /// <summary>Per-match attribution (C-G1-8). Server-side only — never on the wire, never in
        /// <c>GameState</c>. Cleared with the session; the telemetry emitter reads it at match end.</summary>
        private readonly Dictionary<string, (AccountId p0, AccountId p1)> _accounts =
            new Dictionary<string, (AccountId, AccountId)>(StringComparer.Ordinal);

        private (WebSocketConnection conn, WebSocket socket, AccountId account, TaskCompletionSource<Seating?> seated)? _waiting;
        private int _nextGuest;

        public MatchGate(
            ServerOptions opts, ITimeSource time, ICardDatabase db, IDeckProvider decks,
            IReplayStore replay, ITelemetrySink? telemetry = null)
        {
            _opts = opts;
            _time = time;
            _db = db;
            _decks = decks;
            _replay = replay;
            _telemetry = telemetry ?? NullTelemetrySink.Instance;
        }

        public int LiveSessions
        {
            get { lock (_lock) { return _sessions.Count; } }
        }

        public int MaxSessions => _opts.MaxSessions;

        private sealed record Seating(SessionPump Pump, int Seat);

        /// <summary>Per-match attribution, or null once the session is gone. Read by the telemetry
        /// emitter; exposed for the identity tests.</summary>
        public (AccountId p0, AccountId p1)? AccountsOf(MatchId id)
        {
            lock (_lock)
                return _accounts.TryGetValue(id.Value, out var a) ? a : ((AccountId, AccountId)?)null;
        }

        /// <summary>Drive one fresh socket: hello, pair, then run its receive loop until it dies.</summary>
        public async Task HandleFreshAsync(WebSocket socket, CancellationToken ct)
        {
            var conn = new WebSocketConnection(socket);

            // The hello comes BEFORE the pairing queue (C-G1-8). Refusing here rather than after
            // seating is what guarantees no match can exist without attribution — and it keeps a
            // silent socket from occupying the FIFO slot.
            //
            // On a DEADLINE: this read blocks the whole connection, so without one a socket that
            // connects and says nothing is held open forever with nothing to reap it.
            byte[]? first;
            using (var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                helloCts.CancelAfter(TimeSpan.FromMilliseconds(_opts.HelloTimeoutMs));
                first = await ReceiveOneAsync(socket, helloCts.Token);
            }
            if (first is null)
            {
                conn.Close(1008, "hello timeout");
                await conn.PumpTask;
                return;
            }

            var res = ProtocolCodec.Decode(first);
            if (!res.Ok || res.Message is not HelloMsg hello)
            {
                conn.Close(1008, "expected hello");
                await conn.PumpTask;
                return;
            }

            // ── VULN-003 (security/known-vulnerabilities, LATENT — trigger row "Attaching anything of
            //    value to an account"). THIS IS THE LINE THAT ARMS IT. hello.DeviceToken is a string
            //    the client chose, and this is the whole of our identity check: there is none. That is
            //    adequate while an account holds nothing — the collection is local files on the
            //    player's own disk and there is no accounts table — so presenting someone else's token
            //    buys their telemetry attribution and nothing more.
            //    The moment an account holds an entitlement, a purchase, a rating or a claimed name,
            //    whoever holds this string IS that player, with no password to change and no session
            //    to revoke. Read VULN-003 §4 before keying anything of value off the AccountId below:
            //    value attaches to an authenticated identity, never to a bearer token, and a
            //    guest→account conversion must be one-way — a token that still resolves after linking
            //    is a permanent back door into the account it was meant to protect.
            var account = AccountId.FromDeviceToken(hello.DeviceToken);   // codec guarantees non-empty

            Seating? seating = await PairAsync(conn, socket, account, ct);
            if (seating is null)
            {
                conn.Close(1013, "server full");
                await conn.PumpTask;
                return;
            }
            await ReceiveLoopAsync(socket, seating.Pump, conn, ct);
        }

        /// <summary>Drive one reconnecting socket: first frame must be a `reconnect` envelope; the
        /// session judges the token. The envelope's matchId routes it (pre-session decode for
        /// unbound sockets lives here by design — SessionCommand.Reconnect doc).</summary>
        public async Task HandleReconnectAsync(WebSocket socket, CancellationToken ct)
        {
            var conn = new WebSocketConnection(socket);
            byte[]? first = await ReceiveOneAsync(socket, ct);
            if (first is null) { conn.Abort(); return; }

            var res = ProtocolCodec.Decode(first);
            if (!res.Ok || res.Message is not ReconnectMsg msg)
            {
                conn.Close(1008, "expected reconnect");
                await conn.PumpTask;
                return;
            }

            SessionPump? pump;
            lock (_lock) _sessions.TryGetValue(res.MatchId.Value, out pump);
            if (pump is null)
            {
                conn.Close(1008, "reconnect refused");   // unknown/finished match: same opaque refusal
                await conn.PumpTask;
                return;
            }

            pump.Post(new SessionCommand.Reconnect(msg, conn));
            // No seat resolution here: frames are keyed by the connection object, and the session
            // binds it to a seat only if the token check passes. A forged claim leaves the
            // connection unbound — its frames drop and its close costs nothing (SessionCommand doc).
            await ReceiveLoopAsync(socket, pump, conn, ct);
        }

        private async Task<Seating?> PairAsync(
            WebSocketConnection conn, WebSocket socket, AccountId account, CancellationToken ct)
        {
            TaskCompletionSource<Seating?> mySeating;
            (WebSocketConnection conn, WebSocket socket, AccountId account, TaskCompletionSource<Seating?> seated)? partner = null;

            lock (_lock)
            {
                if (_sessions.Count >= _opts.MaxSessions && _waiting is null)
                    return null;   // RC Q4: refuse before queueing

                if (_waiting is null)
                {
                    mySeating = new TaskCompletionSource<Seating?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiting = (conn, socket, account, mySeating);
                }
                else
                {
                    partner = _waiting;
                    _waiting = null;
                    mySeating = new TaskCompletionSource<Seating?>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            if (partner is null)
            {
                // First of the pair: wait to be seated by whoever arrives next. If we die waiting,
                // VACATE the slot — a dead waiter left in _waiting pairs the next arrival with a
                // corpse and strands it (seen live 2026-07-19 while reproducing the liveness bug).
                using (ct.Register(() =>
                {
                    lock (_lock)
                    {
                        if (_waiting is { } w && ReferenceEquals(w.conn, conn)) _waiting = null;
                    }
                    mySeating.TrySetResult(null);
                }))
                    return await mySeating.Task;
            }

            // Second of the pair: build the match and seat both.
            //
            // PlayerId stays a PER-MATCH handle and is deliberately NOT the identity: it is
            // broadcast to the opponent in OpponentInfo and it lands in hash-canonical GameState.
            // Identity rides alongside in _accounts, server-side only (C-G1-8).
            var p0 = new PlayerId("guest-" + Interlocked.Increment(ref _nextGuest));
            var p1 = new PlayerId("guest-" + Interlocked.Increment(ref _nextGuest));
            var id = new MatchId(Guid.NewGuid().ToString("N"));
            ulong seed = MintSeed();

            var attribution = new MatchAttribution(
                partner.Value.account, account,                                   // seat 0 waited, seat 1 arrived
                TelemetrySources.Classify(partner.Value.account, account));

            var session = new MatchSession(
                id, seed, _opts, _time, _db, _decks.NextMatch(), _replay, p0, p1,
                _telemetry, attribution);
            var pump = new SessionPump(session);
            lock (_lock)
            {
                _sessions[id.Value] = pump;
                _accounts[id.Value] = (attribution.P0, attribution.P1);
            }
            _ = pump.RunTask.ContinueWith(_ =>
            {
                lock (_lock)
                {
                    _sessions.Remove(id.Value);
                    _accounts.Remove(id.Value);
                }
            }, TaskScheduler.Default);

            session.Start(partner.Value.conn, conn);
            partner.Value.seated.TrySetResult(new Seating(pump, 0));
            return new Seating(pump, 1);
        }

        private static ulong MintSeed()
        {
            Span<byte> b = stackalloc byte[8];
            RandomNumberGenerator.Fill(b);
            ulong seed = BitConverter.ToUInt64(b);
            return seed == 0 ? 1UL : seed;   // 0 is the "mint one" sentinel elsewhere; never use it
        }

        // ── Receive plumbing ─────────────────────────────────────────────────────

        private async Task ReceiveLoopAsync(WebSocket socket, SessionPump pump, WebSocketConnection conn, CancellationToken ct)
        {
            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    byte[]? frame = await ReceiveOneAsync(socket, ct);
                    if (frame is null) break;
                    pump.Post(new SessionCommand.Frame(conn, frame));
                }
            }
            finally
            {
                pump.Post(new SessionCommand.Disconnected(conn));
                conn.Abort();
            }
        }

        /// <summary>One whole message, or null on close/abort. Messages beyond MaxFrameBytes are
        /// truncated-read and returned one byte over the cap so the session's §6.5 oversize strike
        /// fires without this layer buffering an attacker's megabytes.</summary>
        private async Task<byte[]?> ReceiveOneAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[8 * 1024];
            int total = 0;
            byte[] assembled = new byte[Math.Min(_opts.MaxFrameBytes + 1, 1024 * 1024)];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return null;

                    int keep = Math.Min(result.Count, assembled.Length - total);
                    Array.Copy(buffer, 0, assembled, total, keep);
                    total += keep;

                    if (total >= assembled.Length)
                    {
                        // Over cap: drain the rest of the message without storing, then hand the
                        // over-sized evidence to the session.
                        while (!result.EndOfMessage)
                            result = await socket.ReceiveAsync(buffer, ct);
                        return assembled;
                    }
                    if (result.EndOfMessage)
                    {
                        var frame = new byte[total];
                        Array.Copy(assembled, frame, total);
                        return frame;
                    }
                }
            }
            catch (Exception)
            {
                return null;   // aborted/faulted socket = disconnect
            }
        }
    }
}
