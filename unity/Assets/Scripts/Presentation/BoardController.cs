using System.Collections;
using System.Collections.Generic;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Data;
using Kingdom.Session;
using TMPro;
using UnityEngine;


namespace Kingdom.Presentation
{
    // Owns all CardPresenter instances on the board and in hand.
    // Called by NetworkedGameBootstrap: Initialize() once, then OnStateUpdated() on every engine tick.
    // Diffs successive GameState snapshots → spawn/move/destroy CardPresenters accordingly.
    public sealed class BoardController : MonoBehaviour
    {
        [SerializeField] private BoardAnchors boardAnchors;

        // Runtime card visual dimensions (world units). Internal, not private: HandFanLayout
        // derives the fan pitch from CardW, and a second copy of this number has already gone
        // stale once (the §1.1 pitch table was written against an assumed 1.3 × 1.8).
        //
        // #234 and the 2026-08-29 readability pass: THE BOARD CARD GREW AND THE HAND DID NOT MOVE.
        //
        // Sam, 2026-08-21: "the ones on the board are not big enough". They were 137.3 x 193.7 px
        // on a 1080p phone — 52% of the 265 px collection tile EF-050's acuity margin was measured
        // against — while a card in hand was 1.44x that. The complaint is about the BOARD end of
        // that ratio, so the ratio is what moved: 1.17/0.9 = 1.30 and 1.65/0.9 = 1.8333, with
        // HandFanLayout.RestScale first fell 1.44 -> 1.296 (= 1.44 x 0.9), then 1.296 -> 1.20
        // for this follow-up 1.08x board growth, to hold HandCardW/H at
        // 1.6848 x 2.3760, the exact values they had before. A card in hand is unchanged to five
        // decimal places, so EF-062 §5, the CentreY solve, TopEdgeY, MaxPitch, Sag and
        // DeviceLayout's band-clearance assertion are all untouched by this — deliberately, because
        // that solve is the hand's whole legibility budget and re-opening it was not the ask.
        //
        // 2026-08-31, Sam: "improve readability of cards in play and in hand". A THIRD 1.08 growth
        // of the board card, paid for by BulwarkRowOffset (0.40 -> 0.28) and RowY (1.4633 -> 1.41)
        // rather than by the hand, which is held at 1.6848 x 2.3760 for the third pass running.
        //
        // WHY THE HAND DID NOT GROW, THOUGH IT WAS ASKED FOR. DeviceLayout.SelfCheck assertion 7
        // bounds the hand's rules band against DesignHalfHeight 4.6 — the ORTHOGRAPHIC half-height.
        // The shipping camera has been PERSPECTIVE since f121a591 (BoardCameraRig.Configure sets
        // orthographic = false), so that assertion, and all of device_layout.py, model a camera
        // that no longer runs. Measured against the real rig a larger hand is fine; measured
        // against the guard it fails. The guard is the thing to fix, and re-deriving the vertical
        // model for a perspective camera is not a change to bury inside a sizing tweak.
        //
        // RETIREMENT TRIGGER: these two and RestScale are one number in two places now. Moving
        // either without the compensating move on the other resizes the card in hand. `device_layout.py`
        // is NOT the check to re-run — it reads `orthographic size` out of the scene YAML and
        // reports a camera the game does not use. Measure against BoardCameraRig instead, and
        // re-solve HandFanLayout.CentreY if the hand card's height moved.
        internal const float CardW = 1.51632f;  // 1.404 * 1.08
        internal const float CardH = 2.13840f;  // 1.98  * 1.08; keeps the 1.17:1.65 aspect

        // How far a bulwarked minion steps out of its row toward the centerline. NOT scaled with
        // the card by #234, which held it at 0.40 and grew the card around it.
        //
        // 0.40 -> 0.28 on 2026-08-31: it is what PAID for the readability growth above. The card
        // is the thing being read and the step is not, so when the two collided at the centreline
        // the step gave way. It is still 13% of the card's height and still the whole of the Taunt
        // front-row visual language — a smaller step, not an absent one. Below about 0.20 the two
        // rows stop reading as two rows, so this is close to spent.
        // Internal, not private: DeviceLayout.SelfCheck asserts the lane this leaves at the
        // centreline, the same reason ProceduralBoardSetup.HeroX and DeckX are internal.
        internal const float BulwarkRowOffset = 0.28f;
        internal const string BulwarkEffectSpriteKey = "effect_bulwark_shields";
        internal const string ShroudSmokeLeftSpriteKey = "effect_shroud_smoke_left";
        internal const string ShroudSmokeRightSpriteKey = "effect_shroud_smoke_right";
        internal const string ShroudSmokeLowerSpriteKey = "effect_shroud_smoke_lower";
        internal const string FrostCrystalsLeftSpriteKey = "effect_frost_crystals_left";
        internal const string FrostCrystalsRightSpriteKey = "effect_frost_crystals_right";
        internal const string FrostCrystalsLowerSpriteKey = "effect_frost_crystals_lower";

        // WHERE THE STAT ROW SITS, as a fraction of CardH below the card's centre. The lowest thing
        // a minion carries, so it is the line the hand fan must not cover — the fan draws in FRONT
        // of the board row and tucks under it on purpose (see HandFanLayout.CentreY), which is only
        // safe while it stops above this. Hoisted out of the four call sites below by #234 so
        // DeviceLayout.SelfCheck can assert against the number the layout actually uses rather than
        // a copy of it.
        internal const float StatRowFrac = 0.42f;

        // Hand cards draw above board content and below the match-end banner (sortingOrder 100).
        // Order within the hand is the slot index, so the fan layers left-to-right and the
        // right-most card at any point is the topmost — the hit-test rule in hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8.
        private const int HandSortBase = 50;

        private ICardDatabase _db;
        private PlayerId _p0Id;
        private PlayerId _p1Id;
        private PlayerId _localHumanId;
        private InputRouter _inputRouter;
        private MatchRunner _matchRunner;
        private IArtProvider _artProvider;
        private IUiSpriteProvider _uiSprites;
        private CardRevealController _revealController;

        /// <summary>The match's one reveal controller, so HeroSocketsView can make the equipped
        /// weapon inspectable through the same pipeline. Null until <see cref="Initialize"/>.</summary>
        internal CardRevealController RevealController => _revealController;
        private GameState _currentState;

        // Hero portraits are separate from their anchors so combat motion can move the character
        // without dragging its collider, hero-power socket or weapon socket along with it. The
        // bootstrap supplies the two live views after constructing them.
        private readonly Dictionary<PlayerId, HeroPlateView> _heroPlates = new();

        // ── Combat contact motion ──────────────────────────────────────────────────
        // AttackDeclared is the one event shared by local, remote and AI combat. Driving the
        // motion from it keeps presentation out of input prediction and gives every attacker /
        // defender pairing the same lunge, contact recoil and return.
        private const float CombatWindupSeconds = 0.12f;
        private const float CombatStrikeSeconds = 0.16f;
        private const float CombatImpactSeconds = 0.13f;
        private const float CombatReturnSeconds = 0.22f;
        private const float CombatContactOverlap = 0.08f;
        private const float CombatMaxWindup = 0.16f;
        private const float CombatMaxRecoil = 0.20f;

        private Coroutine _combatMotion;
        private Transform _combatAttacker;
        private Transform _combatDefender;
        private Vector3 _combatAttackerRestLocalPosition;
        private Quaternion _combatAttackerRestLocalRotation;
        private Vector3 _combatDefenderRestLocalPosition;
        private Quaternion _combatDefenderRestLocalRotation;
        private UnityEngine.Rendering.SortingGroup _combatAttackerSorting;
        private bool _combatAttackerSortingWasEnabled;
        private int _combatAttackerSortingOrder;

        private readonly Dictionary<InstanceId, CardPresenter>  _presenters  = new();
        private readonly Dictionary<InstanceId, CardLiveState>  _liveStates  = new();

        // Card-backs for projected hidden hands (dev-docs/architecture/determinism-and-projection.md:23). Hidden cards all
        // share InstanceId(0) with an empty DefinitionId, so they can't ride the id-keyed path —
        // they're tracked positionally per owner instead.
        private readonly Dictionary<PlayerId, List<CardPresenter>> _cardBacks = new();

        // Tracks every InstanceId that was live last tick so we can destroy departed cards.
        private readonly HashSet<InstanceId> _lastLive = new();
        private readonly HashSet<InstanceId> _thisLive = new();

        // Draw flights spawned during the current tick, so a multi-card tick deals rather than
        // dumps. The opening hand is the case that makes this worth having: four cards arriving
        // in one snapshot fly as a sequence instead of a single blur.
        private const float DrawStagger = 0.06f;
        private int _drawsThisTick;

        // ── Death dissolve (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §5) ────────────────────────────────────
        //
        // THE ORDERING PROBLEM. MatchRunner.PlaybackLoop fires OnStateUpdated and THEN
        // OnMatchEvent off the same queue entry, in the same frame. A minion that dies is already
        // absent from that snapshot's board list, so the departed-card sweep below used to destroy
        // its presenter before the CardDestroyed fact it belongs to had been raised at all — the card
        // was gone, and nothing could have animated it.
        //
        // So the sweep no longer destroys: it PARKS ids, and a WaitForEndOfFrame flush resolves
        // them once the same frame's events have landed. End of frame is the right boundary
        // because it is unambiguously after OnMatchEvent and unambiguously before the next
        // PlaybackLoop iteration (which dequeues at most one entry per frame).
        private readonly List<InstanceId>    _pendingRemoval = new();
        private readonly HashSet<InstanceId> _diedThisTick   = new();
        private Coroutine _removalFlush;
        private bool _heroDeathShakePlayed;

