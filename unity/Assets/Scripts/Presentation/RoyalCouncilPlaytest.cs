#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingdom.AI;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Separate development scene: a complete local match through the shipping runner, input,
    /// card rendering and VFX. It is never included in the public scene list or release builds.
    /// </summary>
    public sealed class RoyalCouncilPlaytest : MonoBehaviour
    {
        public bool Autoplay;
        public int Seed = 1195;
        public bool ReverseSeats;
        public float ActionDelay = 0.2f;
        public GameState Presented { get; private set; }
        public int PresentedActions { get; private set; }
        public int Attacks { get; private set; }
        public int Powers { get; private set; }
        public bool Completed { get; private set; }
        public MatchOutcome Outcome { get; private set; }
        public BoardAnchors Anchors { get; private set; }
        public InputRouter Input { get; private set; }
        public IMatchSession LocalSession { get; private set; }
        private PlayerId _local;
        private ulong _lastTurn;
        private readonly List<float> _frameTimes = new List<float>(12000);
        private float _started = -1f;

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Development scene only: ADB can switch between a measured full match and
            // manual touch checks without rebuilding or changing the production app.
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                Autoplay = intent.Call<bool>("getBooleanExtra", "board_autoplay", Autoplay);
                ReverseSeats = intent.Call<bool>("getBooleanExtra", "board_reverse", ReverseSeats);
                Seed = intent.Call<int>("getIntExtra", "board_seed", Seed);
            }
#endif
            foreach (var intro in FindObjectsByType<OpeningCinematic>(FindObjectsInactive.Include)) Destroy(intro.gameObject);
            Time.timeScale = 1f;
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            if (Camera.main == null)
            {
                var camera = new GameObject("Main Camera") { tag = "MainCamera" };
                camera.AddComponent<Camera>(); camera.AddComponent<AudioListener>();
            }
            ICardDatabase db = CardDatabaseLoader.Load();
            var p0 = new PlayerId("board-review-a");
            var p1 = new PlayerId("board-review-b");
            var setup = new MatchSetup(CardClass.TEMPEST, Deck(db, CardClass.TEMPEST), CardClass.NIGHTVEIL, Deck(db, CardClass.NIGHTVEIL));
            var sessions = LocalMatch.Start(db, setup, p0, p1, (ulong)Seed);
            IMatchSession near = ReverseSeats ? sessions.p1 : sessions.p0;
            IMatchSession far = ReverseSeats ? sessions.p0 : sessions.p1;
            _local = ReverseSeats ? p1 : p0;
            PlayerId remote = ReverseSeats ? p0 : p1;
            LocalSession = near;
            var root = new GameObject("Royal Council Playtest Match");
            root.transform.SetParent(transform, false);
            Anchors = root.AddComponent<BoardAnchors>();
            var physical = root.AddComponent<ProceduralBoardSetup>();
            if (Anchors.Tabletop == null) throw new InvalidOperationException("Build the Royal Council prefab before opening its playtest.");
            Anchors.SetPlayers(_local, remote);
            var runner = root.AddComponent<MatchRunner>();
            runner.CommandDelaySeconds = ActionDelay;
            var human = new HotSeatInput(_local);
            Input = root.AddComponent<InputRouter>();
            Input.Init(_local, human, db, new CommandProcessor(db));
            var board = root.AddComponent<BoardController>();
#if UNITY_EDITOR
            IArtProvider art = new EditorArtProvider();
#else
            IArtProvider art = new AddressableArtProvider();
