using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using UnityEngine;

namespace Emberfall.Review
{
    /// <summary>
    /// Puts one training decision on the real Emberfall board.
    ///
    /// It is NOT a match. There is no session, no runner, no input router, no server and no
    /// server to have — <see cref="BoardController.Initialize"/> takes the router and the runner as
    /// nullable parameters and null-guards every use of them, which is the entire reason this proof
    /// is cheap. A frame is a still: the board is handed one snapshot and nothing ever advances it.
    ///
    /// THIS COMPONENT MUST NOT LIVE IN AN EDITOR-ONLY ASSEMBLY. It did, for one commit, and Unity
    /// refused to attach it — "Can't add script behaviour ... because it is an editor script" —
    /// which surfaced as a NullReferenceException on the AddComponent result. Emberfall.Review is a
    /// runtime assembly constrained to UNITY_EDITOR, so it still cannot reach a player build; the
    /// menu command that drives it lives in Emberfall.Review.Editor next door.
    ///
    /// The chrome helpers below are DUPLICATED from NetworkedGameBootstrap rather than extracted
    /// from it. Extracting would change how the shipping board boots, for a proof that may be
    /// thrown away; when this earns a durable home, the extraction is the first thing to do.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DecisionReviewBootstrap : MonoBehaviour
    {
        [Tooltip("Directory holding bundle-manifest.json, frames.jsonl and cards/. Built by " +
                 "Emberfall.Tools.DecisionReview.")]
        [SerializeField] private string bundleDirectory = "";

        [Tooltip("Which frame to open on. Clamped to the bundle.")]
        [SerializeField] private int startFrame;

        private ReviewBundle _bundle;
        private ICardDatabase _cards;
        private IArtProvider _art;

        private GameObject _boardRoot;
        private BoardAnchors _anchors;
        private BoardController _board;
        private GameState _lastState;

        /// <summary>The chrome — hero plates, sockets, mana trays, deck piles. In the shipping
        /// board these subscribe to MatchRunner.OnStateUpdated; here they ride this list, because
        /// there is no runner to subscribe to. Rebuilt with the board.</summary>
        private readonly List<Action<GameState>> _stateSinks = new List<Action<GameState>>();

        [Tooltip("Whose grades these are. Names the sidecar file, so two reviewers never share one.")]
        [SerializeField] private string reviewer = "sam";

        public string Reviewer => string.IsNullOrWhiteSpace(reviewer) ? "reviewer" : reviewer.Trim();

        /// <summary>Judgement, kept strictly out of the bundle. Null until the bundle loads.</summary>
        public ReviewAnnotations Annotations { get; private set; }

        /// <summary>The label lists the panel offers. Editable on disk and from the panel — it is a
        /// work in progress and the tool must not make it expensive to change.</summary>
        public ReviewVocabulary Vocabulary { get; private set; }

        public ReviewBundle Bundle => _bundle;
        public int FrameCount => _bundle?.Frames.Count ?? 0;

        /// <summary>How many of the bundle's decisions carry a judgement — the review pass's
        /// actual progress, and the reason a long pass can be put down and picked up.</summary>
        public int GradedCount
        {
            get
            {
                if (_bundle == null || Annotations == null) return 0;
                int n = 0;
                foreach (ReviewFrame frame in _bundle.Frames) if (Annotations.IsGraded(frame)) n++;
                return n;
            }
        }
        public int CurrentFrame { get; private set; }
        public int SelectedAction { get; private set; }

        /// <summary>Raised after a frame renders, so an Editor panel can follow along.</summary>
        public event Action Changed;

        public ReviewFrame Frame =>
            _bundle != null && CurrentFrame >= 0 && CurrentFrame < _bundle.Frames.Count
                ? _bundle.Frames[CurrentFrame]
                : null;

        /// <summary>Set before Start when driving this from a menu command.</summary>
        public void Configure(string directory, int frame = 0)
        {
            bundleDirectory = directory;
            startFrame = frame;
        }

        private void Start()
        {
            try
            {
                _bundle = ReviewBundle.Load(bundleDirectory);
            }
            catch (Exception ex)
            {
                // A bundle that will not load is the ONLY honest outcome to show. Rendering
                // whatever decoded is how a reviewer ends up grading a state nobody was in.
                Debug.LogError($"[DecisionReview] {ex.Message}");
                enabled = false;
                return;
            }

            _cards = _bundle.Cards;
            _art = new EditorArtProvider();
            Annotations = ReviewAnnotations.Open(_bundle, Reviewer);
            Vocabulary = ReviewVocabulary.Open(
                System.IO.Path.GetDirectoryName(Annotations.Path));
            Debug.Log($"[DecisionReview] annotations → {Annotations.Path} ({Annotations.Count} graded)");
            Debug.Log($"[DecisionReview] vocabulary  → {Vocabulary.Path} (editable; edit it any time)");

            Debug.Log($"[DecisionReview] {_bundle.Manifest.BundleId}: {_bundle.Frames.Count} frames, "
                      + $"cards {Short(_bundle.Manifest.CardDataSha256)}, "
                      + $"wire {_bundle.Manifest.WireContractVersion}");

            Show(Mathf.Clamp(startFrame, 0, _bundle.Frames.Count - 1));
        }

        public void Next() => Show(CurrentFrame + 1);
        public void Previous() => Show(CurrentFrame - 1);

        /// <summary>
        /// Render a frame.
        ///
        /// THE BOARD IS REBUILT EVERY TIME, and that is not laziness. Every frame in a bundle is a
        /// DIFFERENT MATCH — 30 frames, 30 matches in the current one — so there is no continuity
        /// for BoardController's snapshot diff to exploit and nothing it could correctly carry
        /// across. Worse, the acting player is p1 in 17 of those 30, and the actor has to render at
        /// the bottom in all of them; re-seating a live board would leave every presenter parented
        /// to an anchor that now means the other player. Rebuilding is a few dozen GameObjects and
        /// removes the whole class of question.
        /// </summary>
        public void Show(int index)
        {
            if (_bundle == null || _bundle.Frames.Count == 0) return;
            CurrentFrame = Mathf.Clamp(index, 0, _bundle.Frames.Count - 1);
            ReviewFrame frame = _bundle.Frames[CurrentFrame];

            GameState state;
            try
            {
                state = frame.State();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DecisionReview] frame {CurrentFrame} refused: {ex.Message}");
                return;
            }

            _lastState = state;
            Groups = ActionGrouping.Group(frame, state, _cards);
            BuildBoard(frame, state);
            _board.OnStateUpdated(state);
            foreach (Action<GameState> sink in _stateSinks) sink(state);
            if (frame.IsTargetAudit) ClearActionSelection();
            else SelectAction(frame.ChosenActionIndex);

            Debug.Log($"[DecisionReview] {frame.Caption} — {frame.MatchId}#{frame.DecisionIndex}");
            Changed?.Invoke();
        }

