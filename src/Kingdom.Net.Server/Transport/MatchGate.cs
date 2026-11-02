using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Bots;
using Kingdom.Net.Server.Decks;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Ranking;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Social;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Training;

namespace Kingdom.Net.Server.Transport
{
    /// <summary>
    /// The transport/session gate around the server-owned matchmaking seam (EF-070). Fresh sockets
    /// say hello, enter <see cref="IMatchmakingQueue"/>, and receive FIFO seats when its rating
    /// policy pairs them. Reconnects skip the queue via `/ws?reconnect=1` — a transport routing
    /// hint only; authentication is the
    /// ReconnectMsg token, judged by the session (§8). Also owns the RC Q4 session cap: at
    /// capacity, new pairings are refused with close 1013 <see cref="CapacityRefusal"/> before
    /// queueing, and the client holds the player and re-queues on its own cadence.
    ///
    /// <para><b>C-G1-8:</b> the socket connect is no longer the hello. A fresh socket must open
    /// with a <see cref="HelloMsg"/> carrying a device token, which becomes the seat's
    /// <see cref="AccountId"/> — stable across restarts, unlike the per-process counter
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
        private readonly IMatchmakingQueue _matchmaking;
        private readonly Func<AccountId, int> _ratingForAccount;
        private readonly IIdentityTokenVerifier? _tokenVerifier;
        private readonly IAccountStore? _accountStore;
        private readonly IBotPool? _botPool;
        private readonly IRatingStore? _ratingStore;
        private readonly IRankedMatchSink _rankedMatchSink;
        private readonly ITrainingEpisodeStore? _trainingEpisodes;

        /// <summary>
        /// The onboarding grant, run once per authenticated account on connect. Null when no
        /// entitlement ledger is configured — a dev box with no economy, where ownership stays
        /// unknown and nothing is granted.
        /// </summary>
        private readonly OnboardingGrantService? _onboarding;

        /// <summary>
        /// The ownership authority for L6 on a submitted deck. Null when no economy is configured,
        /// which <see cref="AccountOwnedCounts.ForAsync"/> reads as "ownership unknown, do not
        /// constrain" — the behaviour every box had before the ledger existed.
        /// </summary>
        private readonly IEntitlementLedger? _entitlementLedger;

        /// <summary>
        /// Judges a challenge ticket presented on <c>/ws?challenge=…</c> (#409). Null when the
        /// social surface is not composed in, and then every ticket is refused.
        /// </summary>
        private readonly IChallengeTickets? _challenges;

        /// <summary>
        /// The first-match policy's count of each account's finished matches (launch item 5). Null
        /// turns the policy off and every seat queues as it always has. Only a signed-in seat is
        /// asked about: a tokenless seat is tooling, and a bot never queues.
        /// </summary>
        private readonly ICompletedMatchCounter? _firstMatches;

        /// <summary>The first player of each accepted challenge to arrive, parked until the
        /// second does. Keyed by challenge id; never in <see cref="_matchmaking"/>.</summary>
        private readonly Dictionary<string, WaitingPlayer> _challengeWaiting =
            new Dictionary<string, WaitingPlayer>(StringComparer.Ordinal);

        /// <summary>
        /// Raised with both seats' accounts when a match starts and again when it ends — the
        /// social surface's cue to re-announce their presence. Called outside the gate's lock; a
        /// handler that throws is logged and never costs anybody their match.
        /// </summary>
        public Action<AccountId, AccountId>? MatchRosterChanged { get; set; }

        /// <summary>
        /// Where the first-session funnel's server rows go (pre-publication item 2): <c>hello</c>,
        /// <c>queue</c>, <c>queue_refused</c>, and — through each <see cref="MatchSession"/> —
        /// <c>first_action</c>. Written only for a SIGNED-IN seat: every player signs in, so the
        /// tokenless path is tooling (NetSoak, in-process tests) and a bot never says hello,
        /// and neither is a stranger the funnel is counting.
        /// </summary>
        public IFunnelSink Funnel { get; set; } = NullFunnelSink.Instance;

        private readonly object _lock = new object();
        private readonly Dictionary<string, SessionPump> _sessions = new Dictionary<string, SessionPump>(StringComparer.Ordinal);
        private readonly Dictionary<string, WaitingPlayer> _waiting =
            new Dictionary<string, WaitingPlayer>(StringComparer.Ordinal);
        private int _pendingSessions;

        /// <summary>Per-match attribution (C-G1-8). Server-side only — never on the wire, never in
        /// <c>GameState</c>. Cleared with the session; the telemetry emitter reads it at match end.</summary>
        private readonly Dictionary<string, (AccountId p0, AccountId p1)> _accounts =
            new Dictionary<string, (AccountId, AccountId)>(StringComparer.Ordinal);

        private int _nextSeat;