#endif
            board.Initialize(db, Anchors, _local, remote, Input, runner, art, _local);
            near.OnSnapshot += Input.OnStateUpdated;
            Input.OnStateUpdated(near.Current);
            runner.OnStateUpdated += Input.OnPresentedStateUpdated;
            runner.OnStateUpdated += board.OnStateUpdated;
            Anchors.Tabletop.Bind(_local, remote, Input);
            runner.OnStateUpdated += Anchors.Tabletop.OnStateUpdated;
            var plate0 = Hero(_local, near.Current.PlayerOf(_local).Hero.Class, true, db, art, runner, board);
            var plate1 = Hero(remote, near.Current.PlayerOf(remote).Hero.Class, false, db, art, runner, board);
            Input.SetHeroPlates(plate0, plate1);
            board.SetHeroPlates(_local, plate0, remote, plate1);
            physical.SetupClickables(_local, remote, Input);
            var mulligan = new GameObject("Mulligan confirm").AddComponent<MulliganConfirmButtonView>();
            mulligan.transform.SetParent(root.transform, false);
            mulligan.transform.position = MulliganConfirmButtonView.BoardPosition;
            mulligan.Init(_local, Input);
            near.OnSnapshot += mulligan.OnStateUpdated;
            mulligan.OnStateUpdated(near.Current);
            var choice = new GameObject("Unearth choice").AddComponent<UnearthChoiceView>();
            choice.transform.SetParent(root.transform, false);
            choice.Init(_local, Input, db, board, art);
            near.OnSnapshot += choice.OnStateUpdated;
            var vfx = root.AddComponent<VfxDirector>();
            vfx.Init(art, board);
            runner.OnStateUpdated += vfx.OnStateUpdated;
            runner.OnMatchEvent += vfx.OnMatchEvent;
            runner.PlayRevealClassifier = (state, events) => PlayRevealClassifier.Classify(state, events, db, vfx.RevealHolds);
            runner.OnPlayReveal += vfx.ShowPlayReveal;
            runner.OnPlayReveal += board.OnPlayReveal;
            runner.HeroPowerBeatClassifier = (state, events) => HeroPowerBeatClassifier.Classify(state, events, vfx.HeroPowerHolds);
            runner.OnHeroPowerBeat += vfx.ShowHeroPowerBeat;
            var sound = root.AddComponent<CardSoundDirector>();
            sound.Init(db);
            runner.OnStateUpdated += sound.OnStateUpdated;
            runner.OnMatchEvent += sound.OnMatchEvent;
            runner.OnStateUpdated += Observe;
            runner.OnMatchEvent += events =>
            {
                PresentedActions++;
                foreach (GameEvent e in events)
                {
                    if (e.Kind.ToString().Contains("Attack")) Attacks++;
                    if (e.Kind.ToString().Contains("HeroPower")) Powers++;
                }
            };
            runner.OnMatchEnded += result => { Completed = true; Outcome = result; WriteReport(); };
            IIntentSource localSource = Autoplay ? new PlayingAi(_local, db) : human;
            IIntentSource remoteSource = new PlayingAi(remote, db);
            // Keep authority seat indexes intact while rendering either local seat at the
            // bottom. Two owners exercise the same seat-1 path used by networked matches.
            int localSeat = ReverseSeats ? 1 : 0;
            runner.StartMatch(sessions.p0, ReverseSeats ? new NullIntentSource() : localSource,
                sessions.p1, ReverseSeats ? localSource : new NullIntentSource(),
                PresentationViewer.FixedSeat0, SeatOwnership.Single(localSeat));
            var remoteRunner = new GameObject("Other seat driver").AddComponent<MatchRunner>();
            remoteRunner.transform.SetParent(root.transform, false);
            remoteRunner.CommandDelaySeconds = ActionDelay;
            remoteRunner.StartMatch(sessions.p0, ReverseSeats ? remoteSource : new NullIntentSource(),
                sessions.p1, ReverseSeats ? new NullIntentSource() : remoteSource,
                PresentationViewer.FixedSeat0, SeatOwnership.Single(1 - localSeat));
            _started = Time.unscaledTime;
        }

        private static int[] Deck(ICardDatabase db, CardClass hero)
        {
            var candidates = db.AllCollectible().Where(c => c.Class == hero || c.Class == CardClass.NEUTRAL)
                .Where(c => c.Cost >= 1 && c.Cost <= 6 && c.Type != CardType.HERO_POWER)
                .OrderBy(c => c.Cost).ThenBy(c => c.Id).ToArray();
            var deck = new List<int> { 1194, 1195 };
            foreach (var card in candidates.Where(c => c.Type == CardType.WEAPON).Take(2)) deck.Add(card.Id);
            foreach (var card in candidates.Where(c => c.Type == CardType.SPELL).Take(5)) deck.Add(card.Id);
            var minions = candidates.Where(c => c.Type == CardType.MINION).ToArray();
            int index = 0;
            while (deck.Count < 30) deck.Add(minions[index++ % minions.Length].Id);
            return deck.ToArray();
        }

        private HeroPlateView Hero(PlayerId id, CardClass hero, bool local, ICardDatabase db, IArtProvider art, MatchRunner runner, BoardController board)
        {
            var plate = new GameObject($"Hero {id}").AddComponent<HeroPlateView>();
            plate.Init(id, Anchors.HeroAnchor(id), art, hero, local);
            runner.OnStateUpdated += plate.OnStateUpdated;
            var sockets = new GameObject($"Sockets {id}").AddComponent<HeroSocketsView>();
            sockets.transform.SetParent(Anchors.transform, false);
            sockets.Init(id, Anchors.HeroPowerAnchor(id), Anchors.WeaponAnchor(id), db, art);
            sockets.AttachReveal(board.RevealController);
            runner.OnStateUpdated += sockets.OnStateUpdated;
            return plate;
        }

        private void Observe(GameState state)
        {
            Presented = state;
            // The local session has no server timer; an explicit test deadline exercises the
            // same hourglass input without making it an authority for the local match.
            ulong turn = (ulong)state.TurnNumber;
            if (state.Phase == TurnPhase.Main && turn != _lastTurn)
            {
                _lastTurn = turn;
                Anchors.Tabletop.OnTurnTimer(new TurnTimer(state.ActivePlayer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 75000));
            }
        }

        private void Update()
        {
            if (_started >= 0f && Time.unscaledTime - _started > 8f && !Completed && _frameTimes.Count < 12000)
                _frameTimes.Add(Time.unscaledDeltaTime * 1000f);
        }

        public void WriteReport()
        {
            var times = _frameTimes.OrderBy(t => t).ToArray();
            string report = JsonUtility.ToJson(new Report
            {
                complete = Completed, outcome = Outcome.ToString(), actions = PresentedActions, attacks = Attacks, powers = Powers,
                frames = times.Length, averageMs = times.Length > 0 ? times.Average() : 0,
                p95Ms = times.Length > 0 ? times[Mathf.Min(times.Length - 1, (int)(times.Length * 0.95f))] : 0,
                width = Screen.width, height = Screen.height, device = SystemInfo.deviceModel, gpu = SystemInfo.graphicsDeviceName,
                seed = Seed, reverseSeats = ReverseSeats
            }, true);
            string path = Path.Combine(Application.persistentDataPath, "royal-council-playtest.json");
            File.WriteAllText(path, report);
            Debug.Log("[RoyalCouncilPlaytest] " + report + "\n" + path);
        }

        [Serializable] private sealed class Report
        {
            public bool complete, reverseSeats;
            public string outcome, device, gpu;
            public int actions, attacks, powers, frames, width, height, seed;
            public float averageMs, p95Ms;
        }
    }
}
#endif
