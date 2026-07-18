using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Emberfall.AI;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Net.Client;
using Emberfall.Session;
using UnityEngine;
using Action = System.Action;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The NETWORKED scene entry point (b3-networked-session-design.md §3.1 / F4). Sibling to
    /// <see cref="GameBootstrap"/>; the hot-seat boot is left untouched and green (the kickoff's C1
    /// entry requirement — hot-seat-green before any networked playtest — depends on that).
    ///
    /// Three things this does that the local boot cannot:
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
    /// only truth source is the mirror, which a dead socket freezes forever, so death must be pushed
    /// in from outside.
    /// </summary>
    public sealed class NetworkedGameBootstrap : MonoBehaviour
    {
        [SerializeField] private MatchRunner matchRunner;

        [Header("Server")]
        [Tooltip("wss endpoint. The live droplet is wss://emberfall.outofcontext.to/ws; a local " +
                 "dev server is ws://localhost:8080/ws (Emberfall/Net/Start Local Server).")]
        [SerializeField] private string serverUrl = "wss://emberfall.outofcontext.to/ws";

        [Header("Board (optional, null = console-only)")]
        [SerializeField] private BoardAnchors boardAnchors;
        [SerializeField] private BoardController boardController;

        [Header("Input (null = spectate; a networked client normally drives its own seat)")]
        [SerializeField] private InputRouter inputRouter;
        [SerializeField] private ProceduralBoardSetup proceduralBoardSetup;

        [Header("VFX (null = disabled)")]
        [SerializeField] private VfxDirector vfxDirector;

        private NetworkedMatchSession _net;
        private MainThreadMatchSession _view;

        private void Start()
        {
            if (matchRunner == null)
            {
                Debug.LogError("[NetworkedGameBootstrap] matchRunner is not assigned.");
                return;
            }
            StartCoroutine(Boot());
        }

        private void Update() => _view?.Pump();

        private IEnumerator Boot()
        {
            ICardDatabase db = LoadCardDatabase();

            _net = new NetworkedMatchSession(new WebSocketTransport());
            _view = new MainThreadMatchSession(_net);

            // RC-F10: marshal terminal transitions to the main thread and stop the runner.
            _net.OnConnectionStateChanged += st => _view.Post(() =>
            {
                if (st == ConnectionState.Dead || st == ConnectionState.Ended)
                    matchRunner.StopMatch(_net.State == ConnectionState.Ended ? _view.Current.Outcome : MatchOutcome.InProgress);
            });

            Debug.Log($"[NetworkedGameBootstrap] connecting to {serverUrl} …");
            Task connect;
            try { connect = _net.ConnectAsync(new Uri(serverUrl)); }
            catch (Exception ex) { Debug.LogError($"[NetworkedGameBootstrap] connect threw: {ex.Message}"); yield break; }

            while (!connect.IsCompleted) yield return null;
            if (connect.IsFaulted || _net.State != ConnectionState.Live)
            {
                Debug.LogError($"[NetworkedGameBootstrap] failed to reach a live match " +
                               $"(state={_net.State}, dead={_net.DeadReason}, fault={connect.Exception?.GetBaseException().Message}).");
                yield break;
            }

            Debug.Log($"[NetworkedGameBootstrap] LIVE — seat {_net.Seat} as {_net.Player}.");
            WireMatch(db);
        }

        private void WireMatch(ICardDatabase db)
        {
            GameState s0 = _view.Current;
            PlayerId seat0Id = s0.Players[0].Id;
            PlayerId seat1Id = s0.Players[1].Id;
            PlayerId localId = _net.Player;

            // ── Intent source for OUR seat; the opponent seat is inert (NullIntentSource). ────────
            IIntentSource mine;
            HotSeatInput hotSeat = null;
            if (inputRouter != null)
            {
                hotSeat = new HotSeatInput(localId);
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

            // ── Board visuals ─────────────────────────────────────────────────────
            if (boardController != null && boardAnchors != null)
            {
                boardAnchors.SetPlayers(seat0Id, seat1Id);
                boardController.Initialize(db, boardAnchors, seat0Id, seat1Id, inputRouter, matchRunner);
                matchRunner.OnStateUpdated += boardController.OnStateUpdated;
            }

            // ── Input router: TRUTH cadence, main-thread-marshaled via the decorator. ────────────
            if (inputRouter != null)
            {
                _view.OnSnapshot += inputRouter.OnStateUpdated;
                inputRouter.OnStateUpdated(_view.Current);
                if (proceduralBoardSetup != null)
                    proceduralBoardSetup.SetupClickables(seat0Id, seat1Id, inputRouter);
            }

            // ── Mana trays ────────────────────────────────────────────────────────
            if (boardAnchors != null)
            {
                CreateManaTray(seat0Id, 0, boardAnchors);
                CreateManaTray(seat1Id, 1, boardAnchors);
            }

            // ── Hero plates — class rides the projected snapshot (state-sync §5.2 PUBLIC), no MatchSetup. ─
            if (boardAnchors != null)
            {
#if UNITY_EDITOR
                IArtProvider heroArt = new EditorArtProvider();
#else
                IArtProvider heroArt = new AddressableArtProvider();
#endif
                CreateHeroPlate(seat0Id, 0, s0.Players[0].Hero.Class, boardAnchors, db, heroArt);
                CreateHeroPlate(seat1Id, 1, s0.Players[1].Hero.Class, boardAnchors, db, heroArt);
            }

            // ── Reject toasts (design-system.md §6.4/§6.5) — server rejects (incl.
            // RateLimited from the B1 policy gate) arrive on the same OnRejected path. ─
            if (hotSeat != null)
            {
                var toastGo = new GameObject("RejectToasts");
                toastGo.transform.position = new Vector3(5.6f, -1.5f, 0f);
                var toasts = toastGo.AddComponent<RejectToastView>();
                hotSeat.Rejected += toasts.Show;
            }

            // ── VFX ───────────────────────────────────────────────────────────────
            if (vfxDirector != null)
            {
                matchRunner.OnMatchEvent   += vfxDirector.OnMatchEvent;
                matchRunner.OnStateUpdated += vfxDirector.OnStateUpdated;
            }

            matchRunner.OnMatchEnded += outcome =>
            {
                Debug.Log($"[NetworkedMatch] OVER — {outcome}");
                MatchEndBanner.Show(outcome, localSeat: _net.Seat); // seat-aware, unlike the hot-seat boot
            };

            // ── Drive the runner: ONE seat owned; the same marshaled session sits at both indices. ─
            matchRunner.StartMatch(
                _view, s0Source,
                _view, s1Source,
                PresentationViewer.FixedSeat0,
                SeatOwnership.Single(_net.Seat));
        }

        private void CreateHeroPlate(PlayerId playerId, int seatIndex, CardClass heroClass,
                                     BoardAnchors anchors, ICardDatabase db, IArtProvider artProvider)
        {
            Transform heroAnchor = anchors.HeroAnchor(playerId);
            if (heroAnchor == null) return;
            var go   = new GameObject($"HeroPlate_{playerId}");
            var view = go.AddComponent<HeroPlateView>();
            view.Init(seatIndex, heroAnchor, anchors.HeroPowerAnchor(playerId), db, artProvider, heroClass);
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private void CreateManaTray(PlayerId playerId, int seatIndex, BoardAnchors anchors)
        {
            Transform anchor = anchors.ManaTrayAnchor(playerId);
            if (anchor == null) return;
            var go   = new GameObject($"ManaTray_{playerId}");
            var view = go.AddComponent<ManaTrayView>();
            view.Init(seatIndex, anchor);
            matchRunner.OnStateUpdated += view.OnStateUpdated;
        }

        private void OnDestroy() => _net?.Dispose();

        private static ICardDatabase LoadCardDatabase()
        {
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<TextAsset>("cards/M1.cards");
            TextAsset asset = handle.WaitForCompletion();
            if (asset == null)
                throw new Exception("cards/M1.cards not found in Addressables — run Emberfall/Addressables/Setup Groups + Migrate Card Data");
            return new JsonCardDatabase(asset.text);
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
                _inner.OnSnapshot += st => _mainThread.Enqueue(() => OnSnapshot?.Invoke(st));
                _inner.OnEvents   += batch => _mainThread.Enqueue(() => OnEvents?.Invoke(batch));
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
        }
    }
}