        private const float DeathDissolveDur = 0.40f;

        // ── Public API ──────────────────────────────────────────────────────────────

        public void Initialize(ICardDatabase db, BoardAnchors anchors, PlayerId p0Id, PlayerId p1Id,
                               InputRouter inputRouter = null, MatchRunner matchRunner = null,
                               IArtProvider artProvider = null, PlayerId? localHumanId = null)
        {
            _db          = db;
            boardAnchors = anchors;
            _p0Id        = p0Id;
            _p1Id        = p1Id;
            // Attack affordances key off the human seat. Hot-seat: p0. Networked: the local
            // player, who may sit at either seat — NetworkedGameBootstrap passes it explicitly.
            _localHumanId = localHumanId ?? p0Id;
            _inputRouter = inputRouter;
            _matchRunner = matchRunner;
            _artProvider = artProvider;
            _heroDeathShakePlayed = false;
#if UNITY_EDITOR
            // Editor play mode: serve art from AssetDatabase — fast iteration, no content
            // build required (dev-docs/pipelines/art-and-rendering.md:13). Player builds resolve through Addressables.
            _artProvider ??= new EditorArtProvider();
            _uiSprites   ??= new EditorUiSpriteProvider();
#else
            _artProvider ??= new AddressableArtProvider();
            _uiSprites   ??= new AddressableUiSpriteProvider();
#endif
            // One shared inspect surface for the local hand and both boards. Hidden opponent cards
            // never receive a definition below, so they cannot acquire a reveal trigger or leak
            // projected information. Touch inspection is a hold-and-release read; desktop uses hover.
            if (_revealController == null)
                _revealController = CardRevealController.Create(transform, _inputRouter, this);

            // Hover and press delivery for every collider on the board that is not a router
            // gesture subject. Here rather than on the router: a router-less board still hovers.
            if (GetComponent<BoardPointerRelay>() == null)
                gameObject.AddComponent<BoardPointerRelay>();

            // Targeting-state visibility: while the router has a targeting state armed,
            // only valid targets glow (binder suppresses readiness glow for the duration).
            if (_inputRouter != null)
            {
                _inputRouter.TargetingChanged += OnTargetingChanged;
                _inputRouter.TargetFocusChanged += OnTargetFocusChanged;
            }

            // Combat motion and deaths both consume the paced semantic event stream. Deaths are
            // read here, not only in CardMaterialBinder, because the controller owns presenter
            // lifetime and is the only place that can hold a card alive long enough to animate.
            if (_matchRunner != null)
                _matchRunner.OnMatchEvent += OnMatchEvent;
        }

        /// <summary>Supplies the visual-only hero transforms used by combat contact motion.</summary>
        public void SetHeroPlates(PlayerId firstPlayer, HeroPlateView firstPlate,
                                  PlayerId secondPlayer, HeroPlateView secondPlate)
        {
            _heroPlates.Clear();
            if (firstPlate != null) _heroPlates[firstPlayer] = firstPlate;
            if (secondPlate != null) _heroPlates[secondPlayer] = secondPlate;
        }

        private void OnDestroy()
        {
            if (_inputRouter != null)
            {
                _inputRouter.TargetingChanged -= OnTargetingChanged;
                _inputRouter.TargetFocusChanged -= OnTargetFocusChanged;
            }
            if (_matchRunner != null)
                _matchRunner.OnMatchEvent -= OnMatchEvent;
            StopCombatMotion(restore: false);
        }

        // EffectInterpreter.cs:148 and RulesEngine.cs:94 both emit CardDestroyed with SOURCE set and no
        // target — the dying thing is the subject of its own death. Reading both fields keeps this
        // correct either way, and costs nothing: a weapon's CardDestroyed fact names an id with no presenter,
        // which the sweep simply never looks up.
        private void OnMatchEvent(IReadOnlyList<GameEvent> batch)
        {
            HeroImpactFeedbackDecision impact = HeroImpactFeedbackPolicy.Evaluate(
                _currentState, _localHumanId, batch, _heroDeathShakePlayed);
            for (int i = 0; i < impact.DamagePulseCount; i++)
                HeroHaptics.PulseDamage();
            if (impact.ShakeForHeroDeath)
            {
                _heroDeathShakePlayed = true;
                ScreenEarthquake.Play(Camera.main);
            }

            foreach (GameEvent e in batch)
            {
                if (e.Kind == GameEventKind.AttackDeclared && e.Source.HasValue && e.Target.HasValue)
                    StartCombatMotion(e.Source.Value, e.Target.Value);

                if (e.Kind == GameEventKind.CardDestroyed)
                {
                    if (e.Source.HasValue) _diedThisTick.Add(e.Source.Value);
                    if (e.Target.HasValue) _diedThisTick.Add(e.Target.Value);
                }
            }
        }

        private void StartCombatMotion(InstanceId attackerId, InstanceId defenderId)
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return;

            Transform attacker = ResolveCombatVisual(attackerId);
            Transform defender = ResolveCombatVisual(defenderId);
            if (attacker == null || defender == null || attacker == defender) return;

            StopCombatMotion(restore: true);

            _combatAttacker = attacker;
            _combatDefender = defender;
            _combatAttackerRestLocalPosition = attacker.localPosition;
            _combatAttackerRestLocalRotation = attacker.localRotation;
            _combatDefenderRestLocalPosition = defender.localPosition;
            _combatDefenderRestLocalRotation = defender.localRotation;
            RaiseCombatAttacker(attacker);
            _combatMotion = StartCoroutine(PlayCombatMotion(attacker, defender));
        }

        private void RaiseCombatAttacker(Transform attacker)
        {
            _combatAttackerSorting = attacker.GetComponent<UnityEngine.Rendering.SortingGroup>();
            if (_combatAttackerSorting == null)
            {
                _combatAttackerSorting =
                    attacker.gameObject.AddComponent<UnityEngine.Rendering.SortingGroup>();
                _combatAttackerSortingWasEnabled = false;
                _combatAttackerSortingOrder = 0;
            }
            else
            {
                _combatAttackerSortingWasEnabled = _combatAttackerSorting.enabled;
                _combatAttackerSortingOrder = _combatAttackerSorting.sortingOrder;
            }

            _combatAttackerSorting.enabled = true;
            _combatAttackerSorting.sortingOrder = 80;
        }

        internal Transform ResolveCombatVisual(InstanceId id)
        {
            if (_presenters.TryGetValue(id, out CardPresenter presenter) && presenter != null)
                return presenter.transform;

            if (_currentState == null) return null;
            foreach (PlayerState player in _currentState.Players)
            {
                if (player.Hero.Id != id) continue;
                return _heroPlates.TryGetValue(player.Id, out HeroPlateView plate) && plate != null
                    ? plate.transform
                    : null;
            }
            return null;
        }

        private IEnumerator PlayCombatMotion(Transform attacker, Transform defender)
        {
            Vector3 attackerRest = attacker.position;
            Vector3 defenderRest = defender.position;
            Vector3 separation = defenderRest - attackerRest;
            separation.z = 0f;
            float distance = separation.magnitude;
            if (distance < 0.001f)
            {
                FinishCombatMotion();
                yield break;
            }

            Vector3 direction = separation / distance;
            float attackerHalf = HalfVisualExtent(attacker, direction);
            float defenderHalf = HalfVisualExtent(defender, direction);
            float contactTravel = Mathf.Max(0f,
                distance - attackerHalf - defenderHalf + CombatContactOverlap);
            float windupDistance = Mathf.Min(CombatMaxWindup, distance * 0.06f);
            float recoilDistance = Mathf.Min(CombatMaxRecoil,
                Mathf.Max(0.08f, contactTravel * 0.08f));

            // Negative z is nearer the board camera (the input drag uses the same convention).
            float strikeZ = Mathf.Min(attackerRest.z, defenderRest.z) - 0.35f;
            Vector3 windup = attackerRest - direction * windupDistance;
            Vector3 contact = attackerRest + direction * contactTravel;
            contact.z = strikeZ;
            Vector3 bounce = contact - direction * Mathf.Min(0.14f, contactTravel * 0.12f);
            Vector3 defenderRecoil = defenderRest + direction * recoilDistance;

            yield return MoveCombatPhase(attacker, attackerRest, windup,
                                         defender, defenderRest, defenderRest,
                                         CombatWindupSeconds, 0);
            yield return MoveCombatPhase(attacker, windup, contact,
                                         defender, defenderRest, defenderRest,
                                         CombatStrikeSeconds, 1);
            yield return MoveCombatPhase(attacker, contact, bounce,
                                         defender, defenderRest, defenderRecoil,
                                         CombatImpactSeconds, 2);
            yield return MoveCombatPhase(attacker, bounce, attackerRest,
                                         defender, defenderRecoil, defenderRest,
                                         CombatReturnSeconds, 2);

            FinishCombatMotion();
        }