        public MatchGate(
            ServerOptions opts, ITimeSource time, ICardDatabase db, IDeckProvider decks,
            IReplayStore replay, ITelemetrySink? telemetry = null,
            IIdentityTokenVerifier? tokenVerifier = null, IAccountStore? accountStore = null,
            IBotPool? botPool = null, IRatingStore? ratingStore = null,
            IRankedMatchSink? rankedMatchSink = null,
            ITrainingEpisodeStore? trainingEpisodes = null,
            OnboardingGrantService? onboarding = null,
            IEntitlementLedger? entitlementLedger = null,
            IChallengeTickets? challenges = null,
            ICompletedMatchCounter? firstMatches = null)
            : this(
                opts,
                time,
                db,
                decks,
                replay,
                new BandWideningMatchmakingQueue(time, opts.Matchmaking),
                _ => opts.Matchmaking.DefaultRating,
                telemetry,
                tokenVerifier,
                accountStore,
                botPool,
                ratingStore,
                rankedMatchSink,
                trainingEpisodes,
                onboarding,
                entitlementLedger,
                challenges,
                firstMatches)
        {
        }

        public MatchGate(
            ServerOptions opts, ITimeSource time, ICardDatabase db, IDeckProvider decks,
            IReplayStore replay, IMatchmakingQueue matchmaking,
            Func<AccountId, int> ratingForAccount, ITelemetrySink? telemetry = null,
            IIdentityTokenVerifier? tokenVerifier = null, IAccountStore? accountStore = null,
            IBotPool? botPool = null, IRatingStore? ratingStore = null,
            IRankedMatchSink? rankedMatchSink = null,
            ITrainingEpisodeStore? trainingEpisodes = null,
            OnboardingGrantService? onboarding = null,
            IEntitlementLedger? entitlementLedger = null,
            IChallengeTickets? challenges = null,
            ICompletedMatchCounter? firstMatches = null)
        {
            _opts = opts;
            _time = time;
            _db = db;
            _decks = decks;
            _replay = replay;
            _matchmaking = matchmaking ?? throw new ArgumentNullException(nameof(matchmaking));
            _ratingForAccount = ratingForAccount ?? throw new ArgumentNullException(nameof(ratingForAccount));
            _telemetry = telemetry ?? NullTelemetrySink.Instance;
            _tokenVerifier = tokenVerifier;
            _accountStore = accountStore;
            _botPool = botPool;
            _ratingStore = ratingStore;
            _rankedMatchSink = rankedMatchSink ?? NullRankedMatchSink.Instance;
            _trainingEpisodes = trainingEpisodes;
            _onboarding = onboarding;
            _entitlementLedger = entitlementLedger;
            _challenges = challenges;
            _firstMatches = firstMatches;
        }

        public int LiveSessions
        {
            get { lock (_lock) { return _sessions.Count; } }
        }

        public int MaxSessions => _opts.MaxSessions;

        /// <summary>
        /// The close reason (with status 1013) on a pairing refused at <see cref="MaxSessions"/>.
        /// <b>The client matches this exact spelling</b> (<c>NetworkedMatchSession.ServerFullReason</c>)
        /// to keep the player waiting and re-queue on a short cadence instead of failing the boot
        /// (item 4b of the first-session plan). Reword one and the other must follow, or a full
        /// court reads to the player as an unexplained refusal.
        /// </summary>
        public const string CapacityRefusal = "server full";

        /// <summary>Is this account seated in a live match right now? Server-side only.</summary>
        public bool IsInMatch(AccountId account)
        {
            lock (_lock)
            {
                foreach (var seats in _accounts.Values)
                    if (seats.p0 == account || seats.p1 == account) return true;
                return false;
            }
        }

        private sealed record Seating(SessionPump Pump, int Seat);
        private sealed record WaitingPlayer(
            MatchmakingTicket Ticket,
            /// <summary>Widened from <see cref="WebSocketConnection"/> so a bot's
            /// <see cref="BotSeatRunner"/> — the other <see cref="IServerConnection"/> implementation —
            /// can occupy this same waiter shape when <see cref="TryBotFillTimedOutTickets"/> seats one
            /// directly through <see cref="StartMatch"/>.</summary>
            IServerConnection Conn,
            AccountId Account,
            /// <summary>Already through PlayerName.Check (VULN-002 §4.1). Empty means "use the
            /// server-assigned fallback" — either the player never set one or theirs was refused,
            /// and the two are deliberately indistinguishable from here down.</summary>
            string DisplayName,
            /// <summary>The seat's own revalidated deck (EF-071), or null when it submitted none and
            /// is to be dealt the prebuilt fallback for its seat.</summary>
            SeatDeck? Deck,
            /// <summary>Stable label for training provenance; never supplied by an untrusted client.</summary>
            string PolicyIdentity,
            TaskCompletionSource<Seating?> Seated,
            /// <summary>Server clock ms at enqueue (<see cref="ITimeSource.NowMs"/>). Read by
            /// <see cref="TryBotFillTimedOutTickets"/> against <see cref="ServerOptions.BotFillDelayMs"/>
            /// on the SAME band-widening wake the queue already uses — no new timer.</summary>
            long EnqueuedAtMs,
            /// <summary>A signed-in human: this seat writes first-session funnel rows
            /// (<see cref="Funnel"/>). False for a tokenless tooling seat and for a bot.</summary>
            bool FunnelTracked = false,
            /// <summary>Set for a newcomer the first-match policy owes a bot (launch item 5). Such a
            /// waiter is in <see cref="_waiting"/> but NEVER in <see cref="_matchmaking"/>, so no
            /// pairing can reach it, and <see cref="TryBotFillTimedOutTickets"/> seats it with no
            /// delay against this policy.</summary>
            BotOpponent? FirstMatchBot = null);

