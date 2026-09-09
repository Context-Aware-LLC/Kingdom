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

        /// <summary>The chrome — hero plates, sockets, mana trays, deck piles. In the shipping
        /// board these subscribe to MatchRunner.OnStateUpdated; here they ride this list, because
        /// there is no runner to subscribe to. Rebuilt with the board.</summary>
        private readonly List<Action<GameState>> _stateSinks = new List<Action<GameState>>();

        public ReviewBundle Bundle => _bundle;
        public int FrameCount => _bundle?.Frames.Count ?? 0;
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

            BuildBoard(frame, state);
            _board.OnStateUpdated(state);
            foreach (Action<GameState> sink in _stateSinks) sink(state);
            SelectAction(frame.ChosenActionIndex);

            Debug.Log($"[DecisionReview] {frame.Caption} — {frame.MatchId}#{frame.DecisionIndex}");
            Changed?.Invoke();
        }

        /// <summary>Highlight one action's entities. Defaults to the chosen action on every frame
        /// change; an Editor panel can point it at an alternative to compare.</summary>
        public void SelectAction(int index)
        {
            ReviewFrame frame = Frame;
            if (frame == null || _board == null) return;

            SelectedAction = Mathf.Clamp(index, 0, Mathf.Max(0, frame.Actions.Count - 1));
            if (frame.Actions.Count == 0)
            {
                _board.SetReviewHighlight(null, null);
                return;
            }

            var targets = new HashSet<InstanceId>();
            foreach (InstanceId id in frame.Actions[SelectedAction].ReferencedEntities())
                targets.Add(id);
            _board.SetReviewHighlight(targets.Count == 0 ? null : targets, null);
            Changed?.Invoke();
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
