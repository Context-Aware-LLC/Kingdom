using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Kingdom.AI;
using Kingdom.Core;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Data;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Client.Social;
using Kingdom.Net.Protocol;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Action = System.Action;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The match entry point (dev-docs/architecture/system-overview.md:17), and since 2026-08-21
    /// the ONLY one. It had a sibling — <c>GameBootstrap</c> in <c>Board.unity</c>, an in-process
    /// hot-seat/vs-AI host kept green as the C1 entry requirement for the first networked
    /// playtest. That gate is long since passed and the local path went unplayed, so both were
    /// deleted: the two boots had to be wired twice for every presentation feature, with nothing
    /// reporting a miss (the play-reveal beat shipped invisible for a day that way, 48e99d80).
    /// Solo play is now Find Game: the server's <c>MatchGate</c> pairs a queue that times out
    /// with a <c>BotSeatRunner</c> from its <c>BotPool</c>, through the same <c>StartMatch</c>.
    ///
    /// Three things this does that an in-process host would not have to:
    ///  1. ASYNC boot — connect + pair + match_start is not synchronous (a socket, not an in-process
    ///     call). Boot runs as a coroutine that awaits <see cref="NetworkedMatchSession.ConnectAsync"/>.
    ///  2. SEAT-AWARE — the server deals our seat (0 or 1). The runner is driven with
    ///     <see cref="SeatOwnership"/>.Single(seat); decks/ids/hero classes come from the projected
    ///     snapshot, never a local <c>MatchSetup</c> (the server owns them).
    ///  3. MAIN-THREAD MARSHALING — <see cref="NetworkedMatchSession"/> raises OnSnapshot/OnEvents/
    ///     OnConnectionStateChanged from its background receive pump. Unity API calls must run on the
    ///     main thread, so a decorator (<see cref="MainThreadMatchSession"/>) queues those raises and
    ///     re-fires them from <see cref="Update"/>. This is the one piece that did not exist for the
    ///     synchronous LocalMatchSession, and it can only be proven in-editor at runtime.
    ///
    /// RC-F10: OnConnectionStateChanged → <c>MatchRunner.StopMatch</c> on Dead/Ended — the runner's
    /// only truth source is the mirror, which stops changing after a dead socket, so death must be pushed
    /// in from outside.
    /// </summary>
    public sealed class NetworkedGameBootstrap : MonoBehaviour
    {
        [SerializeField] private MatchRunner matchRunner;

        [Header("Server")]
        [Tooltip("wss endpoint. The live server is wss://emberfall.fly.dev/ws; a local " +
                 "dev server is ws://localhost:8080/ws (Kingdom/Net/Start Local Server).")]
        [SerializeField] private string serverUrl = "wss://emberfall.fly.dev/ws";

        [Header("Board (optional, null = console-only)")]
        [SerializeField] private BoardAnchors boardAnchors;
        [SerializeField] private BoardController boardController;

        [Header("Input (null = spectate; a networked client normally drives its own seat)")]
        [SerializeField] private InputRouter inputRouter;
        [SerializeField] private ProceduralBoardSetup proceduralBoardSetup;

        [Header("VFX (null = disabled)")]
        [SerializeField] private VfxDirector vfxDirector;

        private NetworkedMatchSession _net;
        private readonly MatchQuestWatch _questWatch = new MatchQuestWatch();
        private readonly MatchGoldWatch _goldWatch = new MatchGoldWatch();
        private MainThreadMatchSession _view;
        private DesyncFaultInjector _faultInjector; // [DesyncDbg] dev tool — null outside the editor and dev builds
        private LoadingOverlay _overlay;
        private VersusOverlay _versus;
        private SparkCoinOpening _coinOpening;
        private bool _openingActive;

        /// <summary>The deck this match was queued with, for the result screen's Play Again.
        /// Empty when there is nothing to replay: a friend challenge (its ticket is spent, and a
        /// rematch would need the friend's half of a new handshake) or the dev fallback deck.</summary>
        private string _rematchDeckId = "";
        private MulliganConfirmButtonView _mulligan;

        // Item 4b: the pause between re-queues after a capacity refusal, drawn uniformly per refusal.
        // Short enough that a freed seat is taken within seconds; spread wide enough that a full
        // box's worth of refused clients does not return on the same tick.
        private const float CourtFullRetryMinSeconds = 6f;
        private const float CourtFullRetryMaxSeconds = 10f;
        // How long a re-queue may sit in Pairing unrefused before it counts as queued.
        private const float CourtFullQueuedAfterSeconds = 3f;
        private GameObject _courtFull;

        private void Awake()
        {
            // Frame-0 coverage (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/loading-screen-plate.md §5): the overlay is up before the
            // async Boot() coroutine does any work behind it.
            _overlay = LoadingOverlay.Create(Camera.main, LoadingOverlay.MatchPlate);
        }

        private void Start()
        {
            if (matchRunner == null)
            {
                Debug.LogError("[NetworkedGameBootstrap] matchRunner is not assigned.");
                BootFailed("Scene misconfigured — matchRunner not assigned");
                return;
            }
            StartCoroutine(Boot());
        }

        private void Update()
        {
            _view?.Pump();
            // [DesyncDbg] dev tool — F9 arms a one-shot wire fault (see DesyncFaultInjector).
            if (_faultInjector != null && Keyboard.current?.f9Key.wasPressedThisFrame == true)
                _faultInjector.ArmOneShot();
        }

        private IEnumerator Boot()
        {
            ICardDatabase db;
            try
            {
                db = LoadCardDatabase();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkedGameBootstrap] card database load failed: {ex.Message}");
                BootFailed(Loc.Get("match_boot_error_card_database"));
                yield break;
            }

            SubmitDeckMsg selectedDeck;
            try
            {
                selectedDeck = ConsumeSelectedDeck(db, out _rematchDeckId);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkedGameBootstrap] selected deck unavailable: {ex.Message}");
                BootFailed(Loc.Get("match_boot_error_deck_unavailable"));
                yield break;
            }

            // A friend challenge (#409) seats this socket against that friend instead of the queue.
            // The ticket rides the query string, not HelloMsg, so the wire contract did not move.
            // Taken ONCE, outside the attempt loop below: taking it consumes it.
            string challengeTicket = ChallengeFlow.TakeTicket();
            bool challenge = challengeTicket.Length > 0;
            if (challenge) _rematchDeckId = "";
            string connectUrl = !challenge
                ? serverUrl
                : serverUrl + (serverUrl.Contains("?") ? "&" : "?") + "challenge=" + Uri.EscapeDataString(challengeTicket);

            // ── Item 4b: the court is full ───────────────────────────────────────────────
            // One pass of this loop is one queue attempt: a fresh token, a fresh socket, a fresh
            // hello and deck submit. A refusal at the server's session cap (MatchGate.CapacityRefusal,
            // DeadReason.ServerFull) is NOT a failure: the player stays on this screen, the court-full
            // panel says why, and the loop re-queues after a jittered pause, until seated or until the
            // player leaves (the panel's button, or Android back via its emb-back class). Leaving
            // changes scene, which ends this coroutine and disposes the attempt's socket in OnDestroy.
            // A challenge is not retried: its ticket is spent, so a full court there fails as before.
            int capacityRefusals = 0;
            while (true)
            {
                // EF-074e: the Supabase JWT for this connect, refreshed first if it is near expiry. This
                // is awaited rather than fetched inline because a refresh is a network round trip, and
                // the boot must not present a token that expires during the handshake — the server closes
                // the socket on a bad one and never downgrades silently.
                //
                // A build with sign-in never connects without a token (Sam, 2026-10-18), and every release
                // build has sign-in. A failed token (refresh refused,
                // network down) stops the boot here with Retry; a deployment with sign-in would refuse the
                // tokenless hello anyway ("sign-in required"), and this is the legible version of that.
                // Only an unconfigured dev build connects without a token, and only a tooling server that
                // sets ServerOptions.AllowTokenlessMatches seats it.
                Task<string> authTokenTask = AuthGateway.TokenForConnectAsync();
                while (!authTokenTask.IsCompleted) yield return null;
                string authToken = authTokenTask.Status == TaskStatus.RanToCompletion ? authTokenTask.Result : "";
                if (AuthGateway.IsConfigured && string.IsNullOrEmpty(authToken))
                {
                    BootFailed(ConnectionFailureCopy.SignInUnconfirmed);
                    yield break;
                }

                // ── C-G1-4 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-G1-REVIEW.md §3) — the gate cannot be signed on recollection ──────
                // This client was previously constructed with default ClientOptions, so OnDesyncCapture
                // and OnMisattribution were null. A snapshot that fails VerifyIntegrity is discarded and
                // the mirror holds last-good state (NetworkedMatchSession.cs:260-265) — with no log, no
                // toast and no artifact. On screen that is indistinguishable from "a minion vanished",
                // which is exactly how the open rekindle bug presents, and it is why no offline repro can
                // ever reach it: offline there is no wire and no verification step.
                //
                // NOTE: ClientOptions.DevCapture is declared but read NOWHERE in production code (only
                // set in SnapshotApplyTests) — the sole gate on capture is OnDesyncCapture being non-null.
                // It is set here for intent, not effect. Do not assume toggling it changes behaviour.
                //
                // These sinks run on the background receive pump, so they must never throw.
                string captureDir = Path.Combine(Application.persistentDataPath, "desync");
                var clientOptions = new ClientOptions
                {
                    // C-G1-8: the server refuses a fresh socket whose hello carries no device token.
                    // The SIGNED-IN ACCOUNT's token, not the device's — two seats launched from one machine
                    // must arrive as two accounts. MatchGate.PairAsync never compares accounts, so they
                    // already pair; without this they simply both arrive as the same one, indistinguishable
                    // to the server and to telemetry. DeviceIdentity.Token is untouched and is still what
                    // the first account presents (DeviceAccounts) — see that class for why the persistence,
                    // not the generation, is the load-bearing half.
                    DeviceToken = DeviceAccounts.WireToken(),
                    Platform = DeviceIdentity.Platform,
                    BuildId = DeviceIdentity.BuildId,
                    // EF-073: no name. A signed-in seat is named by its identity, which the server
                    // reads off the verified token and ignores this field for (2026-10-18); an
                    // unconfigured dev build has no name to send.
                    DisplayName = "",
                    // EF-074e: the Supabase JWT; never empty on a configured build (checked above). The
                    // server keys the seat on it. Empty only from an unconfigured dev build, which a
                    // server seats on the device token only when ServerOptions.AllowTokenlessMatches is set.
                    AuthToken = authToken,
                    DeckSubmission = selectedDeck,
                    DevCapture = Debug.isDebugBuild,
                    OnDesyncCapture = cap => WriteDesyncCapture(captureDir, cap),
                    OnMisattribution = m => Debug.LogWarning(
                        $"[Desync] MISATTRIBUTION match={m.MatchId} seat={m.Seat} pendingSeq={m.PendingSeq} " +
                        $"reason={m.RejectReason} v{m.StateVersion}"),
                };

                IWireTransport transport = new WebSocketTransport();
                if (Debug.isDebugBuild)
                {
                    // [DesyncDbg] dev tool — see DesyncFaultInjector.
                    _faultInjector = new DesyncFaultInjector(transport);
                    transport = _faultInjector;
                }
                // Locals, not the fields, inside the handler below: a refused attempt's late raises
                // must land in ITS view, which nothing pumps once the next attempt replaces it, and
                // never in the next attempt's.
                var net = new NetworkedMatchSession(transport, clientOptions);
                var view = new MainThreadMatchSession(net);
                _net = net;
                _view = view;

                // While re-queueing after a refusal, the court-full panel tells the story; the status
                // line stays quiet rather than flashing Connecting → Waiting on every attempt.
                bool holdingForSeat = capacityRefusals > 0;

                // RC-F10: marshal terminal transitions to the main thread and stop the runner.
                // The same handler feeds the loading overlay's status line (ConnectionState already
                // narrates the boot: Connecting → Pairing → Live); raises come from the background
                // receive pump, so everything goes through view.Post.
                // Read on the main thread only, after the connect task completes; it decides between
                // "can't reach the server" and "lost the connection" when the close carries no reason.
                bool reachedServer = false;
                net.OnConnectionStateChanged += st => view.Post(() =>
                {
                    if (st == ConnectionState.Pairing || st == ConnectionState.Live) reachedServer = true;
                    switch (st)
                    {
                        case ConnectionState.Connecting: if (!holdingForSeat) SetStatus(Loc.Get("matchmaking_status_connecting")); break;
                        case ConnectionState.Pairing:    if (!holdingForSeat) SetStatus(Loc.Get(challenge ? "matchmaking_status_waiting_friend" : "matchmaking_status_waiting_opponent")); break;
                        case ConnectionState.Live:       SetStatus(Loc.Get("matchmaking_status_opponent_found")); break;
                    }
                    // Not on a capacity refusal: that is always before any match, the boot is about
                    // to re-queue, and a stop requested now would sit armed on the runner (its
                    // _stopRequested) for the match the next attempt starts.
                    if ((st == ConnectionState.Dead || st == ConnectionState.Ended) &&
                        net.DeadReason != DeadReason.ServerFull)
                        matchRunner.StopMatch(net.State == ConnectionState.Ended ? view.Current.Outcome : MatchOutcome.InProgress);
                });

                Debug.Log($"[NetworkedGameBootstrap] connecting to {serverUrl}{(challenge ? " (friend challenge)" : "")} …");
                if (!holdingForSeat) SetStatus(Loc.Get("matchmaking_status_connecting"));
                Task connect;
                try { connect = net.ConnectAsync(new Uri(connectUrl)); }
                catch (Exception ex)
                {
                    Debug.LogError($"[NetworkedGameBootstrap] connect threw: {ex.Message}");
                    BootFailed(ConnectionFailureCopy.Unreachable);
                    yield break;
                }

                // Bound the CONNECTING phase only, never pairing. ConnectAsync does not resolve until
                // the match starts, so a blanket timeout would abandon a legitimate matchmaking queue;
                // once the server has answered and we reach Pairing, the wait is unbounded on purpose.
                //
                // Why this exists: a half-open server — Fly's proxy terminating TLS with no healthy
                // machine behind it — accepts TCP and then answers nothing, so the WebSocket upgrade
                // never completes and ConnectAsync hangs forever. The failure branch below was
                // unreachable, and the player sat on "Connecting…" with no error and nothing logged.
                // That is how a crash-looping server presented as a silent client hang (2026-08-16).
                const float connectTimeoutSeconds = 20f;
                float connectDeadline = Time.realtimeSinceStartup + connectTimeoutSeconds;
                float pairingSince = -1f;
                while (!connect.IsCompleted)
                {
                    ConnectionState now = net.State;
                    if (now == ConnectionState.Connecting && Time.realtimeSinceStartup > connectDeadline)
                    {
                        Debug.LogError($"[NetworkedGameBootstrap] no answer from {serverUrl} after " +
                                       $"{connectTimeoutSeconds:0}s — still Connecting, so the socket opened " +
                                       $"but the server never completed the handshake.");
                        BootFailed(ConnectionFailureCopy.NotResponding);
                        yield break;
                    }
                    // A re-queue the server has not refused within a few seconds is IN the queue (the
                    // capacity refusal comes straight after the hello and deck). Drop the panel and
                    // narrate the wait like anyone else's. The client marks Pairing as soon as it has
                    // SENT the hello, so Pairing alone does not say the server took it.
                    if (holdingForSeat && now == ConnectionState.Pairing)
                    {
                        if (pairingSince < 0f) pairingSince = Time.realtimeSinceStartup;
                        else if (Time.realtimeSinceStartup - pairingSince > CourtFullQueuedAfterSeconds)
                        {
                            holdingForSeat = false;
                            HideCourtFull();
                            SetStatus(Loc.Get("matchmaking_status_waiting_opponent"));
                        }
                    }
                    yield return null;
                }
                if (connect.IsFaulted || net.State != ConnectionState.Live)
                {
                    if (!challenge && net.DeadReason == DeadReason.ServerFull)
                    {
                        // Item 4b: the court is full. Hold the player here and ask again. The jitter
                        // keeps a full box's worth of refused clients from all returning on one tick.
                        capacityRefusals++;
                        float pause = UnityEngine.Random.Range(
                            CourtFullRetryMinSeconds, CourtFullRetryMaxSeconds);
                        Debug.Log($"[NetworkedGameBootstrap] the court is full (refusal {capacityRefusals}); " +
                                  $"re-queueing in {pause:0.0}s.");
                        net.Dispose();
                        ShowCourtFull();
                        float resumeAt = Time.realtimeSinceStartup + pause;
                        while (Time.realtimeSinceStartup < resumeAt) yield return null;
                        continue;
                    }

                    Debug.LogError($"[NetworkedGameBootstrap] failed to reach a live match " +
                                   $"(state={net.State}, dead={net.DeadReason}, detail={net.DeadReasonDetail}, " +
                                   $"fault={connect.Exception?.GetBaseException().Message}).");
                    // #232: DeadReasonDetail is the server's actual WS close-frame reason ("deck
                    // rejected: ...", "server full", ...) when it sent one — falling every refusal
                    // back to the bare DeadReason bucket is exactly the bug that issue fixed.
                    ConnectionFailureCopy copy =
                        ConnectionFailureCopy.For(net.DeadReason, net.DeadReasonDetail, reachedServer);

                    // A contract refusal is usually this build being out of date — a release landed
                    // while the app was open, after the menu's update gate had already passed. Ask the
                    // gate again: if it agrees, the player needs the store, not a Retry that can never
                    // work. If it does not (a build AHEAD of the server), fall through to the reason.
                    if (net.DeadReason == DeadReason.ContractMismatch ||
                        net.DeadReasonDetail == SocialClient.ContractMismatchReason)
                    {
                        Task<UpdateGateResult> gate = UpdateGateOverlay.CheckAsync(force: true);
                        while (!gate.IsCompleted) yield return null;
                        if (gate.Result.Verdict == UpdateGateVerdict.UpdateRequired)
                        {
                            HideCourtFull();
                            UpdateGateOverlay.Show(gate.Result);
                            yield break;
                        }
                    }

                    BootFailed(copy);
                    yield break;
                }

                HideCourtFull();
                break;
            }

            Debug.Log($"[NetworkedGameBootstrap] LIVE — seat {_net.Seat} as {_net.Player}.");
            WireMatch(db);
        }

        private static SubmitDeckMsg ConsumeSelectedDeck(ICardDatabase db, out string playedDeckId)
        {
            playedDeckId = "";
            string chosenDeckId = MatchDeckSelection.Take();
            if (string.IsNullOrEmpty(chosenDeckId))
            {
                Debug.LogWarning("[NetworkedGameBootstrap] no match deck was selected; using the server's prebuilt fallback.");
                return null;
            }

            PlayerAccount account = PlayerAccount.Local;

            DeckList deck = null;
            IReadOnlyList<DeckList> decks = account.Decks();
            for (int i = 0; i < decks.Count; i++)
            {
                if (decks[i].Id == chosenDeckId)
                {
                    deck = decks[i];
                    break;
                }
            }
            if (deck == null)
                throw new InvalidOperationException("The selected deck no longer exists.");

            // WITH ownership — this is the last gate before the socket, and until 2026-09-03 it was
            // the only Check on the client that did not pass one. The roster's own badge (which does)
            // would call these decks drafts, so an L6 failure walked straight past this line and came
            // back as a closed socket reading "deck rejected: You don't own X." A client gate that is
            // laxer than the server's is not a gate.
            QueueCheck queue = QueueEligibility.Check(deck, db, account.OwnedCards());
            if (!queue.IsEligible)
                throw new InvalidOperationException("The selected deck is no longer queue ready: " + queue.Reason);

            // Remembered here, not at the roster's Select/Continue click: this is the one point that
            // is guaranteed to be an actual match start with a re-validated, queue-ready deck (Sam,
            // 2026-08-09) — what Find Game pre-arms its pick from next time.
            account.RecordLastPlayed(deck.Id);
            playedDeckId = deck.Id;

            var lines = new List<DeckCardLine>(deck.Entries.Count);
            for (int i = 0; i < deck.Entries.Count; i++)
            {
                DeckEntry entry = deck.Entries[i];
                lines.Add(new DeckCardLine(entry.CardId, entry.Count));
            }
            // The pairing comes off the DECK now (ADR-0009). It came off the character the deck was
            // bound to, and SubmitDeckMsg carried that character's id alongside it — an id the
            // message's own doc said "is not an identity the server trusts", which is why removing it
            // cost the wire nothing.
            //
            // The deck's back rides with it (#421) as a request: the server shows it only if this
            // account owns it, and the default otherwise, without telling us.
            return new SubmitDeckMsg(deck.PrimaryClass, deck.SecondaryClass, lines, deck.CardBackId);
        }

        private void WireMatch(ICardDatabase db)
        {
            GameState s0 = _view.Current;
            // Crash reports carry the server's match id from here until this scene goes (OnDestroy).
            CrashReportingScope.SetMatch(s0.Id.Value);
            PlayerId seat0Id = s0.Players[0].Id;
            PlayerId seat1Id = s0.Players[1].Id;
            PlayerId localId = _net.Player;
            // THE LOCAL PLAYER RENDERS AT THE BOTTOM (visual index 0) regardless of server
            // seat — found live at b3 §14 step 4: a seat-1 local otherwise sees the board
            // upside down (their own un-redacted hand rendered as the "enemy" hand). Seat
            // indices stay engine-truth for the runner; only presentation is local-first.
            PlayerId remoteId = localId == seat0Id ? seat1Id : seat0Id;

            // Hero classes ride the projected snapshot (state-sync §5.2 PUBLIC) — never a local
            // MatchSetup, which this path does not have. Resolved once here and reused by both the
            // versus screen and the hero plates below.
            CardClass localClass  = s0.Players[0].Id == localId ? s0.Players[0].Hero.Class : s0.Players[1].Hero.Class;
            CardClass remoteClass = s0.Players[0].Id == localId ? s0.Players[1].Hero.Class : s0.Players[0].Hero.Class;

#if UNITY_EDITOR
            IArtProvider artProvider = new EditorArtProvider();
#else
            IArtProvider artProvider = new AddressableArtProvider();
#endif

            // ── Versus screen (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/versus-screen.md §1) ───────────────────────
            // THIS is the moment the screen was specified for: match_start has landed, so the two
            // classes are known, and the board does not exist yet. Everything before this point is
            // the unbounded pairing wait, which is the boot plate's job and why that plate cannot
            // simply be replaced here. The plate is released now and cross-fades out underneath.
            //
            // Orb variant keys off the MATCH ID, not a local roll, so both clients see the same one
            // (spec §4.2) — s0.Id is the server's id, identical on both seats by construction.
            // Nameplates: OURS is this device's real display name (spec §6) — PlayerAccount reads
            // the local profile, falling back to "You" when none was ever set. THEIRS is now the
            // opponent's real name too (EF-073): it rides their HelloMsg, and the server re-runs
            // PlayerName.Check on arrival before putting it in OpponentInfo, substituting the seat
            // id if it fails. So this string is PEER-AUTHORED and safe only because of that
            // server-side check — see NetworkedMatchSession.OpponentName and VULN-002 §4.
            //
            // "Opponent" below is still ours to draw when the server sent nothing usable; it is not
            // a name the peer can claim, because the server reserves it.
            _versus = VersusOverlay.Create(Camera.main, VersusOverlay.Data.For(
                localClass,  AuthGateway.DisplayNameOr(Loc.Get("common_you")),
                remoteClass, string.IsNullOrEmpty(_net.OpponentName) ? Loc.Get("common_opponent") : _net.OpponentName,
                s0.Id.Value), artProvider);
            // The two heroes trade a line over the versus screen and on into the mulligan.
            VersusBanter.Create(Camera.main, localClass, remoteClass);
            // Carry the narration across the handover rather than letting the line blink empty —
            // "Opponent found" was posted to the plate a frame ago and is still the true state.
            SetStatus(Loc.Get("matchmaking_status_opponent_found"));
            if (_overlay != null) _overlay.NotifyMatchReady();

            // ── Intent source for OUR seat; the opponent seat is inert (NullIntentSource). ────────
            IIntentSource mine;
            if (inputRouter != null)
            {
                var hotSeat = new HotSeatInput(localId);
                inputRouter.Init(localId, hotSeat, db, new CommandProcessor(db));
                mine = hotSeat;
            }
            else
            {
                mine = new PlayingAi(localId, db);
            }
            IIntentSource theirs = new NullIntentSource();
            IIntentSource s0Source = _net.Seat == 0 ? mine : theirs;
            IIntentSource s1Source = _net.Seat == 1 ? mine : theirs;

            // ── Board visuals — local player at the bottom ────────────────────────
            if (boardController != null && boardAnchors != null)
            {
                boardAnchors.SetPlayers(localId, remoteId);
                boardController.Initialize(db, boardAnchors, localId, remoteId, inputRouter, matchRunner,
                                           localHumanId: localId);
                // Each seat's card back (#421), by ENGINE seat, before the opening hand is drawn.
                boardController.SetCardBack(seat0Id, _net.CardBackOf(0));
                boardController.SetCardBack(seat1Id, _net.CardBackOf(1));
                matchRunner.OnStateUpdated += boardController.OnStateUpdated;
                MatchOpeningDeal opening = MatchOpeningDeal.From(s0, localId);
                if (opening != null && inputRouter != null && Camera.main != null)
                {
                    // Cosmetic, so it may never cost the match: a throw here used to abort the rest
                    // of this wiring, and the player sat on the versus screen forever (0.0.10).
                    try
                    {
                        _coinOpening = SparkCoinOpening.Create(transform, boardController, boardAnchors,
                            db, artProvider, opening, localId);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[NetworkedGameBootstrap] The Spark opening failed; skipping it. {ex}");
                        if (_coinOpening != null) Destroy(_coinOpening.gameObject);
                        _coinOpening = null;
                    }
                    if (_coinOpening != null)
                    {
                        _openingActive = true;
                        inputRouter.OpeningBlocksInput = true;
                        boardController.BeginOpening(opening);
                    }
                }
            }

            var ongoingEffects = OngoingEffectsView.Create(localId, db);
            matchRunner.OnStateUpdated += ongoingEffects.OnStateUpdated;

            // Card-authored SFX are presentation-only: they read the same paced snapshots/events
            // as VFX and never enter authority state or the wire protocol.
            // TryGetComponent, not `??` — see CardSoundDirector.Init: `??` is the C# null check
            // and bypasses UnityEngine.Object's overloaded `==`, so a fake-null reads as non-null
            // and AddComponent never runs (Sam, 2026-08-27).
            if (!TryGetComponent(out CardSoundDirector cardSounds))
                cardSounds = gameObject.AddComponent<CardSoundDirector>();
            cardSounds.Init(db);
            matchRunner.OnStateUpdated += cardSounds.OnStateUpdated;
            matchRunner.OnMatchEvent += cardSounds.OnMatchEvent;

            // Hero voice, keyed on the LOCAL hero's class. Its own AudioSource and its own
            // GameObject, so a voice line and a card sound overlap rather than cutting each other
            // off — PlayOneShot mixes on one source, but the two directors' lifetimes and volumes
            // want to stay separable. OnTurnTimer and OnMatchEnded are wired further down, beside
            // the turn clock and the end banner that already consume those signals.
            var voiceGo = new GameObject("HeroVoice");
            voiceGo.transform.SetParent(transform, false);
            var heroVoice = voiceGo.AddComponent<HeroVoiceDirector>();
            heroVoice.Init(localClass, localId, _net.Seat);
            matchRunner.OnStateUpdated += heroVoice.OnStateUpdated;
            matchRunner.OnMatchEvent += heroVoice.OnMatchEvent;

            // The quest baseline, read before anything in this match can move it; the result
            // screen reads again at the end and shows the difference.
            _questWatch.Begin();
            _goldWatch.Begin();

            // ── Input router: TRUTH cadence, main-thread-marshaled via the decorator. ────────────
            if (inputRouter != null)
            {
                // §14 step 4 diagnostic: log every snapshot the MIRROR applies. If these lines
                // stop while the server/bot advances, the apply path is dropping snapshots.
                _view.OnSnapshot += s => Debug.Log(
                    $"[Net] snap v{s.StateVersion} phase={s.Phase} active={s.ActivePlayer} " +
                    $"mana={s.Players[0].Mana.Available}/{s.Players[1].Mana.Available}");
                _view.OnSnapshot += inputRouter.OnStateUpdated;
                inputRouter.OnStateUpdated(_view.Current);
                // ...and the playback cadence beside it, so a turn cannot be acted on before it
                // has been shown.
                matchRunner.OnStateUpdated += inputRouter.OnPresentedStateUpdated;
                if (proceduralBoardSetup != null)
                    proceduralBoardSetup.SetupClickables(localId, remoteId, inputRouter);
            }

            // ── Mana trays + deck piles — local at visual index 0 ─────────────────
            RoyalCouncilBoard table = boardAnchors != null ? boardAnchors.Tabletop : null;
            if (table != null)
            {
                table.Bind(localId, remoteId, inputRouter);
                matchRunner.OnStateUpdated += table.OnStateUpdated;
                matchRunner.OnMatchEvent += table.OnMatchEvent;
                _view.OnSnapshot += table.OnAuthorityStateUpdated;
                table.OnAuthorityStateUpdated(_view.Current);
                _view.OnTurnTimer += table.OnTurnTimer;
            }
            if (boardAnchors != null && table == null)
            {
                CreateManaTray(localId, boardAnchors);
                CreateManaTray(remoteId, boardAnchors);
                CreateDeckPile(localId, boardAnchors);
                CreateDeckPile(remoteId, boardAnchors);
            }

            // ── Hero plates — class rides the projected snapshot (state-sync §5.2 PUBLIC), no MatchSetup. ─
            if (boardAnchors != null)
            {
                var localPlate  = CreateHeroPlate(localId, localClass, boardAnchors, db, artProvider);
                var remotePlate = CreateHeroPlate(remoteId, remoteClass, boardAnchors, db, artProvider);
                // Selection/target cues track OUR seat (also closes the missing
                // hero-as-attacker gold cue in networked mode).
                if (inputRouter != null)
                    inputRouter.SetHeroPlates(localPlate, remotePlate);
                boardController?.SetHeroPlates(localId, localPlate, remoteId, remotePlate);
            }

            // ── End-turn button (was hot-seat-only; found missing at b3 §14 step 4) ──
            if (inputRouter != null && table == null)
            {
                var endTurnGo = new GameObject("EndTurnButton");
                endTurnGo.transform.position = EndTurnButtonView.BoardPosition;
                BoardBackdrop.RegisterArtworkAnchor(
                    endTurnGo.transform, EndTurnButtonView.ArtworkUv);
                var endTurn = endTurnGo.AddComponent<EndTurnButtonView>();
                endTurn.Init(localId, inputRouter);
                matchRunner.OnStateUpdated += endTurn.OnStateUpdated;
            }

            // ── Visible turn clock (E2 minimal slice; C-G1-2a) ──────────────────────
            // Binds to the seam's OnTurnTimer (main-thread-marshaled by MainThreadMatchSession).
            // Server-driven: the turn clock is a server timer, so it exists only on this path.
            // (It is why the deleted in-process hot-seat boot never had one.) The Royal Council
            // board gives the clock its own crown-fuse medallion on the left.
            if (table == null)
            {
                var clockGo = new GameObject("TurnClock");
                clockGo.transform.position = TurnClockView.BoardPosition;
                BoardBackdrop.RegisterArtworkAnchor(clockGo.transform, TurnClockView.ArtworkUv);
                var turnClock = clockGo.AddComponent<TurnClockView>();
                turnClock.Init();
                _view.OnTurnTimer += turnClock.OnTurnTimer;
                matchRunner.OnStateUpdated += turnClock.OnStateUpdated;
            }
            // Same signal, same marshaling: the rope LINE lands when the clock goes red.
            _view.OnTurnTimer += heroVoice.OnTurnTimer;

            // ── Mulligan confirm button (E2 minimal slice; C-G1-2b) ─────────────────
            // Visible only during the Mulligan phase; the human tosses cards via InputRouter
            // (red X markers) and this commits. Truth cadence (_view.OnSnapshot, like the input
            // router at :233) so visibility never lags the authority.
            if (inputRouter != null)
            {
                var mulliganGo = new GameObject("MulliganConfirmButton");
                mulliganGo.transform.position = MulliganConfirmButtonView.BoardPosition;
                var mulligan = mulliganGo.AddComponent<MulliganConfirmButtonView>();
                _mulligan = mulligan;
                mulligan.Init(localId, inputRouter);
                _view.OnSnapshot += mulligan.OnStateUpdated;
                mulligan.OnStateUpdated(_view.Current);
            }

            // ── Unearth choice (snapshot truth, reconnect-safe) ──────────────────────
            // Definition ids render through the board's detached real-card face. The viewer-safe
            // model preserves chooser order/sequence and turns the opponent projection into a
            // count-free passive state. InputRouter independently gates every non-choice intent.
            if (inputRouter != null && boardController != null)
            {
                var unearthGo = new GameObject("UnearthChoice");
                var unearth = unearthGo.AddComponent<UnearthChoiceView>();
                unearth.Init(localId, inputRouter, db, boardController, artProvider);
                _view.OnSnapshot += unearth.OnStateUpdated;
                unearth.OnStateUpdated(_view.Current);
            }

            // ── Settings overlay + concede (C-G1-3 / decision 23 §5) ──────────────
            // UI Toolkit meta-surface over the world-space board: a corner button opening a modal
            // whose confirm forwards to InputRouter.OnConcedeClicked. Assets load from Resources so
            // there is no scene wiring. IsBlockingInput gates board clicks (UITK won't block physics).
            if (inputRouter != null)
            {
                var settings = MatchSettingsController.Mount(inputRouter.OnConcedeClicked,
                                                              showConcede: true, allowSceneReload: false);
                // The King's commission card (#421). Shows nothing until the server sends one, which
                // it never does in a friend match. Its chip form opens a card that gates the board.
                var commission = CommissionCardView.Mount(_net);
                _view.OnCommission += commission.OnCommission;
                inputRouter.UiBlocksInput = () => settings.IsBlockingInput || commission.IsBlockingInput;
            }

            // Rejections have NO player-facing surface, by decision (#268, 2026-08-26): the
            // world-space toast stack that used to live here was unreadable at device scale and
            // only restated what the absent effect already says. Rejects still reach
            // HotSeatInput.OnRejected, which logs them for diagnosis.

            // ── VFX ───────────────────────────────────────────────────────────────
            if (vfxDirector != null)
            {
                matchRunner.OnMatchEvent   += vfxDirector.OnMatchEvent;
                matchRunner.OnStateUpdated += vfxDirector.OnStateUpdated;

                // Play reveal — THE SAME FOUR LINES GameBootstrap RUNS. They were missing here,
                // which is the whole reason the beat never showed up in a real game: this is the
                // bootstrap the shipped build uses (KingdomBuild ships BoardNetworked.unity and
                // nothing else) and the one Find Game loads. Board.unity/GameBootstrap — Single
                // Player only — had the beat; every other way into a match had none, and the
                // absence is invisible from the outside, because an entry with no hold just looks
                // like ordinary commandDelaySeconds pacing (Sam, 2026-08-19).
                //
                // Init() is what makes the reveal a real card face rather than nothing at all:
                // without it _board is null and ShowPlayReveal returns silently. It has to follow
                // boardController.Initialize above, which it does.
                //
                // RETIREMENT TRIGGER: a third bootstrap, or match wiring lifted into something
                // shared — this block moves with it, or that path loses the beat exactly the way
                // this one did.
                vfxDirector.Init(artProvider, boardController);
                PlayRevealHolds holds = vfxDirector.RevealHolds;
                matchRunner.PlayRevealClassifier = (snapshot, events) =>
                    PlayRevealClassifier.Classify(snapshot, events, db, holds, viewer: localId);
                matchRunner.OnPlayReveal += vfxDirector.ShowPlayReveal;
                // #236: hide the hand copy for the beat's duration, or it and the flying reveal
                // both read on screen at once. See BoardController.OnPlayReveal.
                matchRunner.OnPlayReveal += boardController.OnPlayReveal;

                // A hero power's cast rides the same hold-then-apply beat as the reveal, and is
                // covered by the retirement trigger above for the same reason.
                HeroPowerHolds powerHolds = vfxDirector.HeroPowerHolds;
                matchRunner.HeroPowerBeatClassifier = (snapshot, events) =>
                    HeroPowerBeatClassifier.Classify(snapshot, events, powerHolds);
                matchRunner.OnHeroPowerBeat += vfxDirector.ShowHeroPowerBeat;
            }

            matchRunner.OnMatchEnded += outcome =>
            {
                Debug.Log($"[NetworkedMatch] OVER — {outcome}");
                heroVoice.OnMatchEnded(outcome);                    // picks the win, lose or draw line
                // The banner's sequence owns the timing from here: stinger, spoils, the voice
                // line, then the linger. The Gold and quest reads start now so they run under the music.
                MatchEndBanner.Show(outcome, localSeat: _net.Seat,  // seat-aware — the server deals the seat
                    quests: _questWatch.AdvancedAsync(),
                    playVoiceLine: heroVoice.PlayMatchEndLine,
                    // The MatchEndMsg follows the final snapshot, so its reason and findings beat the drain.
                    reason: _net.EndReason,
                    findings: _net.EndFindings,
                    gold: _goldWatch.EarnedAsync(),
                    rematchDeckId: _rematchDeckId,
                    // The same message carries the King's final (#421); null in a match for no King.
                    commission: _net.EndCommission);
                MusicDirector.FadeOutUntilSceneChange();            // the battle loop would clash with the stinger
                cardSounds.OnMatchEnded(outcome, _net.Seat);
                _ = PlayGamesServices.SyncAchievementsAfterMatchAsync();   // never throws; nothing waits on it
            };

            // ── §14 step 4 diagnostic (v9 frost retest): playback-side twin of the [Net] snap
            // trail. Diffing the two trails finds where playback falls behind the mirror — a
            // healthy [Net] trail with no matching [Playback] advance IS the frost signature. ──
            matchRunner.OnStateUpdated += s => Debug.Log(
                $"[Playback] v{s.StateVersion} phase={s.Phase} active={s.ActivePlayer}");

            // ── Loading overlay fade: first released snapshot = the board is drawn. Subscribed
            // BEFORE StartMatch (the instant initial entry fires synchronously inside it). ──────
            if (_overlay != null)
                matchRunner.OnStateUpdated += _ => _overlay.NotifyMatchReady();
            // Same signal for the versus screen, which additionally holds its own minimum dwell —
            // the board is ready long before a player has read who they are fighting.
            if (_versus != null)
                matchRunner.OnStateUpdated += _ => _versus.NotifyMatchReady();
            // The funnel's board_playable: the same first released snapshot, once per match.
            string funnelMatchId = s0.Id.Value;
            bool boardPlayableRecorded = false;
            matchRunner.OnStateUpdated += _ =>
            {
                if (boardPlayableRecorded) return;
                boardPlayableRecorded = true;
                FirstSessionFunnel.BoardPlayable(funnelMatchId);
            };

            // ── Drive the runner: ONE seat owned; the same marshaled session sits at both indices. ─
            matchRunner.StartMatch(
                _view, s0Source,
                _view, s1Source,
                PresentationViewer.FixedSeat0,
                SeatOwnership.Single(_net.Seat));
            if (_coinOpening != null) StartCoroutine(ShowOpeningCoin(localId));
        }

        private IEnumerator ShowOpeningCoin(PlayerId local)
        {
            bool Pending() => _net.State == ConnectionState.Live && MatchOpeningDeal.IsPending(_view.Current, local);
            try
            {
                // Let the existing versus beat finish first. Gameplay snapshots keep flowing;
                // a timeout, concession or resumed game cancels the cosmetic opening immediately.
                while (_versus != null && Pending()) yield return null;
                if (Pending() && _coinOpening != null) yield return _coinOpening.Play(Pending);
            }
            finally { ReleaseOpening(); }
        }

        private void ReleaseOpening()
        {
            if (!_openingActive) return;
            _openingActive = false;
            if (inputRouter != null) inputRouter.OpeningBlocksInput = false;
            if (boardController != null) boardController.EndOpening(isActiveAndEnabled && boardController.isActiveAndEnabled);
            if (_mulligan != null && _view != null) _mulligan.OnStateUpdated(_view.Current);
            if (_coinOpening != null)
            {
                _coinOpening.gameObject.SetActive(false);
                Destroy(_coinOpening.gameObject);
            }
            _coinOpening = null;
        }

        private HeroPlateView CreateHeroPlate(PlayerId playerId, CardClass heroClass,
                                              BoardAnchors anchors, ICardDatabase db, IArtProvider artProvider)
        {
            Transform heroAnchor = anchors.HeroAnchor(playerId);
            if (heroAnchor == null) return null;
            var go   = new GameObject($"HeroPlate_{playerId}");
            var view = go.AddComponent<HeroPlateView>();
            view.Init(playerId, heroAnchor, artProvider, heroClass, anchors.IsLocalPlayer(playerId));
            matchRunner.OnStateUpdated += view.OnStateUpdated;

            CreateHeroSockets(playerId, anchors, db, artProvider);
            return view;
        }

        /// <summary>The forged hero-power and weapon frames across the portrait's lower edge.
        /// Separate from the plate because the weapon half comes and goes with the equipped
        /// weapon.</summary>
        private void CreateHeroSockets(PlayerId playerId, BoardAnchors anchors,
                                       ICardDatabase db, IArtProvider artProvider)
        {
            Transform power  = anchors.HeroPowerAnchor(playerId);
            Transform weapon = anchors.WeaponAnchor(playerId);
            if (power == null && weapon == null) return;

            var go   = new GameObject($"HeroSockets_{playerId}");
            var view = go.AddComponent<HeroSocketsView>();
            view.Init(playerId, power, weapon, db, artProvider);
            // boardController.Initialize ran above, so its reveal controller exists by now.
            if (boardController != null) view.AttachReveal(boardController.RevealController);
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private void CreateManaTray(PlayerId playerId, BoardAnchors anchors)
        {
            Transform anchor = anchors.ManaTrayAnchor(playerId);
            if (anchor == null) return;
            var go   = new GameObject($"ManaTray_{playerId}");
            var view = go.AddComponent<ManaTrayView>();
            view.Init(playerId, anchor, anchors.IsLocalPlayer(playerId));
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private void CreateDeckPile(PlayerId playerId, BoardAnchors anchors)
        {
            Transform anchor = anchors.AnchorFor(playerId, Core.Contracts.Zone.Deck, 0);
            if (anchor == null) return;
            var go   = new GameObject($"DeckPile_{playerId}");
            var view = go.AddComponent<DeckPileView>();
            view.Init(playerId, anchor, anchors.IsLocalPlayer(playerId));
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        /// <summary>
        /// Boot narration goes to whichever screen is currently in front of the player: the boot
        /// plate during connect/pair, the versus screen once it exists. Both own a status line in
        /// the same canvas slot (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/versus-screen.md §4 takes the loading plate's slot verbatim), so
        /// the line does not appear to move across the handover. Writing to both would double it
        /// during the cross-fade, hence the else.
        /// </summary>
        private void SetStatus(string text)
        {
            if (_versus != null) _versus.SetStatus(text);
            else if (_overlay != null) _overlay.SetStatus(text);
        }

        /// <summary>Terminal boot failure. Whichever screen is up stays up — neither ever fades on
        /// a failure, so the player is never dropped onto an empty board — and the recovery panel
        /// (#232: Retry and Back, where kill-the-app used to be the only exit) renders over it.
        /// Only the panel states the reason; the overlay's status line is cleared, because saying
        /// it on both read as the same error twice.</summary>
        private void BootFailed(string plain) => BootFailed(new ConnectionFailureCopy(plain, ""));

        private void BootFailed(ConnectionFailureCopy copy)
        {
            HideCourtFull();
            if (_versus != null) _versus.NotifyBootFailed("");
            else if (_overlay != null) _overlay.NotifyBootFailed("");

            var uiGo = new GameObject("ConnectionFailedUI");
            var doc = uiGo.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("ConnectionFailedScreen");
            uiGo.AddComponent<ConnectionFailedOverlay>().Init(copy);
        }

        /// <summary>
        /// Item 4b: the server is at its session cap and the boot is waiting to re-queue. Built from
        /// the boot-failure panel's tree, as <see cref="UpdateGateOverlay"/> is: the Herald's line in
        /// place of a joke, no Retry (retrying is automatic), and Back relabelled "Leave the queue".
        /// Back keeps its emb-back class, so Android back leaves too. Idempotent across refusals.
        ///
        /// <para>Text, not a voice line: the Herald's clips are recorded assets and none says the
        /// court is full. The loading plate's status line is cleared for the same reason
        /// <see cref="BootFailed(ConnectionFailureCopy)"/> clears it — the panel says it once.</para>
        /// </summary>
        private void ShowCourtFull()
        {
            if (_overlay != null) _overlay.SetStatus("");
            if (_courtFull != null) return;

            _courtFull = new GameObject("CourtFullUI");
            var doc = _courtFull.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("ConnectionFailedScreen");
            VisualElement root = doc.rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[NetworkedGameBootstrap] court-full panel has no rootVisualElement.");
                return;
            }
            UiLocalizer.Attach(root);

            var title = root.Q<Label>("title");
            if (title != null) title.text = Loc.Get("connection_court_full_title");

            var reason = root.Q<Label>("reason");
            if (reason != null)
            {
                reason.text = Loc.Get("connection_court_full_herald");
                reason.style.display = DisplayStyle.Flex;
            }

            root.Q<Button>("btn-retry")?.RemoveFromHierarchy();

            var leave = root.Q<Button>("btn-back");
            if (leave != null)
            {
                leave.text = Loc.Get("connection_court_full_leave");
                // The scene change ends Boot() and OnDestroy disposes the attempt in flight, which
                // closes its socket — so leaving here leaves the server's queue too.
                leave.RegisterCallback<ClickEvent>(_ => ScreenNav.Go(ScreenNav.MainMenu));
            }
        }

        private void HideCourtFull()
        {
            if (_courtFull == null) return;
            Destroy(_courtFull);
            _courtFull = null;
        }

        private void OnDestroy()
        {
            CrashReportingScope.ClearMatch();
            ReleaseOpening();
            _questWatch.Cancel();
            _goldWatch.Cancel();
            _net?.Dispose();
        }

        private static ICardDatabase LoadCardDatabase()
        {
            return CardDatabaseLoader.Load();
        }

        /// <summary>
        /// C-G1-4 desync sink. A rejected snapshot leaves an artifact instead of a silence: a loud
        /// console line (always) plus the raw frame and a readable sidecar on disk (best-effort).
        /// The wire-vs-local hash pair is the whole diagnosis — they disagree, and the sidecar says
        /// by how much and at which state version.
        ///
        /// Runs on the background receive pump, so it swallows every write failure: failing to
        /// RECORD a desync must never also break the match that is still playable.
        /// </summary>
        private static void WriteDesyncCapture(string dir, DesyncCapture cap)
        {
            // Unconditional and first — if the disk write dies, this line is still the evidence.
            Debug.LogError(
                $"[Desync] SNAPSHOT REJECTED, mirror held — match={cap.MatchId} seat={cap.Seat} " +
                $"v{cap.StateVersion} envSeq={cap.EnvelopeSeq} wireHash={cap.WireHash} localHash={cap.LocalHash} " +
                $"frameBytes={cap.RawFrame?.Length ?? 0}");

            try
            {
                Directory.CreateDirectory(dir);
                string stem = Path.Combine(
                    dir, $"desync_{cap.MatchId}_s{cap.Seat}_v{cap.StateVersion}_e{cap.EnvelopeSeq}");

                File.WriteAllBytes(stem + ".frame", cap.RawFrame ?? Array.Empty<byte>());
                File.WriteAllText(stem + ".txt",
                    $"matchId       = {cap.MatchId}\n" +
                    $"seat          = {cap.Seat}\n" +
                    $"stateVersion  = {cap.StateVersion}\n" +
                    $"envelopeSeq   = {cap.EnvelopeSeq}\n" +
                    $"wireHash      = {cap.WireHash}\n" +
                    $"localHash     = {cap.LocalHash}\n" +
                    $"rawFrameBytes = {cap.RawFrame?.Length ?? 0}\n");

                Debug.LogError($"[Desync] capture written → {stem}.frame / .txt");
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[Desync] capture WRITE FAILED ({ex.GetType().Name}: {ex.Message}) — " +
                    "the console line above is the only record of this rejection.");
            }
        }

        /// <summary>
        /// Main-thread marshaling decorator: subscribes to the background-pump
        /// <see cref="NetworkedMatchSession"/> and re-fires its events on the Unity main thread via
        /// <see cref="Pump"/> (called from <see cref="NetworkedGameBootstrap.Update"/>). Ordering
        /// (OnSnapshot before OnEvents for a version) is preserved by the FIFO queue.
        /// <see cref="Current"/>/<see cref="StateVersion"/> pass straight through (thread-safe reads),
        /// so the runner's TRUTH reads are never stale; only the paced PRESENTATION raises are queued.
        /// </summary>
        private sealed class MainThreadMatchSession : IMatchSession
        {
            private readonly NetworkedMatchSession _inner;
            private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

            public MainThreadMatchSession(NetworkedMatchSession inner)
            {
                _inner = inner;
                _inner.OnSnapshot  += st => _mainThread.Enqueue(() => OnSnapshot?.Invoke(st));
                _inner.OnEvents    += batch => _mainThread.Enqueue(() => OnEvents?.Invoke(batch));
                _inner.OnTurnTimer += t => _mainThread.Enqueue(() => OnTurnTimer?.Invoke(t));
                _inner.OnCommission += c => _mainThread.Enqueue(() => OnCommission?.Invoke(c));
            }

            /// <summary>Queue an action to run on the main thread (drained by <see cref="Pump"/>).</summary>
            public void Post(Action a) => _mainThread.Enqueue(a);

            public void Pump()
            {
                while (_mainThread.TryDequeue(out Action a)) a();
            }

            public ulong StateVersion => _inner.StateVersion;
            public GameState Current => _inner.Current;
            public Task<CommandResult> SubmitAsync(PlayerIntent intent) => _inner.SubmitAsync(intent);
            public event Action<GameState> OnSnapshot;
            public event GameEventStream OnEvents;
            public event Action<TurnTimer> OnTurnTimer;
            /// <summary>Not on <see cref="IMatchSession"/>: the King's card is a networked match's
            /// alone, so it is marshalled here beside the rest and read off this concrete type.</summary>
            public event Action<CommissionProgress> OnCommission;
        }
    }
}