        private static IEnumerator MoveCombatPhase(
            Transform attacker, Vector3 attackerFrom, Vector3 attackerTo,
            Transform defender, Vector3 defenderFrom, Vector3 defenderTo,
            float duration, int easing)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = duration > 0f ? Mathf.Clamp01(elapsed / duration) : 1f;
                float eased = CombatEase(t, easing);
                if (attacker != null)
                    attacker.position = Vector3.LerpUnclamped(attackerFrom, attackerTo, eased);
                if (defender != null)
                    defender.position = Vector3.LerpUnclamped(defenderFrom, defenderTo, eased);
                yield return null;
            }

            if (attacker != null) attacker.position = attackerTo;
            if (defender != null) defender.position = defenderTo;
        }

        private static float CombatEase(float t, int easing)
        {
            switch (easing)
            {
                case 1: return t * t;                         // accelerate into contact
                case 2: return 1f - Mathf.Pow(1f - t, 3f);   // recoil / settle quickly
                default: return t * t * (3f - 2f * t);       // compact windup
            }
        }

        private static float HalfVisualExtent(Transform visual, Vector3 direction)
        {
            Renderer[] renderers = visual.GetComponentsInChildren<Renderer>();
            Bounds bounds = default;
            bool found = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }

            if (!found) return 0.5f;
            Vector3 extent = bounds.extents;
            return Mathf.Abs(direction.x) * extent.x + Mathf.Abs(direction.y) * extent.y;
        }

        private void StopCombatMotion(bool restore)
        {
            if (_combatMotion != null)
                StopCoroutine(_combatMotion);
            if (restore) RestoreCombatPoses();
            _combatMotion = null;
            _combatAttacker = null;
            _combatDefender = null;
            _combatAttackerSorting = null;
        }

        private void FinishCombatMotion()
        {
            RestoreCombatPoses();
            _combatMotion = null;
            _combatAttacker = null;
            _combatDefender = null;
            _combatAttackerSorting = null;
        }

        private void RestoreCombatPoses()
        {
            if (_combatAttacker != null)
            {
                _combatAttacker.localPosition = _combatAttackerRestLocalPosition;
                _combatAttacker.localRotation = _combatAttackerRestLocalRotation;
            }
            if (_combatDefender != null)
            {
                _combatDefender.localPosition = _combatDefenderRestLocalPosition;
                _combatDefender.localRotation = _combatDefenderRestLocalRotation;
            }
            if (_combatAttackerSorting != null)
            {
                _combatAttackerSorting.sortingOrder = _combatAttackerSortingOrder;
                _combatAttackerSorting.enabled = _combatAttackerSortingWasEnabled;
            }
        }

        private void OnTargetingChanged(HashSet<InstanceId> targets)
        {
            foreach (var kvp in _presenters)
            {
                var binder = kvp.Value != null ? kvp.Value.GetComponent<CardMaterialBinder>() : null;
                binder?.SetTargetOverride(targets == null ? (bool?)null : targets.Contains(kvp.Key));
            }
        }

        /// <summary>
        /// Light up a set of entities without an InputRouter driving it.
        ///
        /// The decision-review tool renders a state nobody is playing — no session, no runner, no
        /// input — but still has to show which cards an action names. That is exactly what
        /// targeting already draws, so this hands the same two primitives a caller that is not a
        /// router. Pass null to clear.
        ///
        /// Nothing in a match calls this, and it changes no behaviour when unused. If a second
        /// affordance ever needs the same treatment, make the pair below internal rather than
        /// growing a third entry point.
        /// </summary>
        public void SetReviewHighlight(HashSet<InstanceId> targets, InstanceId? focus)
        {
            OnTargetingChanged(targets);
            OnTargetFocusChanged(focus);
        }

        private void OnTargetFocusChanged(InstanceId? target)
        {
            foreach (var kvp in _presenters)
            {
                var binder = kvp.Value != null ? kvp.Value.GetComponent<CardMaterialBinder>() : null;
                binder?.SetTargetFocus(target.HasValue && target.Value.Equals(kvp.Key));
            }
        }

        // #236: the played card's hand presenter is still live here — MatchRunner holds
        // OnStateUpdated for this entry until the reveal beat finishes, so without this the
        // original sits in the fan for the whole hold while VfxDirector flies a second copy of
        // it to the board. Hide it the instant the beat starts; GetOrCreate below reactivates
        // whichever presenter the next real snapshot resolves it to, board or hand alike, so a
        // fizzled/rejected play (no state change) can't strand it invisible.
        public void OnPlayReveal(PlayReveal reveal)
        {
            if (_presenters.TryGetValue(reveal.Source, out CardPresenter p) && p != null)
                p.gameObject.SetActive(false);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_db == null) return;

            // At zero playback delay another snapshot can arrive before the 0.39s motion finishes.
            // A newer authoritative pose wins; never let the old coroutine fight the redraw.
            if (_combatMotion != null) StopCombatMotion(restore: true);
            _currentState = state;

            // Defensive: if the end-of-frame flush could not run (component disabled mid-tick,
            // coroutine stopped), resolve last tick's parked ids now — while _diedThisTick still
            // describes THEM — rather than carrying them into this tick's answer.
            if (_pendingRemoval.Count > 0) FlushRemovals();
            _diedThisTick.Clear();

            _thisLive.Clear();
            _drawsThisTick = 0;

            foreach (PlayerState ps in state.Players)
            {
                // Attack-affordance context (CardLiveState.BuildReadiness): only the local
                // human's minions, on their own Main turn, show ready-glow / exhausted-dim.
                bool ownerMayAct = ps.Id == _localHumanId
                    && state.ActivePlayer == ps.Id
                    && state.Phase == TurnPhase.Main;

                int spellDamageBonus = ArcaneSurge.BonusFor(state, ps.Id);
                SyncZone(ps.Id, ps.Board, Zone.Board, ownerMayAct, spellDamageBonus);
                SyncZone(ps.Id, ps.Hand,  Zone.Hand,  ownerMayAct, spellDamageBonus);
                // ps.Weapon is deliberately NOT synced and deliberately NOT added to _thisLive.
                // HeroSocketsView draws the equipped weapon as the forged ring beside the hero power,
                // so a CardPresenter for it is a second copy of the same weapon sitting directly
                // behind the first — which is what it looked like (Sam, watching it live, 2026-07-25).
                //
                // Marking it live would mark the HAND card live: an equipped weapon is the same
                // runtime CardInstance, with the same InstanceId, that was in hand a moment ago
                // (dev-docs/specs/card-schema.md, "What a weapon can and cannot do"). The sweep below
                // would then never park it, nothing would destroy its presenter, and the weapon
                // would sit in the hand fan for the rest of the match — GH #166, "Searing Quill
                // remains in hand after use". The `_thisLive.Add(weapon.Id)` that did exactly that
                // was correct when written (weapons were a separate WeaponInstance with its own id,
                // so keeping a never-registered id alive was free) and became a bug at 1157c997,
                // which unified weapons onto CardInstance and collapsed the two ids into one.
                //
                // RETIREMENT TRIGGER: registering a presenter for the equipped weapon means adding
                // a sync call back HERE, and it cannot be the old one — "keep the weapon view alive"
                // and "destroy the hand view" are the same id and cannot both be one _thisLive
                // entry. Whoever does it updates this comment and the card-schema weapon section.
            }

            // Park presenters for cards that left hand and board — resolved at end of frame, once
            // this tick's CardDestroyed facts have told us WHICH of them died (see _pendingRemoval).
            foreach (InstanceId id in _lastLive)
                if (!_thisLive.Contains(id))
                    _pendingRemoval.Add(id);

            if (_pendingRemoval.Count > 0)
            {
                if (isActiveAndEnabled && _removalFlush == null)
                    _removalFlush = StartCoroutine(FlushRemovalsAtEndOfFrame());
                else if (!isActiveAndEnabled)
                    FlushRemovals();   // no coroutine possible; take the immediate-destroy path
            }

            // Swap: _lastLive becomes the current live set for the next tick.
            _lastLive.Clear();
            foreach (InstanceId id in _thisLive) _lastLive.Add(id);
        }

        // ── Private helpers ─────────────────────────────────────────────────────────

        // The local human's seat has not confirmed its mulligan yet — the same test as
        // DragTargetingModel.IsMulliganActive, which gates the taps. Only when a router exists:
        // without one nobody can tap or confirm, so there is nothing to lay out for.
        private bool LocalMulliganPending(GameState state)
        {
            if (_inputRouter == null || state == null || state.Phase != TurnPhase.Mulligan) return false;
            int seat = state.SeatOf(_localHumanId);
            return !(seat == 0 ? state.Mulligan0Done : state.Mulligan1Done);
        }

        private void SyncZone(PlayerId owner, List<CardInstance> cards, Zone zone, bool ownerMayAct,
                              int spellDamageBonus)
        {
            // Projected hidden hand: every entry is the redaction sentinel (id 0, empty
            // DefinitionId — a4 design §6). _db.Get("") would THROW and abort the whole tick,
            // so sentinel hands take the positional card-back path instead.
            if (zone == Zone.Hand)
            {
                // Re-solve the fan BEFORE any hand anchor is resolved this tick: the fan's
                // pitch is a function of hand count, so every card moves when one is drawn
                // or played (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8).
                //
                // While the local human is still choosing a mulligan, their hand is the head-on
                // row instead (#401). Presentation only: the same presenters sit in the same
                // slots, so taps, marks and the confirmed intent are unchanged.
                boardAnchors?.SetHandCount(owner, cards.Count,
                    mulliganRow: owner == _localHumanId && LocalMulliganPending(_currentState));

                bool hidden = cards.Count > 0 && cards[0].Id.Value == 0;
                SyncHiddenHand(owner, hidden ? cards.Count : 0);
                if (hidden) return;
            }
            else if (zone == Zone.Board)
            {
                // Re-solve the row BEFORE any board anchor is resolved this tick, same reasoning
                // as the hand fan above: center the occupied slots instead of packing them against
                // the leftmost ones (Sam, 2026-08-09).
                boardAnchors?.SetBoardCount(owner, cards.Count);
            }

            for (int i = 0; i < cards.Count; i++)
            {
                CardInstance ci = cards[i];
                _thisLive.Add(ci.Id);
                GetOrCreate(ci, owner, zone, i, ownerMayAct, spellDamageBonus);
            }
        }

        // Maintains N card-back presenters for a hidden hand: no def bind, no art, no clickable,
        // no CardMaterialBinder, no CardLiveState — a card-back must never exist with id 0 or
        // redacted events would light it up (a4 design §6 / §11 finding 3).
        private void SyncHiddenHand(PlayerId owner, int count)
        {
            if (!_cardBacks.TryGetValue(owner, out List<CardPresenter> backs))
            {
                if (count == 0) return;
                backs = new List<CardPresenter>();
                _cardBacks[owner] = backs;
            }

            while (backs.Count > count)
            {
                CardPresenter p = backs[backs.Count - 1];
                backs.RemoveAt(backs.Count - 1);
                if (p != null) DestroyObj(p.gameObject);
            }

            while (backs.Count < count)
            {
                int slot = backs.Count;
                Transform anchor = boardAnchors != null
                    ? boardAnchors.AnchorFor(owner, Zone.Hand, slot)
                    : transform;

                CardPresenter presenter = CreatePresenter(anchor != null ? anchor : transform);
                var binder = presenter.GetComponent<CardMaterialBinder>();
                if (binder != null) DestroyObj(binder);
                presenter.SetHidden();
                SetCardSorting(presenter, Zone.Hand, slot);
                LaunchDrawFlight(presenter.transform, owner);
                backs.Add(presenter);
            }
        }

        // Sends a just-spawned hand card flying in from its owner's deck pile. Silent no-op when
        // there is no DeckAnchor (a stripped test scene), because a missing decoration must never
        // cost a card its position.
        private void LaunchDrawFlight(Transform card, PlayerId owner)
        {
            Transform deck = boardAnchors != null ? boardAnchors.AnchorFor(owner, Zone.Deck, 0) : null;
            if (deck == null) return;
            CardDrawFlight.Launch(card, deck.position, _drawsThisTick * DrawStagger);
            _drawsThisTick++;
        }

        private void GetOrCreate(CardInstance ci, PlayerId owner, Zone zone, int slotIndex,
                                 bool ownerMayAct, int spellDamageBonus)
        {
            bool spawned = false;
            if (!_presenters.TryGetValue(ci.Id, out CardPresenter presenter))
            {
                spawned = true;
                Transform anchor = boardAnchors != null
                    ? boardAnchors.AnchorFor(owner, zone, slotIndex)
                    : transform;

                presenter = CreatePresenter(anchor);
                var live = new CardLiveState(ci.Id);
                _liveStates[ci.Id] = live;

                CardDefinition def = _db.Get(ci.DefinitionId);
                if (def != null)
                {
                    presenter.Bind(def, live, _db);
                    _artProvider?.LoadArt(def.ArtId, presenter.SetArt);
                }

                presenter.GetComponent<CardMaterialBinder>()?.Init(live, _matchRunner);

                // Attach the raycast descriptor when InputRouter is present (hot-seat mode) —
                // InputRouter reads identity off it after its own raycast (EF-048).
                ClickableCard clickable = null;
                if (_inputRouter != null)
                {
                    clickable = presenter.gameObject.AddComponent<ClickableCard>();
                    clickable.Init(ci.Id, owner, zone, presenter);
                }

                if (def != null && _revealController != null)
                {
                    var reveal = presenter.gameObject.AddComponent<CardRevealTrigger>();
                    reveal.Init(ci.Id, def, live, clickable, _revealController);
                }

                _presenters[ci.Id] = presenter;
            }
            else if (_inputRouter != null)
            {
                // Keep zone current for hand→board transitions.
                var clickable = presenter.GetComponent<ClickableCard>();
                clickable?.UpdateZone(zone);
            }

            // #236: undoes OnPlayReveal's hide. Every card still live in this snapshot passes
            // through here every tick, so a presenter parked mid-reveal always resurfaces the
            // moment its real state lands — wherever that turns out to be, not only the board
            // slot the reveal was flying toward.
            if (!presenter.gameObject.activeSelf) presenter.gameObject.SetActive(true);

            // Update live state regardless (stats may have changed).
            if (_liveStates.TryGetValue(ci.Id, out CardLiveState liveState))
                liveState.UpdateFrom(ci, zone == Zone.Board && ownerMayAct, spellDamageBonus);

            // Large perimeter status treatments belong only to played minions. This also handles
            // hand→board moves where the AuraSet itself did not change and emitted no aura event.
            presenter.GetComponent<CardMaterialBinder>()?.SetBoardStatusEffectsVisible(zone == Zone.Board);

            // Re-parent to the correct anchor if the card moved slots.
            if (boardAnchors != null)
            {
                Transform targetAnchor = boardAnchors.AnchorFor(owner, zone, slotIndex);
                if (targetAnchor != null && presenter.transform.parent != targetAnchor)
                {
                    presenter.transform.SetParent(targetAnchor, false);
                    presenter.transform.localPosition = Vector3.zero;
                    presenter.transform.localRotation = Quaternion.identity;
                }
            }

            SetCardSorting(presenter, zone, slotIndex,
                hoverAllowed: boardAnchors == null || !boardAnchors.IsMulliganRow(owner));

            // A card that appears in hand having had no presenter last tick was drawn (or
            // generated straight to hand, which deals the same way). Must run AFTER the
            // re-parent block above — the flight resolves its origin in the anchor's local
            // space and zeroes the card into it.
            if (spawned && zone == Zone.Hand) LaunchDrawFlight(presenter.transform, owner);

            // Front-row bulwark (decision 2026-07-17): bulwarked board minions step toward
            // the centerline so blockers read spatially, not by glow color. Re-applied
            // every sync — bulwark can be gained/lost without a slot change.
            if (zone == Zone.Board)
            {
                float toward = (owner == _p0Id) ? 1f : -1f;
                bool hasBulwark = ci.Keywords.Contains(Keyword.BULWARK);
                presenter.transform.localPosition =
                    hasBulwark ? new Vector3(0f, toward * BulwarkRowOffset, 0f) : Vector3.zero;
            }
        }

        // Hand cards overlap, so their draw order has to be explicit. Board cards never overlap
        // and sit at the group default; the fan's per-index z-stagger (HandFanLayout) keeps
        // opaque materials and the physics hit-test agreeing with this ordering.
        //
        // Hover-reveal is hand-only. Disabling the component on the board path is what returns a
        // played card to its resting pose — HandCardHover.OnDisable snaps the visual back, so a
        // card can never land on the board still lifted and enlarged.
        //
        // hoverAllowed is false for the mulligan row (#401). Its cards sit side by side in a band
        // sized to hold them, so the lift and the 1.2x emphasis would push a card into its
        // neighbour and off the top of the band. Disabling the component also drops the hand's
        // RestScale and collider back to board size, which is what MulliganRowLayout scales from.
        private static void SetCardSorting(CardPresenter presenter, Zone zone, int slotIndex,
                                           bool hoverAllowed = true)
        {
            bool inHand = zone == Zone.Hand;
            int  order  = inHand ? HandSortBase + slotIndex : 0;

            // For a hand card the hover component OWNS sortingOrder, because it may be holding a
            // boost this method knows nothing about. Writing the base order here first would stomp
            // that boost on every state tick and silently kill the re-sort mid-hover.
            var hover = presenter.GetComponent<HandCardHover>();
            if (hover != null)
            {
                hover.enabled = inHand && hoverAllowed;
                if (hover.enabled)
                {
                    hover.SetBaseOrder(order);
                    return;
                }
            }

            var group = presenter.GetComponent<UnityEngine.Rendering.SortingGroup>();
            if (group != null) group.sortingOrder = order;
        }

        private IEnumerator FlushRemovalsAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            _removalFlush = null;
            FlushRemovals();
        }

        // Every parked id is resolved together, so a board wipe burns as one event rather than a
        // cascade of one-at-a-time destroys — each dying card gets its own snapshot, its own
        // material instance and its own coroutine, and they overlap.
        private void FlushRemovals()
        {
            for (int i = 0; i < _pendingRemoval.Count; i++)
            {
                InstanceId id = _pendingRemoval[i];
                DestroyPresenter(id, died: _diedThisTick.Contains(id));
            }
            _pendingRemoval.Clear();
        }

        /// <param name="died">
        /// True when this tick raised CardDestroyed for the id. Cards that merely LEFT the board — played
        /// from hand, returned to hand, discarded, shuffled back — keep the immediate destroy: a
        /// burn would read as "this card was killed", which for those is a lie.
        /// </param>
        private void DestroyPresenter(InstanceId id, bool died = false)
        {
            if (_presenters.TryGetValue(id, out CardPresenter p))
            {
                _presenters.Remove(id);   // out of the diff immediately, even while it burns
                if (p != null)
                {
                    // A card that dies from its own play-reveal batch (e.g. a self-sacrifice
                    // effect) can still be OnPlayReveal-hidden here: GetOrCreate never ran for it
                    // (a dead card is in neither Board nor Hand), so nothing undid the hide. A
                    // dissolve on an inactive GameObject never starts its coroutine and would
                    // leak the object invisibly forever — reactivate before the burn either way.
                    if (!p.gameObject.activeSelf) p.gameObject.SetActive(true);

                    if (died && Application.isPlaying && TryStartDissolve(p))
                    {
                        _liveStates.Remove(id);
                        return;
                    }
                    p.Unbind();
                    DestroyObj(p.gameObject);
                }
            }
            _liveStates.Remove(id);
        }

        // The card root outlives the state that justified it for DeathDissolveDur seconds, so
        // everything that could still act on it is switched off first: no clicks, no hit-test, no
        // hover lift, no shader binder chasing a live state that has already been dropped.
        private bool TryStartDissolve(CardPresenter p)
        {
            Transform visual = p.transform.Find("Visual");
            if (visual == null) return false;

            p.Unbind();
            // DESTROYED, not disabled. A disabled MonoBehaviour still receives C# delegate
            // callbacks, so a merely-disabled binder stays subscribed to MatchRunner.OnMatchEvent
            // and would try to StartCoroutine on itself — which errors. Destroy runs its OnDestroy,
            // which is the only thing that actually unsubscribes.
            var binder = p.GetComponent<CardMaterialBinder>();
            if (binder != null) DestroyObj(binder);
            var hover = p.GetComponent<HandCardHover>();
            if (hover != null) hover.enabled = false;
            var clickable = p.GetComponent<ClickableCard>();
            if (clickable != null) clickable.enabled = false;
            var col = p.GetComponent<BoxCollider>();
            if (col != null) col.enabled = false;

            GameObject root = p.gameObject;
            return root.AddComponent<CardDeathDissolve>()
                       .Play(visual, DeathDissolveDur, () => DestroyObj(root));
        }

        // CreatePresenter runs from the offscreen dissolve preview as well as from play mode, and
        // edit mode refuses Destroy — without this the harness would build every card with the
        // primitives' auto-generated MeshColliders still attached, and log an error per quad.
        private static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        /// <summary>
        /// Builds ONE detached card visual under <paramref name="parent"/> through the shipping
        /// <see cref="CreatePresenter"/> — the same renderers, the same frame layer, the same
        /// TMP labels a card in a match gets — and hands back its presenter, unbound.
        ///
        /// DETACHED MEANS NOT IN THE MATCH. It is registered in nothing: not _presenters, not
        /// _liveStates, not _thisLive. The diffing loop therefore cannot see it, cannot re-parent
        /// it to an anchor, and cannot destroy it. It carries no ClickableCard and its
        /// CardMaterialBinder is never Init'd, so nothing drives it and it cannot act — the caller
        /// owns its lifetime and calls Bind/SetArt itself.
        ///
        /// It exists so <see cref="CardFaceBaker"/> can photograph the REAL card for the collection
        /// wall instead of the wall re-implementing the card front in UI Toolkit. Two
        /// implementations of one card face would drift on the first frame-art change; one
        /// implementation and a camera cannot.
        ///
        /// <see cref="Initialize"/> must have run first: the frame/rarity sprites resolve through
        /// the IUiSpriteProvider that Initialize picks (Editor vs Addressables).
        /// </summary>
        public CardPresenter BuildDetachedCard(Transform parent) => CreatePresenter(parent);

        /// <summary>Builds and binds a non-interactive copy of a live card for the board focus
        /// view. A fresh presenter per focused card makes an asynchronous art callback harmless:
        /// if the player moves focus before the load finishes, the old presenter is destroyed and
        /// Unity's fake-null guard below drops the stale result.</summary>
        internal CardPresenter BuildFocusedCard(Transform parent, CardDefinition def,
                                                 ICardLiveState live)
        {
            if (def == null || live == null) return null;

            CardPresenter presenter = CreatePresenter(parent);

            // A focus card is display-only. Its root collider must not steal the release from the
            // board card or target underneath it, and none of the gameplay-only root components
            // need to run on this duplicate.
            var binder = presenter.GetComponent<CardMaterialBinder>();
            if (binder != null) { binder.enabled = false; DestroyObj(binder); }
            var hover = presenter.GetComponent<HandCardHover>();
            if (hover != null) { hover.enabled = false; DestroyObj(hover); }
            var collider = presenter.GetComponent<BoxCollider>();
            if (collider != null) { collider.enabled = false; DestroyObj(collider); }

            presenter.Bind(def, live, _db);
            _artProvider?.LoadArt(def.ArtId, sprite =>
            {
                if (presenter != null) presenter.SetArt(sprite);
            });
            return presenter;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Builds one real card through the shipping <see cref="CreatePresenter"/>/GetOrCreate path
        /// for Kingdom/Preview Card Dissolve. Editor-only: the preview must burn the actual card
        /// hierarchy — every renderer, real frame art, real TMP labels — because a mock card would
        /// only ever prove the harness agrees with itself.
        /// </summary>
        public CardPresenter EditorBuildCard(CardInstance ci, PlayerId owner, Zone zone)
        {
            _thisLive.Add(ci.Id);
            GetOrCreate(ci, owner, zone, 0, ownerMayAct: false, spellDamageBonus: 0);
            return _presenters.TryGetValue(ci.Id, out CardPresenter p) ? p : null;
        }
#endif

        // Builds a minimal world-space card visual hierarchy (no external prefab needed).
        // Phase C step 5: upgraded to TextMeshPro. Replace primitives with shader-driven prefab when card art lands.
        private CardPresenter CreatePresenter(Transform parent)
        {
            var root = new GameObject("Card");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            // One SortingGroup re-layers the whole card — quads, sprites and TMP labels
            // together — off a single integer, which is what makes the fan's overlap (and
            // later the hover-reveal in hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8) a one-line change.
            root.AddComponent<UnityEngine.Rendering.SortingGroup>();

            // Physical backing. The painted card face stays on the existing quads at z <= 0;
            // this shallow lit box begins immediately behind them on +z. From the tabletop
            // camera that exposes a dark lower/side lip without changing any face layout,
            // collider, sorting order or interaction coordinate.
            const float cardDepth = 0.12f;
            BoardFurnitureDepth.Create(root.transform, "CardDepth",
                new Vector2(CardW, CardH), cardDepth);

            // Background quad (SG_CardComposite body) — remove auto-generated MeshCollider.
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "BG";
            bg.transform.SetParent(root.transform, false);
            bg.transform.localScale    = new Vector3(CardW, CardH, 1f);
            bg.transform.localPosition = Vector3.zero;
            DestroyObj(bg.GetComponent<MeshCollider>());
            var bodyRenderer = bg.GetComponent<Renderer>();
            MaterialCatalog.Assign(bodyRenderer, MaterialCatalog.CardBody);

            // Glow overlay — slightly larger, just in front. SG_Glow material assigned later.
            var glowGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            glowGo.name = "GlowOverlay";
            glowGo.transform.SetParent(root.transform, false);
            glowGo.transform.localScale    = new Vector3(CardW + 0.15f, CardH + 0.15f, 1f);
            glowGo.transform.localPosition = new Vector3(0f, 0f, 0.005f);
            DestroyObj(glowGo.GetComponent<MeshCollider>());
            var glowRenderer = glowGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(glowRenderer, MaterialCatalog.Glow);
            // GO stays active, renderer starts disabled: CardMaterialBinder toggles
            // Renderer.enabled, which is inert on an inactive GameObject.
            glowRenderer.enabled = false;

            // Painted Aegis dome: preserve the 1176x1337 source oval, with room around the
            // card. The transparent shell sits over the face/frame but behind sockets and
            // labels, so the curved rim remains visible where it crosses the card's corners.
            const float aegisAspect = 1176f / 1337f;
            float aegisHeight = CardH * 1.30f;
            var aegisGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            aegisGo.name = "AegisDome";
            aegisGo.transform.SetParent(root.transform, false);
            aegisGo.transform.localScale    = new Vector3(aegisHeight * aegisAspect, aegisHeight, 1f);
            aegisGo.transform.localPosition = new Vector3(0f, 0f, -0.007f);
            DestroyObj(aegisGo.GetComponent<MeshCollider>());
            var aegisRenderer = aegisGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(aegisRenderer, MaterialCatalog.Aegis);
            aegisRenderer.sortingOrder = FrameRarityCatalog.FrameOrder;
            // Keep the GO active: the binder controls the renderer and its gain/break tween.
            aegisRenderer.enabled = false;

            // Art window — upper card face, in front of BG, behind labels. Texture assigned
            // via CardPresenter.SetArt when the IArtProvider resolves; hidden until then.
            var artGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            artGo.name = "Art";
            artGo.transform.SetParent(root.transform, false);
            // Derived from the one ArtWindow rather than restated: CardPresenter.Layout overwrites
            // both of these on Bind, so a second hand-written copy is invisible when it drifts —
            // and it had, by 2026-08-16, to a window two frame revisions old.
            Vector4 artWin = FrameRarityCatalog.ArtWindow; // l, t, r, b
            artGo.transform.localScale    = new Vector3((artWin.z - artWin.x) * CardW,
                                                        (artWin.w - artWin.y) * CardH, 1f);
            artGo.transform.localPosition = new Vector3(
                ((artWin.x + artWin.z) * 0.5f - 0.5f) * CardW,
                (0.5f - (artWin.y + artWin.w) * 0.5f) * CardH, -0.005f);
            DestroyObj(artGo.GetComponent<MeshCollider>());
            var artRenderer = artGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(artRenderer, MaterialCatalog.CardArt);
            artGo.SetActive(false);

            // Frame border — full card face, in front of the (opaque) art quad so the border ring
            // overlaps the illustration edges; its transparent window lets the art show through.
            // Sprite (the one frame_card) assigned by CardPresenter.ApplyFrame (D2).
            var frameRenderer = MakeSprite(root.transform, "Frame",
                new Vector3(0f, 0f, -0.006f), new Vector2(CardW, CardH));
            frameRenderer.sortingOrder = FrameRarityCatalog.FrameOrder;

            // Frost overlay — the frost cue (SG_Frost), card-sized, driven by CardMaterialBinder's
            // _FrostAmount. Its own quad because SG_Frost is an overlay shader: it has _FrostOpacity
            // and _RefractStrength and expects something behind it to frost OVER. The body quad
            // wears SG_CardComposite, which does not declare _FrostAmount at all, so pushing the
            // property there wrote it nowhere.
            //
            // Z = -0.008 puts it in FRONT of the art (-0.005) and frame (-0.006) but BEHIND every
            // TMP label (-0.010) and the gem (-0.012). That is deliberate: a frosted minion still
            // has to be readable, so the ice covers the illustration and never the numbers. At
            // -0.02 it sat in front of the labels and, writing depth on the transparent queue,
            // made them vanish entirely while drawing no ice of its own.
            var frostGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            frostGo.name = "FrostOverlay";
            frostGo.transform.SetParent(root.transform, false);
            frostGo.transform.localScale    = new Vector3(CardW, CardH, 1f);
            frostGo.transform.localPosition = new Vector3(0f, 0f, -0.008f);
            DestroyObj(frostGo.GetComponent<MeshCollider>());
            var frostRenderer = frostGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(frostRenderer, MaterialCatalog.Frost);
            // Same pattern as glow and aegis: active GO, disabled renderer. The binder enables it
            // when AuraSet.Frosted arrives, so an unfrosted card costs no draw call.
            frostRenderer.enabled = false;

            // The shader freezes the card surface; these separate edge clusters give Frost a
            // crystalline silhouette and a readable growth event. They sit in front of the frost
            // quad and frame, but behind sockets and labels so the minion remains playable.
            var frostCrystalRoot = new GameObject("FrostCrystals");
            frostCrystalRoot.transform.SetParent(root.transform, false);
            var frostCrystalLeft = MakeSprite(frostCrystalRoot.transform, "LeftGrowth",
                new Vector3(0f, 0f, -0.009f), new Vector2(CardW + 0.28f, CardH + 0.48f));
            var frostCrystalRight = MakeSprite(frostCrystalRoot.transform, "RightGrowth",
                new Vector3(0f, 0f, -0.009f), new Vector2(CardW + 0.28f, CardH + 0.48f));
            var frostCrystalLower = MakeSprite(frostCrystalRoot.transform, "LowerShelf",
                new Vector3(0f, -CardH * 0.29f, -0.009f),
                new Vector2(CardW + 0.54f, (CardW + 0.54f) / 1.776f));
            // Assigning SpriteRenderer.sprite resets .size to the texture's native PPU extent.
            // These generated pieces are 1024-1671 px, so direct assignment makes each one
            // roughly 10-17 world units and fills most of the board. Restore the card-relative
            // size MakeSprite established, and bind the per-texture sprite material as usual.
            SpriteMaterials.SetPreservingSize(frostCrystalLeft, _uiSprites?.Get(FrostCrystalsLeftSpriteKey));
            SpriteMaterials.SetPreservingSize(frostCrystalRight, _uiSprites?.Get(FrostCrystalsRightSpriteKey));
            SpriteMaterials.SetPreservingSize(frostCrystalLower, _uiSprites?.Get(FrostCrystalsLowerSpriteKey));
            frostCrystalLeft.sortingOrder = FrameRarityCatalog.FrameOrder + 1;
            frostCrystalRight.sortingOrder = FrameRarityCatalog.FrameOrder + 1;
            frostCrystalLower.sortingOrder = FrameRarityCatalog.FrameOrder + 1;
            var frostCrystals = frostCrystalRoot.AddComponent<FrostCrystalView>();
            frostCrystals.Setup(frostCrystalLeft, frostCrystalRight, frostCrystalLower);

            // Bulwark/Taunt status treatment — the approved shields-only concept. The sprite
            // straddles the card with an open centre, in front of the painted frame but behind
            // the stat/tribe sockets and every label. It supplements the established front-row
            // step instead of replacing that spatial rule during this first runtime trial.
            var bulwarkRenderer = MakeSprite(root.transform, "BulwarkShields",
                new Vector3(0f, -CardH * 0.06f, -0.007f),
                new Vector2(CardW + 0.72f, (CardW + 0.72f) / 1.5f));
            SpriteMaterials.SetPreservingSize(bulwarkRenderer, _uiSprites?.Get(BulwarkEffectSpriteKey));
            bulwarkRenderer.sortingOrder = FrameRarityCatalog.FrameOrder + 1;
            bulwarkRenderer.enabled = false;

            // Shroud/Stealth status treatment — three separately generated smoke pieces so the
            // effect can gather around the minion instead of popping on as a static rectangle.
            // Every source image leaves the reading area open and this layer remains behind all
            // labels/sockets; the existing _ShroudAmount body fade supplies the spectral interior.
            var shroudRoot = new GameObject("ShroudSmoke");
            shroudRoot.transform.SetParent(root.transform, false);
            var shroudLeft = MakeSprite(shroudRoot.transform, "LeftPlume",
                new Vector3(0f, 0f, -0.007f), new Vector2(CardW + 0.22f, CardH + 0.42f));
            var shroudRight = MakeSprite(shroudRoot.transform, "RightPlume",
                new Vector3(0f, 0f, -0.007f), new Vector2(CardW + 0.22f, CardH + 0.42f));
            var shroudLower = MakeSprite(shroudRoot.transform, "LowerBank",
                new Vector3(0f, -CardH * 0.29f, -0.007f),
                new Vector2(CardW + 0.62f, (CardW + 0.62f) / 1.777f));
            SpriteMaterials.SetPreservingSize(shroudLeft, _uiSprites?.Get(ShroudSmokeLeftSpriteKey));
            SpriteMaterials.SetPreservingSize(shroudRight, _uiSprites?.Get(ShroudSmokeRightSpriteKey));
            SpriteMaterials.SetPreservingSize(shroudLower, _uiSprites?.Get(ShroudSmokeLowerSpriteKey));
            shroudLeft.sortingOrder = FrameRarityCatalog.FrameOrder + 1;
            shroudRight.sortingOrder = FrameRarityCatalog.FrameOrder + 1;
            shroudLower.sortingOrder = FrameRarityCatalog.FrameOrder + 1;
            var shroudSmoke = shroudRoot.AddComponent<ShroudSmokeView>();
            shroudSmoke.Setup(shroudLeft, shroudRight, shroudLower);

            // Rarity gem — set into the frame's upper-right socket; sprite (gem_{rarity}) and
            // final size assigned per-rarity by ApplyFrame, which is also where the socket
            // geometry is applied. Position and size here are only the pre-frame placeholder
            // fallback for a card with no painted frame; ApplyFrame overrides both the moment
            // one resolves.
            var gemRenderer = MakeSprite(root.transform, "Gem",
                new Vector3(0f, -CardH * StatRowFrac, -0.012f), new Vector2(CardW * 0.16f, CardW * 0.16f));
            gemRenderer.sortingOrder = FrameRarityCatalog.GemOrder;

            // Socket housings — the three digit bosses and the gem bezel, composited OVER the
            // frame rather than painted into it (2026-08-19). That is what lets one frame serve
            // minion, spell and weapon: type only decides which of these get drawn, and the frame
            // behind an undrawn one is finished rail, not a hole. Sprite, size and position are
            // all assigned by CardPresenter.ApplySocketSprites; the values here are placeholders
            // for the pre-frame fallback, and each starts inactive so a card with no painted
            // frame costs no draw call.
            SpriteRenderer MakeSocket(string goName, float z) =>
                MakeSprite(root.transform, goName, new Vector3(0f, 0f, z),
                           new Vector2(CardW * 0.17f, CardH * 0.12f));

            var costSocket   = MakeSocket("CostSocket",   -0.009f);
            var attackSocket = MakeSocket("AttackSocket", -0.009f);
            var powerSword = MakeSocket("PowerSword", -0.0085f);
            var healthSocket = MakeSocket("HealthSocket", -0.009f);
            var gemSocket    = MakeSocket("GemSocket",    -0.011f);
            gemSocket.sortingOrder = FrameRarityCatalog.GemSocketOrder;
            // The cost socket needs the explicit order as much as the other two: left at the
            // frame's own order it loses the tie, and the frame's top-left corner paints over
            // every part of the disc that is not above the art aperture. Invisible while the
            // disc was black on black; half a blue disc once it had a colour (2026-10-03).
            costSocket.sortingOrder   = FrameRarityCatalog.StatSocketOrder;
            powerSword.sortingOrder = FrameRarityCatalog.PowerSwordOrder;
            attackSocket.sortingOrder = FrameRarityCatalog.PowerCircleOrder;
            healthSocket.sortingOrder = FrameRarityCatalog.StatSocketOrder;
            costSocket.gameObject.SetActive(false);
            attackSocket.gameObject.SetActive(false);
            powerSword.gameObject.SetActive(false);
            healthSocket.gameObject.SetActive(false);
            gemSocket.gameObject.SetActive(false);

            // NO KEYWORD ICON STRIP (removed 2026-09-04, Sam: "we aren't doing keyword icons").
            // This built four SpriteRenderers per card below the art window, immediately
            // SetActive(false), for CardPresenter to fill from StatusMarkCatalog. It never filled
            // any: no icon_kw_*.png existed, and the painted-frame branch returned before the
            // strip was reached. Four GameObjects plus four renderers on every card — a whole
            // collection wall's worth — to stay invisible.

            // BoxCollider on root for the router's probe and BoardPointerRelay — sized to card face.
            var col = root.AddComponent<BoxCollider>();
            col.size = new Vector3(CardW, CardH, 0.1f);

            // Name — top strip
            TextMeshPro nameLabel   = MakeLabel(root.transform, "Name", new Vector3(0f,             CardH *  0.39f, -0.01f), 3.0f, Color.white);
            // Cost — top-left boss. White + rim per §3.1; the blue is in the socket disc behind
            // it (FrameRarityCatalog.CostSocketSprite), not in the digit.
            TextMeshPro costLabel   = MakeLabel(root.transform, "Cost", new Vector3(-CardW * 0.38f,  CardH *  StatRowFrac, -0.01f), 3.75f, Color.white, statOutline: true);
            // Stat digits are white with a black rim (§3.1) — the rim is what keeps them
            // legible on any frame's boss art, any rarity.
            // Attack — bottom-left (minions + weapons)
            TextMeshPro attackLabel = MakeLabel(root.transform, "Atk",  new Vector3(-CardW * 0.35f, -CardH *  StatRowFrac, -0.01f), 3.75f, Color.white, statOutline: true);
            // Health — bottom-right (minions only)
            TextMeshPro healthLabel = MakeLabel(root.transform, "HP",   new Vector3( CardW * 0.35f, -CardH *  StatRowFrac, -0.01f), 3.75f, Color.white, statOutline: true);
            // Durability — bottom-right (weapons only; occupies the health slot, mutually exclusive)
            TextMeshPro durabilityLabel = MakeLabel(root.transform, "Dur", new Vector3( CardW * 0.35f, -CardH *  StatRowFrac, -0.01f), 3.75f, Color.white, statOutline: true);
            // Keywords — center strip
            TextMeshPro kwLabel     = MakeLabel(root.transform, "KW",   new Vector3(0f,             -CardH *  0.08f, -0.01f), 1.8f, new Color(0.9f, 0.85f, 0.6f));

            // Rules text — band between the keyword icon strip and the stat row. Unlike
            // MakeLabel's single-line names this wraps; auto-sizes down for long lines.
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);
            bodyGo.transform.localPosition = new Vector3(0f, -CardH * 0.30f, -0.01f);
            var bodyLabel = bodyGo.AddComponent<TextMeshPro>();
            bodyGo.GetComponent<Renderer>().sortingOrder = 10; // over the frame, same as MakeLabel
            var bodyFont = BodyFont();   // EF-050: text face here, display face everywhere else
            if (bodyFont != null) bodyLabel.font = bodyFont;
            bodyLabel.fontSize  = 1.0f;
            // Centered across, TOP down the band — not TextAlignmentOptions.Center (Sam,
            // 2026-08-13). The band runs y 1369..1833 of the 2048 master (0.668..0.895 of the
            // card, FrameRarityCatalog.BodyCenterY/BodyHeight), while the stat sockets sit at
            // 0.934/0.933 and their rings reach up to ~0.87. Vertically centering meant a SHORT
            // line floated to the middle of that band and a two-liner grew DOWNWARD into the
            // attack/health frames — the shorter the text, the closer to the collision. Growing
            // from the top instead spends the empty space at the bottom, where the stats already
            // are, so a one-word rule sits clear of them and only genuinely long text reaches down.
            bodyLabel.alignment = TextAlignmentOptions.Top;
            bodyLabel.color     = new Color(0.92f, 0.92f, 0.92f);
            bodyLabel.rectTransform.sizeDelta = new Vector2(CardW * 0.90f, CardH * 0.22f);
            // Keep the first line off the painted middle rail without moving the whole rules box
            // toward the stat sockets. TMP's own top margin reduces the usable content area in
            // place, so the bottom edge and the hand-fan clearance budget stay unchanged.
            // Halved from 0.025 (Sam, 2026-09-28): long rules text ran off the bottom while the
            // top margin held nearly a whole line of empty parchment.
            bodyLabel.margin = new Vector4(0f, CardH * 0.0125f, 0f, 0f);
            bodyLabel.textWrappingMode = TextWrappingModes.Normal;
            bodyLabel.overflowMode     = TextOverflowModes.Ellipsis;
            bodyLabel.enableAutoSizing = true;
            bodyLabel.fontSizeMax      = 1.0f;
            bodyLabel.fontSizeMin      = 0.5f;

            // The tribe plate stretches UNDER the stat frames, which mask both ends.
            // Shared by match and collection cards; no fitted end cutouts are needed.
            // Sam's transparent chrome includes the text bed; preserve its alpha without a fill quad.
            var tribeBanner = new GameObject("TribeBanner");
            tribeBanner.transform.SetParent(root.transform, false);
            tribeBanner.transform.localPosition = new Vector3(0f,
                (0.5f - FrameRarityCatalog.TribeCenterY) * CardH, -0.00825f);
            float tribeWidth = FrameRarityCatalog.TribeWidth * CardW;
            float tribeHeight = FrameRarityCatalog.TribeHeight * CardH;
            var tribePanel = MakeSprite(tribeBanner.transform, "Plate",
                new Vector3(FrameRarityCatalog.TribeCanvasOffset.x * tribeWidth,
                    FrameRarityCatalog.TribeCanvasOffset.y * tribeHeight, -0.00025f),
                new Vector2(FrameRarityCatalog.TribeCanvasScale.x * tribeWidth,
                    FrameRarityCatalog.TribeCanvasScale.y * tribeHeight));
            tribePanel.sortingOrder = FrameRarityCatalog.TribePanelOrder;
            var tribeLabel = MakeLabel(tribeBanner.transform, "Tribe",
                new Vector3(0f, 0f, -0.00175f), FrameRarityCatalog.TribeEm * CardH * 10f,
                FrameRarityCatalog.NameParchment);
            if (bodyFont != null) tribeLabel.font = bodyFont;
            tribeLabel.fontStyle = FontStyles.Bold;
            tribeLabel.rectTransform.sizeDelta = new Vector2(
                FrameRarityCatalog.TribeTextWidth * CardW,
                tribeHeight - 24f / FrameRarityCatalog.MasterH * CardH);
            tribeLabel.enableAutoSizing = true;
            tribeLabel.fontSizeMax = FrameRarityCatalog.TribeEm * CardH * 10f;
            tribeLabel.fontSizeMin = FrameRarityCatalog.TribeEmFloor * CardH * 10f;
            tribeBanner.SetActive(false);

            // Tooltip banner — the card's full/original title (CardDefinition.Tooltip), shown only
            // on hover for a card whose printed Name was shortened to fit the face (Card Title
            // Length Audit, 2026-08-09). Sits at the very top edge, in front of every other layer
            // (z nearer the camera than the gem, the nearest thing before it) so it reads as a
            // solid strip laid over the name/cost bosses rather than a fourth thing competing with
            // them. Hidden by default; CardPresenter toggles it on pointer enter/exit.
            var tooltipBgGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            tooltipBgGo.name = "TooltipBg";
            tooltipBgGo.transform.SetParent(root.transform, false);
            tooltipBgGo.transform.localScale    = new Vector3(CardW * 0.94f, CardH * 0.13f, 1f);
            tooltipBgGo.transform.localPosition = new Vector3(0f, CardH * 0.435f, -0.02f);
            DestroyObj(tooltipBgGo.GetComponent<MeshCollider>());
            var tooltipBgRenderer = tooltipBgGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(tooltipBgRenderer, MaterialCatalog.Plate);
            var tooltipMpb = new MaterialPropertyBlock();
            tooltipBgRenderer.GetPropertyBlock(tooltipMpb);
            tooltipMpb.SetColor("_BaseColor", new Color(0.04f, 0.04f, 0.06f, 0.92f));
            tooltipBgRenderer.SetPropertyBlock(tooltipMpb);
            tooltipBgGo.SetActive(false);

            var tooltipLabelGo = new GameObject("TooltipText");
            tooltipLabelGo.transform.SetParent(root.transform, false);
            tooltipLabelGo.transform.localPosition = new Vector3(0f, CardH * 0.435f, -0.021f);
            var tooltipLabel = tooltipLabelGo.AddComponent<TextMeshPro>();
            tooltipLabelGo.GetComponent<Renderer>().sortingOrder = 20; // over TooltipBg and every other layer
            var tooltipFont = BodyFont();
            if (tooltipFont != null) tooltipLabel.font = tooltipFont;
            tooltipLabel.alignment        = TextAlignmentOptions.Center;
            tooltipLabel.color            = Color.white;
            tooltipLabel.rectTransform.sizeDelta = new Vector2(CardW * 0.88f, CardH * 0.12f);
            tooltipLabel.textWrappingMode  = TextWrappingModes.NoWrap;
            tooltipLabel.overflowMode      = TextOverflowModes.Ellipsis;
            tooltipLabel.enableAutoSizing  = true;
            tooltipLabel.fontSizeMax       = 1.4f;
            tooltipLabel.fontSizeMin       = 0.8f;

            // Wrap every visual under one child transform. Hover-reveal lifts and scales THIS,
            // never the root — the BoxCollider above stays on the root and therefore never moves,
            // which is what makes the reveal flicker-proof (see HandCardHover). Re-parenting after
            // the fact keeps the construction above untouched; the wrapper sits at identity, so
            // every local pose already computed is still correct.
            var visualGo = new GameObject("Visual");
            visualGo.transform.SetParent(root.transform, false);
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = root.transform.GetChild(i);
                if (child != visualGo.transform) child.SetParent(visualGo.transform, true);
            }

            var hover = root.AddComponent<HandCardHover>();
            hover.Init(visualGo.transform, root.GetComponent<UnityEngine.Rendering.SortingGroup>());
            hover.enabled = false;   // hand-only; ConfigureForZone turns it on

            var presenter = root.AddComponent<CardPresenter>();
            presenter.Setup(nameLabel, costLabel, attackLabel, healthLabel, durabilityLabel, kwLabel, bodyRenderer, artRenderer, bodyLabel);
            presenter.SetupFrameLayer(frameRenderer, gemRenderer, _uiSprites,
                                      costSocket, attackSocket, healthSocket, gemSocket, powerSword);
            presenter.SetupTribe(tribeBanner, tribeLabel, tribePanel);
            // DISABLED 2026-09-08 (Sam): the on-card name-overflow banner sticks forever on touch
            // and covers the mana cost (#362). Leaving tooltipBanner/tooltipLabel unwired on
            // CardPresenter keeps its pointer-enter check false; it is already null-safe.
            // presenter.SetupTooltip(tooltipBgGo, tooltipLabel);

            // CardMaterialBinder drives shader MPB floats; Init() called after ICardLiveState exists.
            var binder = root.AddComponent<CardMaterialBinder>();
            binder.SetupRenderers(bodyRenderer, glowRenderer, aegisRenderer, frostRenderer,
                                  bulwarkRenderer, shroudSmoke, frostCrystals);

            return presenter;
        }

        // Creates a world-space SpriteRenderer child sized to exact world dimensions via Sliced
        // draw mode (PPU-independent). Sprite is assigned later by CardPresenter; starts empty.
        private static SpriteRenderer MakeSprite(Transform parent, string goName, Vector3 localPos, Vector2 worldSize)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.drawMode = SpriteDrawMode.Sliced; // lets .size set exact world dimensions regardless of PPU
            sr.size     = worldSize;
            return sr;
        }

        // One shared outlined material for every stat label — per-label .fontMaterial would
        // instance a material per digit and break batching across the whole board.
        private static Material s_statLabelMaterial;

        // EF-050 display face for card names, stats, and digits. Loaded once;
        // TMP's default font remains the fallback when the asset is unavailable.
        private static TMP_FontAsset s_cardFont;

        // Internal, not private: DeckPileView's hover count is a card-face digit by any other
        // name and has to be the same typeface — a second copy of this loader would be a second
        // thing to forget when the display face changes.
        internal static TMP_FontAsset CardFont()
        {
            if (s_cardFont == null)
                s_cardFont = LoadAsset<TMP_FontAsset>(
                    "Assets/Art/Fonts/AlfaSlabOne-Regular SDF.asset", "fonts/AlfaSlabOne-Regular SDF");
            return s_cardFont;
        }

        // EF-050: two typefaces, two roles. CardFont() above is the DISPLAY face and owns the
        // card name and the stat digits; this is the TEXT face and owns the rules box only.
        //
        // Why the split is not taste: at 265 px card width (the true size on the smallest
        // supported phone) body text runs at roughly 1.6x the letter-recognition acuity
        // threshold. There is no margin at that ratio, and a display face spends all of it —
        // slab strokes thicken and counters close first at small sizes, which is exactly the
        // failure mode you cannot see on a monitor.
        //
        // The bed-margin overrun that used to affect this label was a separate bug in the bed
        // and font-window size calculation, already fixed. Do not re-diagnose it as a wrapping
        // or font-width problem.
        private static TMP_FontAsset s_bodyFont;

        internal static TMP_FontAsset BodyFont()
        {
            if (s_bodyFont == null)
                s_bodyFont = LoadAsset<TMP_FontAsset>(
                    "Assets/Art/Fonts/GoogleSansFlex-Card SDF.asset", "fonts/GoogleSansFlex-Card SDF");
            return s_bodyFont ?? CardFont();   // missing asset must not blank the rules box
        }

        // The board-label faces: turn buttons, hero and weapon numbers, mulligan, match end, versus,
        // loading, Unearth. Two static cuts of Google Sans Flex (tooling/scripts/instance_font.py,
        // tuned in the Font Bench), because TMP cannot set a variable font's weight per label.
        // Before these existed every one of those labels fell back to TMP's LiberationSans.
        private static TMP_FontAsset s_uiFont, s_uiBoldFont;

        /// <summary>Gives a board label its UI face. A label already styled Bold gets the bold CUT
        /// and loses TMP's faux bold, which would otherwise thicken the real bold a second time. A
        /// missing asset leaves the label on TMP's default rather than blank.</summary>
        internal static void ApplyUiFont(TMP_Text label)
        {
            bool bold = (label.fontStyle & FontStyles.Bold) != 0;
            TMP_FontAsset font = bold ? UiBoldFont() : UiFont();
            if (font == null) return;
            label.font = font;
            if (bold) label.fontStyle &= ~FontStyles.Bold;
        }

        private static TMP_FontAsset UiFont()
        {
            if (s_uiFont == null)
                s_uiFont = LoadAsset<TMP_FontAsset>(
                    "Assets/Art/Fonts/GoogleSansFlex-UI SDF.asset", "fonts/GoogleSansFlex-UI SDF");
            return s_uiFont;
        }

        private static TMP_FontAsset UiBoldFont()
        {
            if (s_uiBoldFont == null)
                s_uiBoldFont = LoadAsset<TMP_FontAsset>(
                    "Assets/Art/Fonts/GoogleSansFlex-UIBold SDF.asset", "fonts/GoogleSansFlex-UIBold SDF");
            return s_uiBoldFont;
        }

        private static T LoadAsset<T>(string editorPath, string address) where T : Object
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(editorPath);
#else
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<T>(address);
            var asset = handle.WaitForCompletion();