        /// <summary>Per-match attribution, or null once the session is gone. Read by the telemetry
        /// emitter; exposed for the identity tests.</summary>
        public (AccountId p0, AccountId p1)? AccountsOf(MatchId id)
        {
            lock (_lock)
                return _accounts.TryGetValue(id.Value, out var a) ? a : ((AccountId, AccountId)?)null;
        }

        /// <summary>Drive one fresh socket: hello, (optional deck), pair, then run its receive loop until
        /// it dies. <paramref name="expectDeck"/> is set by the <c>/ws?deck=1</c> route (EF-071): the
        /// socket then owes a <see cref="SubmitDeckMsg"/> as its second handshake frame.
        /// <paramref name="challengeTicket"/> is set by <c>/ws?challenge=…</c> (#409): the socket
        /// then skips the queue and is seated against the one player its ticket names.</summary>
        public async Task HandleFreshAsync(WebSocket socket, CancellationToken ct, bool expectDeck = false,
            string? challengeTicket = null)
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

            if (!string.Equals(
                    hello.ClientContractVersion,
                    _opts.WireContractVersion,
                    StringComparison.Ordinal))
            {
                conn.Close(1002, "contract mismatch");
                await conn.PumpTask;
                return;
            }

            // ── EF-074c (NOR-229). AuthToken present → verified against Supabase JWKS via
            //    IIdentityTokenVerifier; on success the subject hash is resolved through IAccountStore
            //    to a stable player_id. An invalid token CLOSES the socket — no silent fallback to
            //    the tokenless path, because a downgrade is how a rejected credential still gets in.
            //    AuthToken absent → tokenless tooling path (AccountId.FromDeviceToken), only where
            //    ServerOptions.AllowTokenlessMatches opts in.
            //
            //    VULN-003 latent item 2 in security.md remains standing: its trigger is value
            //    attaching to an account, and this ticket attaches none. See security.md §Latent.
            AccountId account;
            // The durable Account behind this seat, when one was resolved through the store. It carries
            // the AccountKind and the player_id that AccountOwnedCounts.ForAsync reads to gate L6 on a
            // submitted deck (NOR-313); null on every path that never touches the store (tokenless, or
            // authenticated with no store), which is the "ownership unknown, do not constrain" case.
            Account? deckOwnerAccount = null;
            // A signed-in seat is a player the first-session funnel counts; a tokenless seat is tooling.
            bool funnelTracked = false;
            // The name this seat is shown under. For a signed-in player it is the one held against
            // their identity, read off the token this method has just verified; see below.
            string candidateName;
            if (!string.IsNullOrEmpty(hello.AuthToken))
            {
                if (_tokenVerifier is null)
                {
                    conn.Close(1008, "authentication not configured");
                    await conn.PumpTask;
                    return;
                }
                var verifyResult = await _tokenVerifier.VerifyAsync(hello.AuthToken, ct);
                if (!verifyResult.IsSuccess)
                {
                    conn.Close(1008, "token rejected");
                    await conn.PumpTask;
                    return;
                }
                var identity = verifyResult.Identity!;
                candidateName = identity.DisplayName;
                string subHash = HashCredential(identity.Subject);
                if (_accountStore is not null)
                {
                    var acct = await _accountStore.ResolveOrCreateAsync("supabase", subHash, identity.Email, ct);
                    // Log linkage: player_id is already a hash; email is hashed (§4.5 — never log raw).
                    Console.Error.WriteLine("[auth] authenticated player_id={0} email_h={1}",
                        acct.PlayerId, HashCredential(identity.Email));
                    account = AccountId.FromAuthenticatedAccount(acct.PlayerId);
                    deckOwnerAccount = acct;

                    // The onboarding grant. Idempotent on signup:{playerId}, so this runs on EVERY
                    // authenticated connect and lands exactly once — there is no due-check here to
                    // race with a second socket from the same account.
                    //
                    // A failure must NOT cost the player their match. An ungranted account still
                    // connects, still plays with the prebuilt deck, and is granted on the next
                    // connect; closing the socket over an economy write would turn a database blip
                    // into "the game won't start". Logged rather than swallowed, because a grant
                    // failing every time is invisible otherwise.
                    if (_onboarding is not null)
                    {
                        try
                        {
                            await _onboarding.EnsureGrantedAsync(acct, ct);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("[onboarding] grant failed for player_id={0}: {1}",
                                acct.PlayerId, ex.Message);
                        }
                    }
                }
                else
                {
                    // No account store configured — derive identity from the subject hash directly.
                    account = AccountId.FromAuthenticatedAccount(subHash);
                }

                // The funnel's `hello`: the device token binds to this account here, confirming the
                // client's own sign_in row. The token is hashed; it is never written.
                funnelTracked = true;
                WriteFunnel(ServerFunnelRows.Hello(account.Value, hello.DeviceToken, _time.NowMs));
            }
            else
            {
                // A device token alone is a tokenless tooling seat: no collection to check a deck against, no quests,
                // no achievements. Players never get here (Sam, 2026-10-18) — the client refuses to
                // connect without a sign-in token — so a deployment with sign-in turns it away,
                // and only a dev or tooling box (NetSoak) opts in. See ServerOptions.AllowTokenlessMatches.
                if (!_opts.AllowTokenlessMatches)
                {
                    conn.Close(1008, "sign-in required");
                    await conn.PumpTask;
                    return;
                }
                account = AccountId.FromDeviceToken(hello.DeviceToken);   // codec guarantees non-empty
                candidateName = hello.DisplayName;
            }