        /// <summary>
        /// Highlight one action's entities. Defaults to the chosen action on every frame change;
        /// an Editor panel can point it at an alternative to compare.
        ///
        /// It also SAYS what it did, in <see cref="HighlightNote"/>. A highlight that silently does
        /// nothing is the worst outcome here — it reads as "this action names nothing", when the
        /// truth is usually that it names a hero, and the board draws heroes with HeroPlateView,
        /// which owns no CardPresenter and therefore cannot glow. Sam hit exactly that on frame 1,
        /// whose chosen action targets the enemy hero.
        /// </summary>
        public void SelectAction(int index)
        {
            ReviewFrame frame = Frame;
            if (frame == null || _board == null) return;

            SelectedAction = Mathf.Clamp(index, 0, Mathf.Max(0, frame.Actions.Count - 1));
            if (frame.Actions.Count == 0)
            {
                _board.SetReviewHighlight(null, null);
                HighlightNote = "no legal actions";
                return;
            }

            GameState state = _lastState;
            var lit = new HashSet<InstanceId>();
            var heroes = new List<string>();
            var missing = new List<string>();

            foreach (InstanceId id in frame.Actions[SelectedAction].ReferencedEntities())
            {
                switch (Classify(state, id))
                {
                    case EntityKind.Card: lit.Add(id); break;
                    case EntityKind.Hero: heroes.Add(id.ToString()); break;
                    default: missing.Add(id.ToString()); break;
                }
            }

            _board.SetReviewHighlight(lit.Count == 0 ? null : lit, null);

            var note = new List<string>();
            if (lit.Count > 0) note.Add($"{lit.Count} card(s) lit");
            if (heroes.Count > 0) note.Add($"hero {string.Join(", ", heroes)} — heroes cannot glow");
            if (missing.Count > 0) note.Add($"not on the board: {string.Join(", ", missing)}");
            HighlightNote = note.Count == 0 ? "this action names nothing" : string.Join(" · ", note);
            Changed?.Invoke();
        }

        public void ClearActionSelection()
        {
            SelectedAction = -1;
            if (_board != null) _board.SetReviewHighlight(null, null);
            HighlightNote = "select View beside a play to inspect its targets";
            Changed?.Invoke();
        }

        /// <summary>What the last <see cref="SelectAction"/> could and could not light up.</summary>
        public string HighlightNote { get; private set; } = "";

        /// <summary>
        /// The frame's legal actions grouped into the distinct PLAYS they represent, with the
        /// policy's probability summed over each. Recomputed per frame.
        ///
        /// This replaces a dropdown that asked the reviewer to classify the same thing by hand.
        /// See ActionGrouping for why that was the wrong question to ask a human.
        /// </summary>
        public IReadOnlyList<ActionGroup> Groups { get; private set; } = new List<ActionGroup>();

        private enum EntityKind { Absent, Card, Hero }

