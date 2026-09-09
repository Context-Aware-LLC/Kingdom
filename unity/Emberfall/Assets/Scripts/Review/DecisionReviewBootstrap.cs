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
    /// is cheap. A frame is a still: <see cref="BoardController.OnStateUpdated"/> is called once
    /// per frame change and nothing ever advances the state.
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

        private BoardAnchors _anchors;
        private BoardController _board;
        private ReviewBundle _bundle;
        private ICardDatabase _cards;
        private IArtProvider _art;

        /// <summary>The chrome — hero plates, sockets, mana trays, deck piles. In the shipping
        /// board these subscribe to MatchRunner.OnStateUpdated; here they ride this list, because
        /// there is no runner to subscribe to.</summary>
        private readonly List<Action<GameState>> _stateSinks = new List<Action<GameState>>();

        private PlayerId _actorId;
        private PlayerId _opponentId;
        private bool _seated;

        public ReviewBundle Bundle => _bundle;
        public int FrameCount => _bundle?.Frames.Count ?? 0;
        public int CurrentFrame { get; private set; }
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
            _anchors = GetComponent<BoardAnchors>();
            _board = GetComponent<BoardController>();
            if (_anchors == null || _board == null)
            {
                Debug.LogError("[DecisionReview] Needs BoardAnchors and BoardController on this object.");
                enabled = false;
                return;
            }

            Debug.Log($"[DecisionReview] {_bundle.Manifest.BundleId}: {_bundle.Frames.Count} frames, "
                      + $"cards {_bundle.Manifest.CardDataSha256.Substring(0, 12)}, "
                      + $"wire {_bundle.Manifest.WireContractVersion}");

            Show(Mathf.Clamp(startFrame, 0, _bundle.Frames.Count - 1));
        }

        /// <summary>Render a frame. Safe to call repeatedly; the board diffs snapshots.</summary>
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

            Seat(frame, state);
            _board.OnStateUpdated(state);
            foreach (Action<GameState> sink in _stateSinks) sink(state);
            HighlightChosenAction(frame, state);

            Debug.Log($"[DecisionReview] {frame.Caption} — {frame.MatchId}#{frame.DecisionIndex}");
        }

        /// <summary>
        /// THE ACTING PLAYER RENDERS AT THE BOTTOM, whichever seat they really held.
        /// <see cref="BoardAnchors.ResolveSeat"/> keys on PlayerId, not on seat index, so handing
        /// it (actor, opponent) is all it takes — the same trick NetworkedGameBootstrap uses to put
        /// a server-seat-1 local on the near side. Reviewing from the wrong side would show the
        /// actor their opponent's board as their own.
        ///
        /// Seating happens ONCE. Every frame in a bundle is a different match, but the anchors and
        /// presenters are keyed by the ids in the state, and re-seating mid-session would strand
        /// the presenters built under the previous mapping.
        /// </summary>
        private void Seat(ReviewFrame frame, GameState state)
        {
            if (_seated) return;

            PlayerId seat0 = state.Players[0].Id;
            PlayerId seat1 = state.Players[1].Id;
            _actorId = string.Equals(frame.Actor, seat0.Value, StringComparison.Ordinal) ? seat0 : seat1;
            _opponentId = _actorId == seat0 ? seat1 : seat0;

            _anchors.SetPlayers(_actorId, _opponentId);
            // No InputRouter and no MatchRunner: nothing here is playable, and both parameters are
            // null-tolerant by design. localHumanId is the actor so affordances read from their side.
            _board.Initialize(_cards, _anchors, _actorId, _opponentId,
                              inputRouter: null, matchRunner: null,
                              artProvider: _art, localHumanId: _actorId);

            CardClass actorClass = state.PlayerOf(_actorId).Hero.Class;
            CardClass opponentClass = state.PlayerOf(_opponentId).Hero.Class;
            BuildChrome(_actorId, actorClass);
            BuildChrome(_opponentId, opponentClass);
            _seated = true;
        }

        /// <summary>Light up what the chosen action names. The board already owns the primitive —
        /// it is what targeting uses — and BoardController.SetReviewHighlight is the one addition
        /// this proof makes to shipping presentation code.</summary>
        private void HighlightChosenAction(ReviewFrame frame, GameState state)
        {
            if (frame.ChosenActionIndex < 0 || frame.ChosenActionIndex >= frame.Actions.Count)
            {
                _board.SetReviewHighlight(null, null);
                return;
            }

            ReviewAction chosen = frame.Actions[frame.ChosenActionIndex];
            var targets = new HashSet<InstanceId>();
            foreach (InstanceId id in chosen.ReferencedEntities()) targets.Add(id);
            _board.SetReviewHighlight(targets.Count == 0 ? null : targets, null);
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
    }
}