            // ── VULN-002 §4 (EF-073). THIS IS THE HALF THAT COUNTS. The name is peer-authored either
            //    way: a signed-in player writes their own user_metadata, and a tokenless tooling seat types its own into
            //    the hello. So it is re-checked here, on arrival, with the IDENTICAL predicate —
            //    Kingdom.Core.Collection is UnityEngine-free for exactly this reason, and a
            //    re-implementation here would be a second definition free to drift on a length or a
            //    category. A signed-in player's hello.DisplayName is ignored: the identity's copy is
            //    the one their friends see too, so the two cannot disagree.
            //
            //    A refusal is a FALLBACK, not a closed socket: an empty name is the honest first-run
            //    state, and dropping a player's connect over a name they may not know is bad would be
            //    a support incident for no security gain. The fallback is server-authored from a
            //    closed set (the seat id, minted in StartMatch) — see NameForSeat there.
            //
            //    Note what is NOT done: the name is not stored, forwarded, or logged before it passes.
            //    An unvalidated name echoed anywhere is the server laundering peer input into
            //    trusted-looking state (§4.2).
            string checkedName = PlayerName.Check(candidateName).Normalized;   // "" when refused

            // EF-071: a /ws?deck=1 client submits its deck as the SECOND handshake frame, revalidated
            // server-side HERE — an illegal deck is refused at the handshake and never becomes a match
            // (never trust the client; VULN-001 "Client-supplied deck lists"). A plain /ws client
            // (expectDeck=false) submits nothing and is dealt the prebuilt fallback for its seat, so this
            // is purely additive — existing clients are untouched. Bounded by the SAME deadline as the
            // hello so a /ws?deck=1 socket that then goes silent cannot hold the connection open forever.
            SeatDeck? submitted = null;
            if (expectDeck)
            {
                byte[]? deckFrame;
                using (var deckCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    deckCts.CancelAfter(TimeSpan.FromMilliseconds(_opts.HelloTimeoutMs));
                    deckFrame = await ReceiveOneAsync(socket, deckCts.Token);
                }
                if (deckFrame is null)
                {
                    // The reason has to be logged HERE. Kestrel only ever reports "the application
                    // aborted the connection", and the client discards the WebSocket close status —
                    // so without this line a refusal is invisible from BOTH ends and reads to the
                    // player as "Forfeited". Cost hours on 2026-08-21.
                    Console.Error.WriteLine("[gate] refused player_id={0}: deck timeout after {1}ms",
                                            account.Value, _opts.HelloTimeoutMs);
                    conn.Close(1008, "deck timeout");
                    await conn.PumpTask;
                    return;
                }
                var deckRes = ProtocolCodec.Decode(deckFrame);
                if (!deckRes.Ok || deckRes.Message is not SubmitDeckMsg deckMsg)
                {
                    Console.Error.WriteLine("[gate] refused player_id={0}: expected deck, got {1}",
                                            account.Value,
                                            deckRes.Ok ? deckRes.Message?.GetType().Name ?? "null" : "undecodable frame");
                    conn.Close(1008, "expected deck");
                    await conn.PumpTask;
                    return;
                }
                var outcome = DeckSubmission.Validate(
                    deckMsg, _db, await AccountOwnedCounts.ForAsync(deckOwnerAccount, _entitlementLedger, ct));
                if (!outcome.Accepted)
                {
                    Console.Error.WriteLine("[gate] refused player_id={0}: deck rejected: {1}",
                                            account.Value, outcome.Reason);
                    conn.Close(1008, CloseReason("deck rejected: " + outcome.Reason));
                    await conn.PumpTask;
                    return;
                }
                Console.Error.WriteLine("[gate] accepted deck player_id={0}", account.Value);
                submitted = outcome.Seat;
            }

            Seating? seating;
            int refusalCode;
            string refusal;
            if (challengeTicket is null)
                (seating, refusalCode, refusal) =
                    await PairAsync(conn, account, checkedName, submitted, funnelTracked, ct);
            else
                (seating, refusalCode, refusal) = await SeatChallengeAsync(
                    conn, account, checkedName, submitted, challengeTicket, funnelTracked, ct);