        /// <summary>Only cards in a zone the board draws get a presenter, so only those can glow.</summary>
        private static EntityKind Classify(GameState state, InstanceId id)
        {
            if (state == null) return EntityKind.Absent;
            foreach (PlayerState player in state.Players)
            {
                if (player.Hero != null && player.Hero.Id == id) return EntityKind.Hero;
                foreach (CardInstance card in player.Board) if (card.Id == id) return EntityKind.Card;
                foreach (CardInstance card in player.Hand) if (card.Id == id) return EntityKind.Card;
            }
            return EntityKind.Absent;
        }

        /// <summary>
        /// THE ACTING PLAYER RENDERS AT THE BOTTOM, whichever seat they really held.
        /// <see cref="BoardAnchors"/> resolves seats by PlayerId, not by index, so handing it
        /// (actor, opponent) is all it takes — the same trick NetworkedGameBootstrap uses to put a
        /// server-seat-1 local on the near side. Reviewing from the wrong side would show the actor
        /// their opponent's board as their own, and would do it silently.
        /// </summary>
        private void BuildBoard(ReviewFrame frame, GameState state)
        {
            if (_boardRoot != null) DestroyImmediate(_boardRoot);
            _stateSinks.Clear();

            // THE BACKDROP LIVES UNDER THE BOARD; THE THING THAT SIZES IT LIVES ON THE CAMERA.
            // ProceduralBoardSetup creates a BoardBackdrop under itself and then attaches a
            // SafeAreaCamera to Camera.main — outside this hierarchy, and only when one is absent.
            // SafeAreaCamera scales the plate in Awake and thereafter only when the screen changes,
            // which it never does here. So a surviving component silently keeps sizing a plate that
            // was destroyed with the last board, and every rebuild after the first paints a
            // backdrop that no longer fills the view (Sam, watching it step, 2026-09-08).
            // Dropping it makes ProceduralBoardSetup re-add it, and its Awake bind the new plate.
            Camera main = Camera.main;
            if (main != null)
            {
                var safeArea = main.GetComponent<SafeAreaCamera>();
                if (safeArea != null) DestroyImmediate(safeArea);
            }

            _boardRoot = new GameObject("BoardRoot");
            _boardRoot.transform.SetParent(transform, false);

            _anchors = _boardRoot.AddComponent<BoardAnchors>();
            _boardRoot.AddComponent<ProceduralBoardSetup>();   // builds the anchor tree in Awake
            _board = _boardRoot.AddComponent<BoardController>();

            PlayerId seat0 = state.Players[0].Id;
            PlayerId seat1 = state.Players[1].Id;
            PlayerId actor = string.Equals(frame.Actor, seat0.Value, StringComparison.Ordinal) ? seat0 : seat1;
            PlayerId opponent = actor == seat0 ? seat1 : seat0;

            _anchors.SetPlayers(actor, opponent);
            // No InputRouter and no MatchRunner: nothing here is playable, and both parameters are
            // null-tolerant by design. localHumanId is the actor so affordances read from their side.
            _board.Initialize(_cards, _anchors, actor, opponent,
                              inputRouter: null, matchRunner: null,
                              artProvider: _art, localHumanId: actor);

            BuildChrome(actor, state.PlayerOf(actor).Hero.Class);
            BuildChrome(opponent, state.PlayerOf(opponent).Hero.Class);
        }

        // ── Chrome, duplicated from NetworkedGameBootstrap (see the class remark) ───────────────

        private void BuildChrome(PlayerId playerId, CardClass heroClass)
        {
            Transform hero = _anchors.HeroAnchor(playerId);
            if (hero != null)
            {
                var plate = new GameObject($"HeroPlate_{playerId}").AddComponent<HeroPlateView>();
                plate.Init(playerId, hero, _art, heroClass);
                _stateSinks.Add(plate.OnStateUpdated);
            }

            Transform power = _anchors.HeroPowerAnchor(playerId);
            Transform weapon = _anchors.WeaponAnchor(playerId);
            if (power != null || weapon != null)
            {
                var sockets = new GameObject($"HeroSockets_{playerId}").AddComponent<HeroSocketsView>();
                sockets.Init(playerId, power, weapon, _cards, _art);
                _stateSinks.Add(sockets.OnStateUpdated);
            }

            Transform mana = _anchors.ManaTrayAnchor(playerId);
            if (mana != null)
            {
                var tray = new GameObject($"ManaTray_{playerId}").AddComponent<ManaTrayView>();
                tray.Init(playerId, mana);
                _stateSinks.Add(tray.OnStateUpdated);
            }

            Transform deck = _anchors.AnchorFor(playerId, Zone.Deck, 0);
            if (deck != null)
            {
                var pile = new GameObject($"DeckPile_{playerId}").AddComponent<DeckPileView>();
                pile.Init(playerId, deck);
                _stateSinks.Add(pile.OnStateUpdated);
            }
        }

        private static string Short(string hash) =>
            string.IsNullOrEmpty(hash) ? "?" : hash.Substring(0, Math.Min(12, hash.Length));
    }
}
