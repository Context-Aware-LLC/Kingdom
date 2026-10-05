using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Emberfall.Presentation
{
    // Pointer state machine (EF-048): converts press / drag / release into PlayerIntents for the local
    // human player and routes them to HotSeatInput (ARCHITECTURE §6).
    //
    // ONE MACHINE, BOTH INPUT MODELS. Mouse drag on desktop and touch drag on Android are the
    // same code path, because PointerSampler collapses them into one press/drag/release stream before
    // this class sees them. Hover is therefore ADDITIVE on desktop (HandCardHover) rather than load-
    // bearing, which is the only shape that survives a phone.
    //
    // States:
    //   None                → Pressed             : pointer down on something of ours that can move
    //   Pressed             → Dragging            : pointer travelled past DragThresholdPx
    //                       → None                : released without travel; taps do not arm gameplay
    //   Dragging            → None                : release resolves or cancels
    //                       → AwaitingEffectDrag  : a placed card still owes another target
    //   AwaitingEffectDrag  → AwaitingEffectClick : press a legal target; release selects it
    //                       → Pressed             : re-grab the banked card (legacy drag option)
    //
    // A true button action may still be a tap (untargeted hero power, mulligan, HUD controls).
    // Cards, attacks and targeted powers begin with a drag. Minions, attackers and untargeted
    // cards are CARRIED to where they go; a targeted spell or hero power stays put and is AIMED,
    // with an AimTrail running out toward the pointer (2026-10-04). Once a minion placement is
    // banked, its on-play target can be clicked or keyboard-focused with Tab and confirmed with
    // Enter/Space; re-grab-and-drag remains accepted as a compatible fallback.
    //
    // WHAT IS NOT HERE ANY MORE: the four Clickable* components no longer forward OnMouseDown. They
    // are raycast descriptors now — identity and a collider — and this class does the one raycast per
    // frame itself. Unity's OnMouseDown does fire from touch (simulateMouseWithTouches), so the old
    // click machine was not broken on Android; what it could not express was a gesture with a
    // beginning, a middle and an end, which is what targeting actually is. Nothing in the tree
    // uses the OnMouse* messages any more: the plain buttons and the hover affordances get their
    // press / enter / exit from BoardPointerRelay.
    //
    // End turn : Enter or Space during Main phase, or the on-screen button.
    // Cancel   : Escape clears any active gesture.
    public sealed class InputRouter : MonoBehaviour
    {
        private enum Gesture { None, Pressed, Dragging, AwaitingEffectDrag, AwaitingEffectClick }

        // Travel that separates a tap from a drag. Scaled off screen density because a thumb on a
        // 420 dpi phone wanders several times further than a mouse does on a click, and a fixed
        // pixel count is either a hair-trigger on mobile or unreachable on desktop.
        private static float DragThresholdPx => Screen.dpi > 1f ? Mathf.Max(10f, Screen.dpi * 0.06f) : 10f;

        // Depth a dragged card lifts to, so it draws in front of the whole hand fan and board row
        // (fan staggers to -0.27, HandCardHover's reveal reaches -0.40). Reuses the exact value the
        // deleted AimLineView drew at (AimZ) rather than inventing a new one.
        private const float DragLiftZ = -1.5f;

        private Gesture       _gesture;
        private DragSource    _source;
        private Vector2       _pressPos;
        private CardPresenter _sourcePresenter;
        private Transform     _sourceTransform;
        private Vector3       _sourceRestLocalPos;
        private Quaternion    _sourceRestLocalRot;
        private bool          _sourceIsOwnHero;

        // How far an aimed card rises out of the fan, in the hand's own space: up, and toward the
        // camera far enough to clear its neighbours (the fan staggers to -0.27).
        private static readonly Vector3 AimLift = new Vector3(0f, 0.35f, -0.45f);

        private bool     _aiming;     // this drag aims from rest; see DragTargetingModel.AimsFromRest
        private AimTrail _aimTrail;

        private PointerSampler _pointer;

        private PointerSampler EnsurePointer() => _pointer ??= new PointerSampler();

        private void Awake() => EnsurePointer();

        private void OnEnable() => EnsurePointer().Enable();

        private void OnDisable()
        {
            if (_gesture != Gesture.None) CancelGesture();
            _pointer?.Disable();
        }

        private void OnDestroy()
        {
            _pointer?.Dispose();
            _aimTrail?.Dispose();
        }

        private GameState          _currentState;
        private GameState          _presentedState;
        private HotSeatInput       _p0Input;
        private PlayerId           _p0Id;
        private ICommandValidator  _validator;
        private DragTargetingModel _model;

        private HeroPlateView _p0HeroPlate;      // selection + target cue for our hero
        private HeroPlateView _enemyHeroPlate;   // target cue for the enemy hero

        // Set by the bootstrap to the settings overlay's IsBlockingInput. UI Toolkit does NOT
        // block Unity physics, so while a UI surface owns input we suppress board gestures here —
        // otherwise a press passes through the scrim to the cards behind it.
        public System.Func<bool> UiBlocksInput;
        private bool ChoiceBlocksInput => _currentState?.PendingChoice != null;
        public bool BlocksInput => ChoiceBlocksInput || (UiBlocksInput != null && UiBlocksInput());

        // True from the moment a card is pressed through to release (Pressed, Dragging and both
        // Awaiting* states). CardRevealController reads this to keep the hover/hold inspect panel
        // from popping up mid-drag: the hover timer starts on pointer enter, which happens before
        // the press, so a press that becomes a drag can still cross HoverDelaySeconds while the
        // card is being carried across the board (Sam, 2026-09-05 — the panel was opening under a
        // drag and obscuring the drop).
        public bool GestureInProgress => _gesture != Gesture.None;

        // A stationary press is allowed to mature into CardRevealController's touch-hold inspect.
        // Once travel turns it into a drag (or a placed card is waiting for a target), inspection
        // must stay closed so it cannot cover the drop surface. BlocksInput includes choice/UI modals.
        public bool BlocksCardInspection => BlocksInput || _gesture == Gesture.Dragging
            || _gesture == Gesture.AwaitingEffectDrag || _gesture == Gesture.AwaitingEffectClick;

        // EDITOR/TEST ONLY: injectable probe for headless verification. Overrides raycast behavior.
        public System.Func<Vector2, (PointerSubject subject, Transform hitTransform, CardPresenter presenter)> InjectableProbe;

        // EDITOR/TEST ONLY: the same escape hatch for the DROP POINT, which since #139 decides as
        // much as the probe does. Without it a headless test has no Camera.main and every release
        // would report world x 0, quietly collapsing every placement assertion onto one answer.
        public System.Func<Vector2, DropPoint> InjectableDrop;

        // ── Targeting-state visibility (ui-polish 2026-07-17) ──────────────────────
        // While a targeting gesture is armed the set of legal targets is broadcast so
        // BoardController can glow ONLY those (readiness glow is suppressed for the
        // duration). Null = targeting cleared. The glow IS the affordance — there is no
        // instructional banner (Sam, 2026-08-27); see
        // decisions/emberfall-no-persistent-instructional-banners.
        public event System.Action<HashSet<InstanceId>> TargetingChanged;
        public event System.Action<InstanceId?> TargetFocusChanged;
        private HashSet<InstanceId> _activeTargets;
        private InstanceId? _focusedTarget;

        // ── Mulligan picker (C-G1-2b) ──────────────────────────────────────────────
        // During the Mulligan phase a hand-card press toggles that card into the toss set (a red X
        // marker) instead of starting a gesture; the confirm button then submits MulliganIntent(toss).
        // Own seat only — the AI/remote seat confirms itself. Empty toss set = keep all. Fires
        // MulliganSelectionChanged(count) so the confirm button can label itself "KEEP ALL" vs
        // "MULLIGAN (n)".
        //
        // MULLIGAN IS A TAP SURFACE, NOT A DRAG ONE, and it is NOT turn-gated: the engine's mulligan
        // is simultaneous, so the non-active seat decides at the same time we do. Do not add an
        // ActivePlayer check to this path — it hangs that seat with nothing logged.
        public event System.Action<int> MulliganSelectionChanged;
        private readonly HashSet<InstanceId> _mulliganToss = new HashSet<InstanceId>();
        private readonly Dictionary<InstanceId, CardPresenter> _tossPresenters =
            new Dictionary<InstanceId, CardPresenter>();

        /// <summary>Wired by the bootstraps after the hero plates exist — heroes have no
        /// CardPresenter, so their selection/target highlights route through the plate views.</summary>
        public void SetHeroPlates(HeroPlateView p0Plate, HeroPlateView enemyPlate)
        {
            _p0HeroPlate    = p0Plate;
            _enemyHeroPlate = enemyPlate;
        }

        /// <summary>EDITOR/TEST ONLY: access the pointer sampler for headless frame injection
        /// (EF-048 InputGestureVerify). Allows the test harness to script a press/drag/release
        /// sequence without running the Update loop or touching Input.</summary>
        public PointerSampler Pointer => EnsurePointer();

        public void Init(PlayerId p0Id, HotSeatInput p0Input, ICardDatabase db = null,
                         ICommandValidator validator = null)
        {
            _p0Id      = p0Id;
            _p0Input   = p0Input;
            _validator = validator;
            _model     = new DragTargetingModel(p0Id, db);
        }

        // Subscribed to the P0 session's OnSnapshot — the IMMEDIATE truth cadence, NOT the
        // queue-delayed MatchRunner.OnStateUpdated (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a4-session-seam-design.md §7 / §11 finding 7):
        // turn gating and gesture-clearing must not lag behind the authority while the playback
        // queue drains, or the player is locked out / acts on a stale turn.
        public void OnStateUpdated(GameState state)
        {
            _currentState = state;
            if (_model != null) _model.State = state;

            // Mulligan toss selection lives only while our seat is still deciding; once the phase
            // passes or our confirm lands, clear marks from any presenters that survived the redraw
            // before dropping the bookkeeping. ResetMulliganSelection skips destroyed presenters.
            if (!IsMulliganActive() && _mulliganToss.Count > 0)
                ResetMulliganSelection(clearMarks: true);

            if (ChoiceBlocksInput && _gesture != Gesture.None)
            {
                CancelGesture();
                return;
            }

            if (_gesture != Gesture.None && state != null && state.ActivePlayer != _p0Id)
            {
                CancelGesture();
                return;
            }
            // Refresh the armed-target set on every snapshot — board changes (a bulwark dying
            // to a toll, a minion gaining shroud) can alter what's legal mid-aim.
            RefreshTargeting();
        }

        // Subscribed to MatchRunner.OnStateUpdated — the PLAYBACK cadence, i.e. what is on screen.
        // Truth alone is not enough to act on: the opponent's whole turn resolves in a few frames
        // and then plays back over seconds, and for all of that time truth already says it is our
        // turn. A tap on the hero power then spent it against a board the player had not been
        // shown, with the socket still drawn dark, and the turn opened with the power gone (Sam,
        // 2026-10-04). Space or Enter in the same window ended a turn nobody had seen start.
        // Never fed in the modes that draw truth directly (decision review, the gesture verifier),
        // which leaves this null and the gate open.
        public void OnPresentedStateUpdated(GameState state) => _presentedState = state;

        /// <summary>Truth has moved into a turn the playback queue has not reached yet. Lag INSIDE
        /// a turn does not count: chaining plays ahead of your own animations stays legal.</summary>
        private bool TurnNotYetShown => _presentedState != null && _currentState != null
            && _presentedState.TurnNumber != _currentState.TurnNumber;

        // ── The machine ─────────────────────────────────────────────────────────────

        private void Update()
        {
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true) CancelGesture();

            // Sample unconditionally: the sampler tracks press/release EDGES, so skipping frames
            // makes it miss the transition it exists to report.
            PointerSample p = _pointer.Sample();

            if (BlocksInput)
            {
                if (_gesture != Gesture.None) CancelGesture();
                _pointer.Reset();   // a press that began under the UI must not arm when it lifts
                return;
            }
            if (_currentState == null || _p0Input == null || _model == null) return;

            HandleKeyboard();

            switch (p.Phase)
            {
                case PointerPhase.Pressed:  OnPointerPressed(p.Position);  break;
                case PointerPhase.Held:     OnPointerHeld(p.Position);     break;
                case PointerPhase.Released: OnPointerReleased(p.Position); break;
                case PointerPhase.Canceled: CancelGesture(); _pointer.Reset(); break;
            }
        }

        private void OnPointerPressed(Vector2 screenPos)
        {
            _pressPos = screenPos;
            PointerSubject subject = Probe(screenPos, out Transform hitTransform, out CardPresenter presenter);

            if (IsMulliganActive())
            {
                if (subject.Kind == PointerSubjectKind.Card
                    && subject.Owner == _p0Id && subject.Zone == Zone.Hand)
                    ToggleMulliganCard(subject.Id, presenter);
                return;
            }

            // Nothing arms against a board from an earlier turn; Submit holds the same line for
            // the paths that never press anything.
            if (TurnNotYetShown) return;

            // A placed card can still owe one or more effect targets. Pressing a glowing target
            // starts click confirmation; re-grabbing the source preserves the original drag
            // fallback for touch players who prefer one continuous interaction vocabulary.
            if (_gesture == Gesture.AwaitingEffectDrag)
            {
                // The click half of targeting gets the same fat-finger radius the drop half does.
                PointerSubject aimed = SnapToTarget(screenPos, subject);
                if (IsActiveTarget(aimed))
                {
                    _gesture = Gesture.AwaitingEffectClick;
                    SetTargetFocus(aimed.Id);
                    return;
                }
                if (!IsGestureSource(subject)) return;
                _gesture = Gesture.Pressed;
                return;
            }

            DragSource src = _model.BeginSource(subject);
            if (src.Kind == DragSourceKind.None) return;

            _source          = src;
            _sourcePresenter = presenter;
            _sourceIsOwnHero = subject.Kind == PointerSubjectKind.Hero;
            // THE PLATE MOVES, THE COLLIDER DOES NOT. Probe hands back the hero ANCHOR, which is
            // where ClickableHero's box lives; dragging that would carry the box under the pointer
            // and every release would then raycast our own hero first — an illegal target, so
            // every hero swing would cancel itself. HeroPlateView's own transform is a child of
            // the anchor, so moving it lifts the portrait and leaves the hitbox at home.
            _sourceTransform = _sourceIsOwnHero
                ? (_p0HeroPlate != null ? _p0HeroPlate.transform : null)
                : hitTransform;
            _gesture         = Gesture.Pressed;
            CaptureSourceRestPose(_sourceTransform);
        }

        private void OnPointerHeld(Vector2 screenPos)
        {
            if (_gesture == Gesture.Pressed
                && (screenPos - _pressPos).sqrMagnitude >= DragThresholdPx * DragThresholdPx)
            {
                _gesture = Gesture.Dragging;
                ArmVisuals();
            }

            if (_gesture != Gesture.Dragging) return;
            if (_aiming) Aim(screenPos);
            else         FollowPointer(screenPos);
        }

        /// <summary>
        /// The aimed half of a drag (Sam, 2026-10-04): a targeted spell or hero power stays where
        /// it is and an <see cref="AimTrail"/> runs out from it instead. Whatever a release here
        /// would land on is focused NOW, through the same snap the release uses, so the target
        /// that lights up is the one that gets hit.
        /// </summary>
        private void Aim(Vector2 screenPos)
        {
            PointerSubject raw = Probe(screenPos, out Transform hit, out _, skipGestureSource: true);
            PointerSubject subject = SnapToTarget(screenPos, raw);
            bool legal = IsActiveTarget(subject);
            SetTargetFocus(legal ? subject.Id : (InstanceId?)null);

            Camera cam = Camera.main;
            if (cam == null || _sourceTransform == null) return;
            if (!BoardPlaneProjection.TryScreenToPlane(cam, screenPos, planeZ: 0f, out Vector3 pointer))
                return;

            // A snapped target was never under the ray, so there is no transform to read its
            // centre from; the pointer is within a thumb's width of it by construction.
            Vector3? lockedOn = null;
            if (legal)
                lockedOn = hit != null && raw.Kind == subject.Kind && raw.Id.Equals(subject.Id)
                    ? hit.position
                    : pointer;

            _aimTrail ??= new AimTrail();
            _aimTrail.Track(cam, _sourceTransform.position, pointer, lockedOn);
        }

        /// <summary>Remembers where a source card rests so a cancelled drag can put it back
        /// exactly, rather than merely zeroing it (see <see cref="RestoreSourceVisual"/>).</summary>
        private void CaptureSourceRestPose(Transform t)
        {
            if (t == null) return;
            _sourceRestLocalPos = t.localPosition;
            _sourceRestLocalRot = t.localRotation;
        }

        /// <summary>
        /// The source card lifts to <see cref="DragLiftZ"/> and tracks the pointer for the rest of
        /// the gesture (Sam, 2026-08-16). #173/#174 removed every other drag-in-progress cue — the
        /// aim line and the picked-up card's own selection glow — leaving nothing on screen that
        /// distinguished an active drag from a completed tap, on a card with no legal-target set to
        /// glow cyan (a plain vanilla minion, say).
        ///
        /// <para>OUR HERO MOVES TOO, since 2026-09-04 — "you could pick up your hero and drag it
        /// to a target the same way you do with a minion" (Sam). It was excluded until then, and
        /// the gold selection tint that stood in for the lift was not enough to tell an armed
        /// swing from a press that did nothing. What moves is the PORTRAIT, not the anchor: see
        /// OnPointerPressed. The two sockets stay docked where they are — they are on their own
        /// anchors, and they are not what you are swinging.</para>
        ///
        /// <para>The hero power is still excluded. It is a socket, not a character, and an
        /// untargeted power is a tap.</para>
        ///
        /// <para>NOT EVERYTHING IS CARRIED ANY MORE. A targeted spell and a targeted hero power
        /// are aimed from where they sit — see <see cref="Aim"/> — so this is now the path for
        /// minions, attackers and untargeted cards only.</para>
        /// </summary>
        private void FollowPointer(Vector2 screenPos)
        {
            if (_sourceTransform == null) return;
            if (_source.Kind == DragSourceKind.HeroPower) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            if (!BoardPlaneProjection.TryScreenToPlane(
                    cam, screenPos, DragLiftZ, out Vector3 world))
                return;

            _sourceTransform.position = world;
            _sourceTransform.rotation = Quaternion.identity;
        }

        private void OnPointerReleased(Vector2 screenPos)
        {
            if (IsMulliganActive()) return;   // the toggle already happened on press
            if (_gesture == Gesture.None) return;

            // The trail belongs to the drag, not to whatever the release turns it into: a spell
            // that still owes a second target goes on to collect it by tap.
            if (_aiming && _gesture == Gesture.Dragging)
            {
                _aimTrail?.Hide();
                SetTargetFocus(null);
            }

            PointerSubject subject =
                SnapToTarget(screenPos, Probe(screenPos, out _, out _, skipGestureSource: true));

            if (_gesture == Gesture.AwaitingEffectClick)
            {
                if (IsActiveTarget(subject))
                    TryResolve(subject, DropAt(screenPos));
                else
                {
                    _gesture = Gesture.AwaitingEffectDrag;
                    SetTargetFocus(null);
                    RefreshTargeting();
                }
                return;
            }

            if (_gesture == Gesture.Pressed)
            {
                // Never travelled: gameplay taps are reads/no-ops. The only source whose entire
                // action is genuinely button-like is an untargeted hero power.
                if (_model.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget)
                {
                    _gesture = Gesture.AwaitingEffectDrag;
                    RefreshTargeting();
                    return;
                }
                if (_source.Kind == DragSourceKind.HeroPower && !_source.NeedsTarget)
                {
                    CancelGesture();
                    Submit(new HeroPowerIntent(_p0Id, System.Array.Empty<TargetRef>()));
                    return;
                }
                CancelGesture();
                return;
            }

            // AwaitingEffectDrag without a matching source or legal-target press is an inert tap.
            // Leave the banked placement intact so click, keyboard, or re-grab can finish it.
            if (_gesture == Gesture.AwaitingEffectDrag) return;

            TryResolve(subject, DropAt(screenPos));
        }

        /// <summary>Submits if the drop produced an intent. Clears the gesture either way unless
        /// we entered AwaitingEffectTarget (multi-step herald), in which case the source stays
        /// available for click, keyboard selection, or another drag. A drop
        /// that produced nothing restores the source's visual position — one that succeeded leaves
        /// it at the drop point, because the coming state sync is about to relocate it for real, and
        /// snapping it back first would just be a flicker on the way there.</summary>
        private bool TryResolve(PointerSubject released, DropPoint drop)
        {
            // Once placement is banked, putting the source back in the hand is still the universal
            // touch cancel. DragTargetingModel otherwise ignores the drop point during target steps.
            if (_model.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget
                && drop.OverOwnHand)
            {
                CancelGesture();
                return true;
            }

            PlayerIntent intent = _model.Resolve(_source, released, drop);

            // The current drop banked a placement or satisfied only part of a multi-target plan.
            if (_model.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget)
            {
                _gesture = Gesture.AwaitingEffectDrag;
                RefreshTargeting();
                return true;   // Consumed the release; no intent submitted yet.
            }

            if (intent == null)
            {
                CancelGesture();
                return false;
            }

            // Validate before deciding whether the dragged card should stay at the release point.
            // A rejected intent emits no snapshot, so treating it as a successful drop strands the
            // visual until an unrelated accepted action happens to refresh the hand.
            bool submitted = Submit(intent);
            CancelGesture(restoreVisual: !submitted);
            return submitted;
        }

        private bool IsGestureSource(PointerSubject s)
        {
            switch (_source.Kind)
            {
                case DragSourceKind.HandCard:
                case DragSourceKind.Attacker:
                    if (s.Kind == PointerSubjectKind.Card) return s.Id.Equals(_source.Id);
                    if (s.Kind == PointerSubjectKind.Hero) return s.Owner == _p0Id && s.Id.Equals(_source.Id);
                    return false;
                case DragSourceKind.HeroPower:
                    return s.Kind == PointerSubjectKind.HeroPower && s.Owner == _p0Id;
                default:
                    return false;
            }
        }

        private void ArmVisuals()
        {
            _sourcePresenter?.SetSelectionHighlight(true);
            if (_sourceIsOwnHero) _p0HeroPlate?.SetSelected(true);

            // An aimed card rises out of the fan and stays there: still readable, and an obvious
            // place for the trail to start. The hero power socket does not move at all.
            _aiming = _model.AimsFromRest(_source);
            if (_aiming && _source.Kind == DragSourceKind.HandCard && _sourceTransform != null)
                _sourceTransform.localPosition = _sourceRestLocalPos + AimLift;

            RefreshTargeting();
        }

        /// <param name="restoreVisual">Put the source card back at its rest pose. False only from
        /// <see cref="TryResolve"/>'s success path — see its comment.</param>
        private void CancelGesture(bool restoreVisual = true)
        {
            _sourcePresenter?.SetSelectionHighlight(false);
            _p0HeroPlate?.SetSelected(false);
            // A SUCCESSFUL HERO SWING STILL PUTS THE PORTRAIT BACK, unlike a card's. The card is
            // left at the drop point because the snapshot that is already on its way relocates it
            // for real; nothing ever relocates a hero, so leaving the plate at the release point
            // strands the portrait in the middle of the board until the scene is rebuilt.
            if (restoreVisual || _sourceIsOwnHero) RestoreSourceVisual();
            _aimTrail?.Hide();
            _aiming          = false;
            _sourcePresenter = null;
            _sourceTransform = null;
            _sourceIsOwnHero = false;
            _source          = DragSource.None;
            _gesture         = Gesture.None;
            ClearTargeting();

            // Clear any banked placement / awaiting-effect-target state.
            if (_model != null)
                _model.Phase = DragTargetingModel.TargetingPhase.Direct;
        }

        /// <summary>Undoes <see cref="FollowPointer"/> — puts the source card, or our own hero's
        /// portrait, back exactly where it was picked up from. A no-op for a hero-power source,
        /// which FollowPointer never moves.</summary>
        private void RestoreSourceVisual()
        {
            if (_sourceTransform == null) return;
            _sourceTransform.localPosition = _sourceRestLocalPos;
            _sourceTransform.localRotation = _sourceRestLocalRot;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                CancelGesture();
                _pointer.Reset();
            }
        }

        private void OnApplicationPause(bool isPaused)
        {
            if (isPaused)
            {
                CancelGesture();
                _pointer.Reset();
            }
        }

        // ── Raycast: screen point → what the pointer is over ────────────────────────

        // ONE raycast per pointer event, resolved against the four Clickable* descriptors. Anything
        // else the ray hits (the end-turn button, the mulligan confirm, the toast) reads as None here
        // and cancels an in-flight gesture, while BoardPointerRelay still presses that object — which is
        // the correct reading of "the player pressed something that is not the board".
        /// <summary>
        /// WHERE the release happened, in board coordinates — the half of a drop that no collider
        /// reports (Sam, 2026-08-12, #139). Placement is proximity on x, so this is what decides
        /// where a minion lands; <see cref="Probe"/> only still matters for cards and heroes.
        ///
        /// <para>The hand test is a LINE, <see cref="HandFanLayout.TopEdgeY"/>, not a hit on a hand
        /// card. Releasing into a gap between two fanned cards, or below the fan entirely, is still
        /// putting the card back — and a rule made of colliders would have said "play it".</para>
        ///
        /// <para>The screen point is resolved by ray-plane intersection rather than a guessed
        /// camera depth. That distinction is invisible under an unrotated orthographic camera,
        /// but load-bearing once the board camera is angled in perspective.</para>
        /// </summary>
        private DropPoint DropAt(Vector2 screenPos)
        {
            if (InjectableDrop != null) return InjectableDrop(screenPos);

            Camera cam = Camera.main;
            if (cam == null) return DropPoint.Board(0f);

            if (!BoardPlaneProjection.TryScreenToPlane(
                    cam, screenPos, planeZ: 0f, out Vector3 world))
                return DropPoint.Board(0f);

            return world.y <= HandFanLayout.TopEdgeY
                ? DropPoint.OwnHand
                : DropPoint.Board(world.x);
        }

        /// <summary>
        /// How far a release may miss a legal target and still land on it. The opponent's half of
        /// the board is drawn in PERSPECTIVE and is therefore physically smaller on screen than
        /// ours, so the enemy hero — the thing most often aimed at — is the hardest target to hit
        /// exactly (Sam, 2026-09-04). Screen-height relative, so it is the same fraction of a
        /// thumb's travel on every device.
        /// </summary>
        private static float TargetSnapPx => Mathf.Max(40f, Screen.height * 0.075f);

        /// <summary>
        /// Widens a legal target from its collider to its collider plus <see cref="TargetSnapPx"/>,
        /// by re-probing a ring around a release that hit nothing.
        ///
        /// <para>It only fires when the raw probe found NO character. Releasing on a real character
        /// that merely is not legal — a BULWARK-blocked minion, a SHROUD, our own board — means
        /// that character, and quietly redirecting the swing to its neighbour would be worse than
        /// refusing it. So this rescues fat-finger misses and dead space, never a deliberate aim.
        /// </para>
        /// </summary>
        private PointerSubject SnapToTarget(Vector2 screenPos, PointerSubject raw)
        {
            // The headless verifier's probe ignores the screen point, so a ring around it would
            // return the same subject sixteen times and prove nothing.
            if (InjectableProbe != null) return raw;
            if (_activeTargets == null || _activeTargets.Count == 0) return raw;
            if (raw.Kind == PointerSubjectKind.Card || raw.Kind == PointerSubjectKind.Hero) return raw;

            const int Spokes = 8;
            for (int ring = 1; ring <= 2; ring++)
            {
                float radius = TargetSnapPx * ring / 2f;
                for (int i = 0; i < Spokes; i++)
                {
                    float a = 2f * Mathf.PI * i / Spokes;
                    Vector2 probeAt = screenPos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
                    PointerSubject s = Probe(probeAt, out _, out _, skipGestureSource: true);
                    if (IsActiveTarget(s)) return s;
                }
            }
            return raw;
        }

        private PointerSubject Probe(Vector2 screenPos, out Transform hitTransform,
                                     out CardPresenter presenter, bool skipGestureSource = false)
        {
            // EDITOR/TEST: use injectable probe if provided (headless verification).
            if (InjectableProbe != null)
            {
                var (subject, transform, pres) = InjectableProbe(screenPos);
                hitTransform = transform;
                presenter = pres;
                return subject;
            }

            hitTransform = null;
            presenter    = null;

            Camera cam = Camera.main;
            if (cam == null) return PointerSubject.None;
            RaycastHit[] hits = Physics.RaycastAll(cam.ScreenPointToRay(screenPos), 100f);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                var card = hit.collider.GetComponentInParent<ClickableCard>();
                if (card != null)
                {
                    PointerSubject cardSubject =
                        PointerSubject.OfCard(card.CardId, card.OwnerId, card.Zone);
                    // FollowPointer lifts the dragged card in front of the board. Look through its
                    // own collider on release so the character beneath it receives the drop.
                    if (skipGestureSource && IsGestureSource(cardSubject)) continue;

                    hitTransform = card.transform;
                    presenter    = card.Presenter;
                    return cardSubject;
                }

                // NO BOARD-SLOT BRANCH (Sam, 2026-08-12, #139). Empty board space is not raycast at
                // all any more — the 14 invisible slot boxes and their ClickableBoardSlot descriptor
                // are gone, and where a minion lands is solved from the drop's x in DropAt below.
                var hero = hit.collider.GetComponentInParent<ClickableHero>();
                if (hero != null)
                {
                    hitTransform = hero.transform;
                    return PointerSubject.OfHero(_model.HeroIdOf(hero.OwnerId), hero.OwnerId);
                }

                var power = hit.collider.GetComponentInParent<ClickableHeroPower>();
                if (power != null)
                {
                    // THE OPPONENT'S SOCKET IS NOT A SUBJECT, IT IS AN OBSTRUCTION. A hero power is
                    // never a drag target (IsActiveTarget accepts Card and Hero only) and never a
                    // source but ours, so returning the enemy's here could only ever cancel a
                    // gesture. Its collider is HeroSocketsView.HitDiameter square, sits at z -0.1 in
                    // FRONT of the hero plate and rides the portrait's lower third — so a card or an attack
                    // released on the fattest part of the enemy hero resolved as "their hero power"
                    // and died there (Sam, 2026-09-04). Both seats keep the descriptor: ownership is
                    // read off it, not implied by which ones exist.
                    if (power.OwnerId != _p0Id) continue;
                    hitTransform = power.transform;
                    return PointerSubject.OfHeroPower(power.OwnerId);
                }
            }

            return PointerSubject.None;
        }

        // ── Targeting-state visibility ─────────────────────────────────────────────

        private void RefreshTargeting()
        {
            if (_model == null) { ClearTargeting(); return; }
            if (_gesture != Gesture.Dragging && _gesture != Gesture.AwaitingEffectDrag)
            {
                ClearTargeting();
                return;
            }

            // If awaiting another effect-target drag, show the remaining effect targets instead.
            if (_model.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget)
            {
                HashSet<InstanceId> effectTargets = _model.AwaitingTargets();
                if (effectTargets == null || effectTargets.Count == 0) ClearTargeting();
                else                                                    SetTargeting(effectTargets);
                return;
            }

            HashSet<InstanceId> targets = _model.TargetsFor(_source);
            if (targets == null) ClearTargeting();
            else                 SetTargeting(targets);
        }

        private void SetTargeting(HashSet<InstanceId> targets)
        {
            _activeTargets = targets;
            if (_focusedTarget.HasValue && !targets.Contains(_focusedTarget.Value))
                SetTargetFocus(null);
            TargetingChanged?.Invoke(targets);
            ApplyHeroTargetCues();
        }

        private void ClearTargeting()
        {
            if (_activeTargets != null)
            {
                _activeTargets = null;
                TargetingChanged?.Invoke(null);
            }
            SetTargetFocus(null);
            _p0HeroPlate?.SetTargetable(false);
            _enemyHeroPlate?.SetTargetable(false);
        }

        private bool IsActiveTarget(PointerSubject subject)
            => (subject.Kind == PointerSubjectKind.Card || subject.Kind == PointerSubjectKind.Hero)
               && _activeTargets != null && _activeTargets.Contains(subject.Id);

        private void SetTargetFocus(InstanceId? target)
        {
            if (_focusedTarget.Equals(target)) return;
            _focusedTarget = target;
            TargetFocusChanged?.Invoke(target);
            _p0HeroPlate?.SetTargetFocused(
                target.HasValue && _model != null && target.Value.Equals(_model.HeroIdOf(_p0Id)));
            _enemyHeroPlate?.SetTargetFocused(
                target.HasValue && _model != null && target.Value.Equals(_model.HeroIdOf(_model.EnemyId())));
        }

        private void ApplyHeroTargetCues()
        {
            if (_currentState == null || _model == null) return;
            _p0HeroPlate?.SetTargetable(
                _activeTargets != null && _activeTargets.Contains(_model.HeroIdOf(_p0Id)));
            _enemyHeroPlate?.SetTargetable(
                _activeTargets != null && _activeTargets.Contains(_model.HeroIdOf(_model.EnemyId())));
        }

        // ── Keyboard shortcuts ─────────────────────────────────────────────────────

        private void HandleKeyboard()
        {
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_currentState.ActivePlayer != _p0Id) return;

            if (_model.Phase == DragTargetingModel.TargetingPhase.AwaitingEffectTarget)
            {
                if (Keyboard.current?.tabKey.wasPressedThisFrame == true)
                    CycleAwaitingTarget(
                        Keyboard.current.leftShiftKey.isPressed
                        || Keyboard.current.rightShiftKey.isPressed);
                else if (Keyboard.current?.enterKey.wasPressedThisFrame == true
                         || Keyboard.current?.spaceKey.wasPressedThisFrame == true)
                    ConfirmAwaitingTarget();
                return;
            }

            if (Keyboard.current?.enterKey.wasPressedThisFrame == true
                || Keyboard.current?.spaceKey.wasPressedThisFrame == true)
            {
                CancelGesture();
                Submit(new EndTurnIntent(_p0Id));
            }
        }

        /// <summary>Moves keyboard focus through the currently legal follow-up targets. Internal
        /// so the headless gesture verifier can exercise the same path without keyboard hardware.</summary>
        internal bool CycleAwaitingTarget(bool reverse = false)
        {
            List<PointerSubject> targets = KeyboardTargets();
            if (targets.Count == 0) return false;

            int index = -1;
            if (_focusedTarget.HasValue)
                index = targets.FindIndex(t => t.Id.Equals(_focusedTarget.Value));
            index = reverse
                ? (index <= 0 ? targets.Count - 1 : index - 1)
                : (index + 1) % targets.Count;
            SetTargetFocus(targets[index].Id);
            return true;
        }

        /// <summary>Confirms the target most recently focused with Tab.</summary>
        internal bool ConfirmAwaitingTarget()
        {
            if (!_focusedTarget.HasValue) return false;
            foreach (PointerSubject subject in KeyboardTargets())
                if (subject.Id.Equals(_focusedTarget.Value))
                    return TryResolve(subject, DropPoint.Board(0f));
            SetTargetFocus(null);
            return false;
        }

        private List<PointerSubject> KeyboardTargets()
        {
            var result = new List<PointerSubject>();
            if (_currentState == null || _activeTargets == null) return result;

            foreach (PlayerState player in _currentState.Players)
            {
                foreach (CardInstance card in player.Board)
                    if (_activeTargets.Contains(card.Id))
                        result.Add(PointerSubject.OfCard(card.Id, player.Id, Zone.Board));
                if (_activeTargets.Contains(player.Hero.Id))
                    result.Add(PointerSubject.OfHero(player.Hero.Id, player.Id));
            }
            return result;
        }

        // On-screen end-turn button (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §6 item 1) — same guards as the
        // keyboard path; keyboard stays.
        public void OnEndTurnClicked()
        {
            if (_currentState == null || _p0Input == null) return;
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_currentState.ActivePlayer != _p0Id) return;

            CancelGesture();
            Submit(new EndTurnIntent(_p0Id));
        }

        // ── Mulligan picker ────────────────────────────────────────────────────────

        private bool IsMulliganActive() => _model != null && _model.IsMulliganActive();

        private void ToggleMulliganCard(InstanceId cardId, CardPresenter presenter)
        {
            if (_mulliganToss.Contains(cardId))
            {
                _mulliganToss.Remove(cardId);
                _tossPresenters.Remove(cardId);
                presenter?.SetMulliganMark(false);
            }
            else
            {
                _mulliganToss.Add(cardId);
                if (presenter != null) _tossPresenters[cardId] = presenter;
                presenter?.SetMulliganMark(true);
            }
            MulliganSelectionChanged?.Invoke(_mulliganToss.Count);
        }

        // Called by MulliganConfirmButtonView. Empty toss set = keep all. Routed through the same
        // Submit pipe as every other intent; the authority is the sole mulligan authority (the
        // local validator passes it, so it reaches the server in networked mode).
        public void OnMulliganConfirmClicked()
        {
            if (!IsMulliganActive()) return;
            var toss = new List<InstanceId>(_mulliganToss);
            ResetMulliganSelection(clearMarks: true);
            Submit(new MulliganIntent(_p0Id, toss));
        }

        /// <summary>
        /// Called only by <see cref="UnearthChoiceView"/>. Rechecks the current snapshot so a
        /// delayed click cannot answer a replacement offer, then echoes the authoritative sequence
        /// unchanged through the ordinary submit pipe.
        /// </summary>
        public void OnUnearthChoiceClicked(int chosenCardId, ulong choiceSequence)
        {
            UnearthChoiceIntent intent = UnearthChoiceModel.IntentForSelection(
                _currentState, _p0Id, chosenCardId, choiceSequence);
            if (intent != null) Submit(intent);
        }

        private void ResetMulliganSelection(bool clearMarks)
        {
            if (clearMarks)
                foreach (var kv in _tossPresenters)
                    // Unity's != null skips destroyed presenters; ?. would still call through them.
                    if (kv.Value != null)
                        kv.Value.SetMulliganMark(false);
            _tossPresenters.Clear();
            _mulliganToss.Clear();
            MulliganSelectionChanged?.Invoke(0);
        }

        /// <summary>
        /// EF-049. "That press turned out to be a read, not a select." Ordinary gameplay taps are
        /// now inert, but mulligan still toggles on press. A long-press card reveal must undo that
        /// toggle so reading a card cannot silently change the toss set.
        ///
        /// <para><b>The mulligan toggle is not an afterthought.</b> Mulligan is the moment a player
        /// most needs to read a card, and there a press toggles the TOSS MARK rather than
        /// selecting. Toggling once more restores the pre-press state exactly, whichever way it
        /// went — which is the only reason this takes the card and not just its id.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER.</b> The ordinary gameplay half was discharged when EF-048
        /// moved cards to drag-and-drop. <b>The MULLIGAN toggle still fires on press</b>
        /// (<c>OnPointerPressed</c>), and that is
        /// the only reason this method survives. When mulligan toggling also moves to release, the
        /// long press should suppress the dispatch instead of undoing it — delete this method and
        /// <c>CardRevealController.CancelPressSelection</c> in that same change.</para>
        /// </summary>
        public void CancelSelection(ClickableCard card)
        {
            if (card == null || _currentState == null) return;

            if (IsMulliganActive())
            {
                // Mirrors OnPointerPressed's mulligan arm exactly; anything it ignored, this must
                // too. That arm still fires on PRESS, which is the only reason this method survives.
                if (card.OwnerId == _p0Id && card.Zone == Zone.Hand)
                    ToggleMulliganCard(card.CardId, card.Presenter);
                return;
            }

            // Outside mulligan, consume a stationary source press immediately. Waiting until
            // release leaves a window where thumb drift can promote the same contact to Dragging,
            // making a read turn into an accidental play when the finger lifts.
            if (_gesture == Gesture.Pressed && _source.Id.Equals(card.CardId))
                CancelGesture();
        }

        // In-match concede (C-G1-3), invoked by the settings overlay's confirm. Deliberately
        // UNGUARDED by turn/phase: concede is legal at any time (CommandProcessor: KnownPlayer +
        // not GameOver), unlike end-turn — so a player can forfeit on the opponent's turn. Routed
        // through the same Submit pipe; the local validator passes it, so it reaches the authority,
        // which is the sole forfeit authority (decision 23). Not UiBlocked-gated — the UI is the caller.
        public void OnConcedeClicked()
        {
            if (_p0Input == null) return;
            CancelGesture();
            Submit(new ConcedeIntent(_p0Id));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────

        // Courtesy pre-validation over the PROJECTED truth state (a4 design §7; ADR-0004 — an
        // affordance, not prediction). The viewer's own hand/mana/board are un-redacted in their
        // own projection, so verdicts match the authority for everything except hidden-info
        // rejects, which the authority still catches. A local reject routes to the same
        // OnRejected path a server reject would take.
        private bool Submit(PlayerIntent intent)
        {
            // A pending interactive choice is the sole legal gameplay continuation. Keep all
            // board/button/keyboard paths quiet rather than manufacturing predictable rejections.
            if (ChoiceBlocksInput && intent is not UnearthChoiceIntent) return false;

            // Quiet for the same reason: the player is early, not wrong. Concede is legal whatever
            // is on screen.
            if (TurnNotYetShown && intent is not ConcedeIntent) return false;

            if (_validator != null && _currentState != null
                && !_validator.CanExecute(_currentState, intent, out RejectReason? reason))
            {
                // §14 step 4 diagnostic: a local reject names the mirror state it judged
                // against — stale-version rejects are invisible without this.
                Debug.LogWarning($"[InputRouter] local reject {reason} judged on mirror " +
                                 $"v{_currentState.StateVersion} phase={_currentState.Phase} " +
                                 $"active={_currentState.ActivePlayer} intent={intent.GetType().Name}");
                _p0Input.OnRejected(reason ?? RejectReason.NotImplemented);
                return false;
            }
            _p0Input.Enqueue(intent);
            return true;
        }
    }
}