            if (seating is null)
            {
                // Three different stories, kept apart because the first-session funnel (item 2)
                // reads them as different drop reasons: the box was full, the player walked away
                // while queued, or the gate turned them away for some other cause.
                if (refusal == CapacityRefusal)
                {
                    // The funnel's `capacity` reason: a queue_refused row, and the log line beside it.
                    if (funnelTracked)
                        WriteFunnel(ServerFunnelRows.QueueRefusedCapacity(account.Value, _time.NowMs));
                    Console.Error.WriteLine("[gate] refused player_id={0} reason=capacity sessions={1}/{2}",
                                            account.Value, LiveSessions, _opts.MaxSessions);
                }
                else if (ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine("[gate] left before seating player_id={0} reason=cancelled",
                                            account.Value);
                }
                else
                {
                    Console.Error.WriteLine("[gate] refused player_id={0}: {1}", account.Value, refusal);
                }
                conn.Close(refusalCode, refusal);
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

        /// <summary>
        /// Queue one socket and wait for its seat. A null seating comes back with the close it is
        /// owed: <see cref="CapacityRefusal"/> when the box is at <see cref="ServerOptions.MaxSessions"/>,
        /// which the client answers by holding the player and re-queueing (item 4b); "match start
        /// failed" when the pairing was made but <see cref="StartMatch"/> threw. A socket that left
        /// while queued also comes back null — the caller tells it apart by its cancelled token, and
        /// must, because until 2026-10-23 that path was logged as "server full".
        /// </summary>
        private async Task<(Seating? Seating, int RefusalCode, string Refusal)> PairAsync(
            WebSocketConnection conn, AccountId account, string displayName, SeatDeck? submitted,
            bool funnelTracked, CancellationToken ct)
        {
            int rating = _ratingStore is null
                ? _ratingForAccount(account)
                : await _ratingStore.GetRatingAsync(account, ct);
            var ticket = new MatchmakingTicket(
                Guid.NewGuid().ToString("N"),
                rating);
            BotOpponent? firstMatchBot = await FirstMatchBotAsync(account, funnelTracked, ct);
            var mySeating = new TaskCompletionSource<Seating?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new WaitingPlayer(
                ticket, conn, account, displayName, submitted, "human", mySeating, _time.NowMs, funnelTracked,
                firstMatchBot);

            lock (_lock)
            {
                if (_sessions.Count + _pendingSessions >= _opts.MaxSessions)
                    return (null, 1013, CapacityRefusal);   // RC Q4: refuse before queueing
                _waiting.Add(ticket.Id, waiter);
                // A newcomer owed a bot never enters the pairing queue, so no human can be paired
                // with them however the queue is woken.
                if (firstMatchBot is null) _matchmaking.Enqueue(ticket);
            }
            // Outside the lock: the sink fsyncs, and the queue must not wait on a disk.
            if (funnelTracked) WriteFunnel(ServerFunnelRows.Queue(account.Value, _time.NowMs));

            StartReadyMatches();
            // Zero fill delay for a newcomer. If every bot is busy they stay parked, still out of
            // the queue, and the wake below retries.
            if (firstMatchBot is not null) TryBotFillTimedOutTickets();

            // Re-run the policy when a band boundary passes so two already-queued ratings can
            // match because of waiting alone; a third arrival is not required to wake the queue.
            using (ct.Register(() => CancelWaiting(ticket.Id, mySeating)))
            using (var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                try
                {
                    while (!mySeating.Task.IsCompleted)
                    {
                        Task wake = _matchmaking.WaitForMatchOpportunityAsync(wakeCts.Token);
                        Task completed = await Task.WhenAny(mySeating.Task, wake);
                        if (ReferenceEquals(completed, mySeating.Task)) break;
                        if (!wake.IsCanceled)
                        {
                            await wake;
                            StartReadyMatches();
                            TryBotFillTimedOutTickets();
                        }
                    }
                    return (await mySeating.Task, 1011, "match start failed");
                }
                finally
                {
                    wakeCts.Cancel();
                }
            }
        }

        /// <summary>
        /// Seat a socket that arrived with a challenge ticket (#409). The queue is skipped
        /// entirely — the two players chose each other — and the SAME <see cref="StartMatch"/>
        /// runs, so seat minting, deck legality, replay and telemetry are a queued match's.
        ///
        /// <para><b>The ticket is judged against the AUTHENTICATED account, not taken on its
        /// own.</b> <see cref="IChallengeTickets.Redeem"/> is handed the account this socket's
        /// hello resolved to, so a ticket in the wrong hands — sniffed, guessed, or presented by
        /// a tokenless seat — redeems nothing. A tokenless seat can never hold one: tickets are only
        /// issued over <c>/social</c>, which requires a sign-in token.</para>
        ///
        /// <para>The first to arrive parks in <see cref="_challengeWaiting"/>; the second starts
        /// the match. If the second never comes the first is told so after
        /// <see cref="ServerOptions.ChallengeArrivalTimeoutMs"/> — a refusal with a reason, not a
        /// socket that hangs until the player gives up.</para>
        /// </summary>
        private async Task<(Seating? Seating, int RefusalCode, string Refusal)> SeatChallengeAsync(
            WebSocketConnection conn, AccountId account, string displayName, SeatDeck? submitted,
            string challengeTicket, bool funnelTracked, CancellationToken ct)
        {
            ChallengeSeat? seat = _challenges?.Redeem(challengeTicket, account.Value);
            if (seat is null) return (null, 1008, "challenge refused");

            var mySeating = new TaskCompletionSource<Seating?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var me = new WaitingPlayer(
                new MatchmakingTicket("challenge-" + Guid.NewGuid().ToString("N"), _opts.Matchmaking.DefaultRating),
                conn, account, displayName, submitted, "human", mySeating, _time.NowMs, funnelTracked);

            WaitingPlayer? opponent;
            lock (_lock)
            {
                if (_challengeWaiting.Remove(seat.ChallengeId, out opponent))
                {
                    if (_sessions.Count + _pendingSessions >= _opts.MaxSessions)
                    {
                        opponent.Seated.TrySetResult(null);
                        return (null, 1013, CapacityRefusal);
                    }
                    _pendingSessions++;
                }
                else
                {
                    _challengeWaiting[seat.ChallengeId] = me;
                }
            }

            if (opponent != null)
            {
                _challenges!.Finish(seat.ChallengeId);
                StartMatch(opponent, me, friendly: true);
                return (await mySeating.Task, 1013, CapacityRefusal);
            }

            using (ct.Register(() => CancelChallengeWaiting(seat.ChallengeId, me)))
            {
                Task arrived = await Task.WhenAny(
                    mySeating.Task, Task.Delay(TimeSpan.FromMilliseconds(_opts.ChallengeArrivalTimeoutMs), ct));
                if (!ReferenceEquals(arrived, mySeating.Task))
                    CancelChallengeWaiting(seat.ChallengeId, me);

                // "server full" is the opponent arriving to a gate at its session cap, which is
                // the only way a parked waiter is completed with null by somebody else.
                bool timedOut = !ReferenceEquals(arrived, mySeating.Task);
                return (await mySeating.Task, timedOut ? 1008 : 1013, timedOut ? "challenge expired" : CapacityRefusal);
            }
        }

        /// <summary>
        /// The bot <see cref="FirstMatchPolicy"/> owes this seat, or null for the ordinary queue.
        /// Only a signed-in seat on a box with both the policy and a bot pool is asked.
        ///
        /// <para><b>A count that cannot be read is treated as a newcomer's</b>, and they play
        /// Greedy: the one outcome launch item 5 forbids is a newcomer meeting a human, and a
        /// veteran dealt one easy bot match is the cheap side of that trade.</para>
        /// </summary>
        private async Task<BotOpponent?> FirstMatchBotAsync(AccountId account, bool funnelTracked, CancellationToken ct)
        {
            if (_firstMatches is null || _botPool is null || !funnelTracked) return null;
            int completed;
            try
            {
                completed = await _firstMatches.CountAsync(account, FirstMatchPolicy.BotOnlyMatches, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    "[gate] first-match count unreadable for player_id={0}, seating as a newcomer: {1}",
                    account.Value, ex.Message);
                completed = 0;
            }
            BotOpponent? bot = FirstMatchPolicy.For(completed);
            if (bot is not null)
                Console.Error.WriteLine("[gate] first-match player_id={0} completed={1} bot={2}",
                    account.Value, completed, bot);
            return bot;
        }

        private void CancelChallengeWaiting(string challengeId, WaitingPlayer waiter)
        {
            lock (_lock)
            {
                // Only if it is still THIS waiter parked there: the opponent may have arrived and
                // taken the entry a moment ago, and then the match is starting.
                if (!_challengeWaiting.TryGetValue(challengeId, out var parked) || !ReferenceEquals(parked, waiter))
                    return;
                _challengeWaiting.Remove(challengeId);
            }
            waiter.Seated.TrySetResult(null);
        }

        private void RaiseRosterChanged(AccountId p0, AccountId p1)
        {
            try
            {
                MatchRosterChanged?.Invoke(p0, p1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[gate] MatchRosterChanged handler faulted: {0}", ex.Message);
            }
        }

        private void CancelWaiting(string ticketId, TaskCompletionSource<Seating?> seating)
        {
            lock (_lock)
            {
                if (!_waiting.Remove(ticketId)) return;   // already matched
                _matchmaking.Remove(ticketId);
            }
            seating.TrySetResult(null);
            StartReadyMatches();   // removing the FIFO head may expose a compatible pair
        }

        /// <summary>
        /// Drain every compatible pair for which a session slot is available. Reservations make
        /// the cap atomic even when several fresh socket handlers wake the queue concurrently.
        /// </summary>
        private void StartReadyMatches()
        {
            while (true)
            {
                WaitingPlayer? first;
                WaitingPlayer? second;
                lock (_lock)
                {
                    if (_sessions.Count + _pendingSessions >= _opts.MaxSessions) return;
                    var pair = _matchmaking.TryDequeueMatch();
                    if (pair is null) return;
                    if (!_waiting.Remove(pair.First.Id, out first) ||
                        first is null ||
                        !_waiting.Remove(pair.Second.Id, out second) ||
                        second is null)
                        throw new InvalidOperationException("Matchmaking queue and gate waiters diverged.");
                    _pendingSessions++;
                }

                StartMatch(first, second);
            }
        }

        /// <summary>
        /// Fills the oldest ticket that has waited &gt;= <see cref="ServerOptions.BotFillDelayMs"/> with a
        /// bot from the pool, by removing it from the real queue and calling the SAME <see cref="StartMatch"/>
        /// two humans go through — no new pairing algorithm, no reliance on rating-band luck (design doc
        /// §Timeout → direct pairing). A no-op unless <see cref="_botPool"/> is configured. If the pool has
        /// no bot free, the ticket is left queued and re-evaluated on the next wake.
        ///
        /// <para>A newcomer the first-match policy owes a bot is due at once, whatever the delay,
        /// and is seated against the policy it is owed rather than the bot-fill default.</para>
        /// </summary>
        private void TryBotFillTimedOutTickets()
        {
            if (_botPool is null) return;
            while (true)
            {
                WaitingPlayer? oldest = null;
                BotHandle? bot;
                lock (_lock)
                {
                    if (_sessions.Count + _pendingSessions >= _opts.MaxSessions) return;
                    long now = _time.NowMs;
                    foreach (var w in _waiting.Values)
                    {
                        if (w.FirstMatchBot is null && now - w.EnqueuedAtMs < _opts.BotFillDelayMs) continue;
                        if (oldest is null || w.EnqueuedAtMs < oldest.EnqueuedAtMs) oldest = w;
                    }
                    if (oldest is null) return;

                    bot = _botPool.TryAcquire(oldest.FirstMatchBot ?? BotOpponent.Default);
                    if (bot is null) return;   // no bot free; leave the ticket queued, retry next wake

                    _waiting.Remove(oldest.Ticket.Id);
                    _matchmaking.Remove(oldest.Ticket.Id);
                    _pendingSessions++;
                }

                var botSeated = new TaskCompletionSource<Seating?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var botTicket = new MatchmakingTicket("bot-" + Guid.NewGuid().ToString("N"), oldest.Ticket.Rating);
                var botWaiter = new WaitingPlayer(
                    botTicket,
                    bot.Seat,
                    bot.Account,
                    bot.DisplayName,
                    bot.Deck,
                    bot.Seat.PolicyIdentity,
                    botSeated,
                    _time.NowMs);

                try
                {
                    StartMatch(oldest, botWaiter);
                }
                finally
                {
                    // LOAD-BEARING INVARIANT: this synchronous .Result read is only safe because
                    // StartMatch always completes botSeated (TrySetResult, success or catch) BEFORE
                    // returning/rethrowing — see the two TrySetResult sites in StartMatch. If a future
                    // refactor makes StartMatch's completion asynchronous relative to its own return,
                    // this becomes a deadlock (or, since TrySetResult uses
                    // RunContinuationsAsynchronously, at best a race) rather than the safe blocking
                    // read it is today.
                    if (botSeated.Task.Result is Seating seated)
                    {
                        bot.Seat.Bind(seated.Pump);
                        seated.Pump.RunTask.ContinueWith(_ => bot.Release(), TaskScheduler.Default);
                    }
                    else
                    {
                        bot.Release();
                    }
                }
            }
        }

        /// <summary>
        /// The nameplate string for a seat: the player's own name once it has passed
        /// <c>PlayerName.Check</c>, otherwise the server-minted <c>PlayerId</c> (VULN-002 §4.1's
        /// "server-assigned fallback").
        ///
        /// <para><b>The fallback is deliberately the seat id and not a friendly word.</b> "Player 1"
        /// or "Opponent" would be a string the server authors into a nameplate that ALSO renders
        /// peer-chosen names — so the moment one player is called "Opponent" by us, "Opponent" is a
        /// name worth claiming, and the reserved list is doing work it should not have to. The seat
        /// id is ugly on purpose: it is unmistakably not a chosen name. The CLIENT substitutes its
        /// own placeholder for display; that is a presentation decision and it belongs there.</para>
        /// </summary>
        private static string NameForSeat(string checkedName, PlayerId seat)
            => checkedName.Length > 0 ? checkedName : seat.Value;

        /// <param name="friendly">A friend challenge (#409): labelled
        /// <see cref="TelemetrySource.Friendly"/> so it moves no rating. Everything else about
        /// the match is identical, deliberately.</param>
        private void StartMatch(WaitingPlayer first, WaitingPlayer second, bool friendly = false)
        {
            bool committed = false;
            string? matchKey = null;
            try
            {
                // PlayerId stays a PER-MATCH handle and is deliberately NOT the identity: it is
                // broadcast to the opponent in OpponentInfo and it lands in hash-canonical GameState.
                // Identity rides alongside in _accounts, server-side only (C-G1-8).
                var p0 = new PlayerId("seat-" + Interlocked.Increment(ref _nextSeat));
                var p1 = new PlayerId("seat-" + Interlocked.Increment(ref _nextSeat));
                var id = new MatchId(Guid.NewGuid().ToString("N"));
                matchKey = id.Value;
                ulong seed = MintSeed();

                // A null _botPool (bot-fill disabled or not yet provisioned) leaves Classify's third
                // argument null too, which is the original two-way Ladder/AiSoak split — unchanged
                // behavior for every deployment that hasn't opted into bot-fill.
                Func<AccountId, bool>? isBotAccount = _botPool is null ? null : _botPool.IsBotAccount;
                var attribution = new MatchAttribution(
                    first.Account, second.Account,                                // FIFO seat order
                    friendly
                        ? TelemetrySource.Friendly
                        : TelemetrySources.Classify(first.Account, second.Account, isBotAccount));

                // EF-071: deal from each seat's SUBMITTED deck where present, else the prebuilt fallback
                // for that seat. HP + spark stay whatever the prebuilt pair uses (30 HP since
                // 2026-08-27) — a submitted deck changes only the cards and the hero class, never the
                // match's balance dials, so this makes no balance decision. Both seats absent ⇒ the
                // exact fixed pair the gate deals today.
                //
                // RETIREMENT TRIGGER: this line is why PrebuiltDecks is a dial on EVERY match rather
                // than only the fallback ones. If HP ever stops coming from `fallback`, say so in
                // PrebuiltDecks' class doc, in NetSoak's ParseHeroHp, and in
                // tooling/scripts/bot_roster_to_corpus.py — all three assert this coupling by name.
                var fallback = _decks.NextMatch();
                var seat0 = first.Deck ?? SeatDeck.FromFallback(fallback, 0);
                var seat1 = second.Deck ?? SeatDeck.FromFallback(fallback, 1);
                var setup = new MatchSetup(
                    seat0.HeroClass, seat0.Deck, seat1.HeroClass, seat1.Deck,
                    fallback.HeroMaxHealth, fallback.SparkTokenId);

                var session = new MatchSession(
                    id, seed, _opts, _time, _db, setup, _replay, p0, p1,
                    _telemetry, attribution,
                    NameForSeat(first.DisplayName, p0), NameForSeat(second.DisplayName, p1),
                    _rankedMatchSink,
                    _trainingEpisodes,
                    attribution.Source == TelemetrySource.AiSoak ? "ai-soak" : first.PolicyIdentity,
                    attribution.Source == TelemetrySource.AiSoak ? "ai-soak" : second.PolicyIdentity,
                    Funnel, first.FunnelTracked, second.FunnelTracked);
                var pump = new SessionPump(session);
                lock (_lock)
                {
                    _sessions[id.Value] = pump;
                    _accounts[id.Value] = (attribution.P0, attribution.P1);
                    _pendingSessions--;
                    committed = true;
                }
                _ = pump.RunTask.ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        _sessions.Remove(id.Value);
                        _accounts.Remove(id.Value);
                    }
                    RaiseRosterChanged(attribution.P0, attribution.P1);
                    StartReadyMatches();
                }, TaskScheduler.Default);

                session.Start(first.Conn, second.Conn);
                // Success path: both waiters' Seated TCS complete before StartMatch returns. See the
                // LOAD-BEARING INVARIANT note at TryBotFillTimedOutTickets' `.Result` read — that
                // synchronous read depends on completion happening on THIS path or the catch below,
                // never later.
                first.Seated.TrySetResult(new Seating(pump, 0));
                second.Seated.TrySetResult(new Seating(pump, 1));
                RaiseRosterChanged(attribution.P0, attribution.P1);
            }
            catch
            {
                lock (_lock)
                {
                    if (committed)
                    {
                        _sessions.Remove(matchKey!);
                        _accounts.Remove(matchKey!);
                    }
                    else
                    {
                        _pendingSessions--;
                    }
                }
                // Failure path: same invariant as the success path above — both Seated TCS must
                // complete (with null) before this rethrows, or TryBotFillTimedOutTickets' `.Result`
                // read blocks forever.
                first.Seated.TrySetResult(null);
                second.Seated.TrySetResult(null);
                throw;
            }
        }

        /// <summary>A funnel row never costs a player anything: the sink is contracted not to throw,
        /// and this holds it to that on the socket path.</summary>
        private void WriteFunnel(FunnelRow row)
        {
            try { Funnel.Write(row); }
            catch (Exception ex) { Console.Error.WriteLine("[funnel] {0} row dropped: {1}", row.Type, ex.Message); }
        }

        /// <summary>16 hex bytes (128 bits) of SHA-256. Same shape as <see cref="AccountId"/>'s value.
        /// Used to hash the Supabase subject and email before they touch a log line (§4.5).
        ///
        /// <para><b>Moved to <see cref="CredentialHash"/> and delegated here (2026-09-01), not
        /// copied.</b> <c>GET /collection</c> authenticates the same player through the same
        /// <c>IAccountStore</c> lookup, and the subject hash IS the lookup key — so a second
        /// implementation that ever drifted from this one would resolve one human to two accounts,
        /// one holding their collection and one empty, silently.</para></summary>
        private static string HashCredential(string value) => CredentialHash.Of(value);

        /// <summary>A WebSocket close reason is capped at 123 UTF-8 bytes (RFC 6455). A validator reason
        /// naming a card can approach that, so it is truncated defensively — the close still carries the
        /// gist and the pump never faults on an over-long control frame.</summary>
        private static string CloseReason(string reason)
            => reason.Length <= 120 ? reason : reason.Substring(0, 117) + "...";

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