#endif
            if (asset == null)
                Debug.LogWarning($"[BoardController] asset '{address}' not found — using default.");
            return asset;
        }

        private static TextMeshPro MakeLabel(Transform parent, string goName, Vector3 localPos, float fontSize, Color color, bool statOutline = false)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var tmp = go.AddComponent<TextMeshPro>();
            // AUTHORED RECT WINS; ONLY THE FONT MAY AUTO-FIT INSIDE IT. Belt to the braces of
            // `m_autoSizeTextContainer: 0` in TMP Settings — kept because the failure it prevents
            // is invisible: `autoSizeTextContainer` makes TMP_Text.Rebuild write
            // `sizeDelta = GetPreferredValues(Infinity, Infinity)` every frame, so the label
            // renders at its natural UNBOUNDED width, wrapping never triggers and auto-sizing
            // never shrinks. Every TextMeshPro in this project is built in code with an authored
            // rect, and none of them wants that.
            //
            // It shipped on for months: it clipped card titles by making ellipsis operate on the
            // rewritten width (Sam's collection-wall audit, 2026-09-01, fixed here for the six
            // labels this method builds), and it was still running the rules band off the sides of
            // the card until the global went off (Sam, 2026-09-04) — that label is built inline
            // below, not through here, so the local fix never reached it.
            //
            // RETIREMENT TRIGGER: turning `m_autoSizeTextContainer` back on in TMP Settings
            // re-arms it for the twelve other runtime label sites that carry no local guard.
            tmp.autoSizeTextContainer = false;
            // Labels draw after the frame sprite EXPLICITLY. Inside the card's SortingGroup
            // everything shares order 0 and the tie-break is hierarchy order, which the Visual
            // reparenting loop reverses — the painted frames are opaque at the bosses/plaque,
            // so a frame drawn after the text erases it (the old placeholders were transparent
            // there, which is why this never showed).
            tmp.GetComponent<Renderer>().sortingOrder = 10;
            var font = CardFont();
            if (font != null) tmp.font = font;
            tmp.fontSize  = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = color;

            if (statOutline)
            {
                if (s_statLabelMaterial == null)
                {
                    s_statLabelMaterial = new Material(tmp.font.material);
                    s_statLabelMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                    s_statLabelMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.25f);
                    s_statLabelMaterial.EnableKeyword(ShaderUtilities.Keyword_Outline);
                }
                tmp.fontSharedMaterial = s_statLabelMaterial;
            }

            // Constrain to the card face: without a sized rect TMP renders at unbounded width,
            // so long names ("Throneless King") bleed across neighboring cards. Auto-size down
            // to fit rather than wrap — card names are single-line by design.
            tmp.rectTransform.sizeDelta = new Vector2(CardW * 0.94f, fontSize * 0.22f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode     = TextOverflowModes.Ellipsis;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax      = fontSize;
            tmp.fontSizeMin      = fontSize * 0.45f;
            return tmp;
        }
    }
}
