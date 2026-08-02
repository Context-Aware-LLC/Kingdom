using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Pointer state machine (EF-048): converts press / drag / release into PlayerIntents for the local
    // human player and routes them to HotSeatInput (ARCHITECTURE §6).
    //
    // ONE MACHINE, BOTH INPUT MODELS. Mouse drag on desktop and tap-and-hold-drag on Android are the
    // same code path, because PointerSampler collapses them into one press/drag/release stream before
    // this class sees them. Hover is therefore ADDITIVE on desktop (HandCardHover) rather than load-
    // bearing, which is the only shape that survives a phone.
    //
    // States:
    //   None       → Pressed     : pointer down on something of ours that can move
    //   Pressed    → Dragging    : the pointer travelled past DragThresholdPx
    //              → ArmedByTap  : released without travelling (a tap)
    //   Dragging   → None        : released — resolves to an intent, or cancels if the drop meant nothing
    //   ArmedByTap → None        : released on a target (submit), on the source again (cancel),
    //                              on another of ours (reselect), or on nothing (cancel)
    //
    // ArmedByTap is not a legacy remnant — it is the accessibility path. A drag is a two-handed,
    // fine-motor gesture; tap-then-tap is the same machine reached by two short presses, and it is
    // what a mouse user's muscle memory produces. Both funnel through DragTargetingModel.Resolve, so
    // there is exactly one place where a gesture becomes an intent.
    //
    // WHAT IS NOT HERE ANY MORE: the four Clickable* components no longer forward OnMouseDown. They
    // are raycast descriptors now — identity and a collider — and this class does the one raycast per
    // frame itself. Unity's OnMouseDown does fire from touch (simulateMouseWithTouches), so the old
    // click machine was not broken on Android; what it could not express was a gesture with a
    // beginning, a middle and an end, which is what targeting actually is.
    //
    // End turn : Enter or Space during Main phase, or the on-screen button.
    // Cancel   : Escape clears any armed gesture.
    public sealed class InputRouter : MonoBehaviour
    {
        private enum Gesture { None, Pressed, Dragging, ArmedByTap }

        // Travel that separates a tap from a drag. Scaled off screen density because a thumb on a
        // 420 dpi phone wanders several times further than a mouse does on a click, and a fixed
        // pixel count is either a hair-trigger on mobile or unreachable on desktop.
        private static float DragThresholdPx => Screen.dpi > 1f ? Mathf.Max(10f, Screen.dpi * 0.06f) : 10f;

        private Gesture       _gesture;
        private DragSource    _source;
        private Vector2       _pressPos;
        private CardPresenter _sourcePresenter;
        private Transform     _sourceTransform;
        private bool          _sourceIsOwnHero;

        private readonly PointerSampler _pointer = new PointerSampler();
        private readonly AimLineView    _aim     = new AimLineView();

        private GameState          _currentState;
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
        private bool UiBlocked => UiBlocksInput != null && UiBlocksInput();

        // EDITOR/TEST ONLY: injectable probe for headless verification. Overrides raycast behavior.
        public System.Func<Vector2, (PointerSubject subject, Transform hitTransform, CardPresenter presenter)> InjectableProbe;

        // ── Targeting-state visibility (ui-polish 2026-07-17) ──────────────────────
        // While a targeting gesture is armed: a persistent banner says so, and the set of
        // legal targets is broadcast so BoardController can glow ONLY those (readiness
        // glow is suppressed for the duration). Null = targeting cleared.
        public event System.Action<HashSet<InstanceId>> TargetingChanged;
        private HashSet<InstanceId> _activeTargets;
        private GameObject  _bannerGo;
        private TextMeshPro _bannerLabel;

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
        public PointerSampler Pointer => _pointer;

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
            // passes or our confirm lands, drop the bookkeeping (the cards are gone/replaced, so
            // don't touch their now-dead presenters — clearMarks: false).
            if (!IsMulliganActive() && _mulliganToss.Count > 0)
                ResetMulliganSelection(clearMarks: false);

            if (_gesture != Gesture.None && state != null && state.ActivePlayer != _p0Id)
            {
                CancelGesture();
                return;
            }
            // Refresh the armed-target set on every snapshot — board changes (a taunt dying
            // to a deathrattle, a minion gaining stealth) can alter what's legal mid-aim.
            RefreshTargeting();
        }

        // ── The machine ─────────────────────────────────────────────────────────────

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) CancelGesture();

            // Sample unconditionally: the sampler tracks press/release EDGES, so skipping frames
            // makes it miss the transition it exists to report.
            PointerSample p = _pointer.Sample();

            if (UiBlocked)
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

            // Already armed by a tap: the press changes nothing, the RELEASE decides. This is what
            // lets a tap-armed gesture be finished by dragging as well as by a second tap.
            if (_gesture == Gesture.ArmedByTap) return;

            DragSource src = _model.BeginSource(subject);
            if (src.Kind == DragSourceKind.None) return;

            _source          = src;
            _sourcePresenter = presenter;
            _sourceTransform = hitTransform;
            _sourceIsOwnHero = subject.Kind == PointerSubjectKind.Hero;
            _gesture         = Gesture.Pressed;
        }

        private void OnPointerHeld(Vector2 screenPos)
        {
            if (_gesture == Gesture.Pressed
                && (screenPos - _pressPos).sqrMagnitude >= DragThresholdPx * DragThresholdPx)
            {
                _gesture = Gesture.Dragging;
                ArmVisuals();
            }

            if (_gesture == Gesture.Dragging || _gesture == Gesture.ArmedByTap)
                UpdateAim(screenPos);
        }

        private void OnPointerReleased(Vector2 screenPos)
        {
            if (IsMulliganActive()) return;   // the toggle already happened on press
            if (_gesture == Gesture.None) return;

            PointerSubject subject = Probe(screenPos, out Transform hitTransform, out CardPresenter presenter);

            if (_gesture == Gesture.Pressed)
            {
                // Never travelled: this was a tap on the source, not a drag.
                ArmByTap();
                return;
            }

            if (_gesture == Gesture.ArmedByTap && IsArmedSource(subject))
            {
                CancelGesture();   // second tap on what you armed puts it down
                return;
            }

            bool wasArmedByTap = _gesture == Gesture.ArmedByTap;
            if (TryResolve(subject)) return;

            // A drag that ended on nothing is simply abandoned. A tap-armed gesture gets one more
            // reading first: tapping another of our own cards switches the selection rather than
            // being a dead tap, exactly as the click machine did.
            if (!wasArmedByTap) return;

            DragSource next = _model.BeginSource(subject);
            if (next.Kind == DragSourceKind.None) return;   // TryResolve already cancelled

            _source          = next;
            _sourcePresenter = presenter;
            _sourceTransform = hitTransform;
            _sourceIsOwnHero = subject.Kind == PointerSubjectKind.Hero;
            _gesture         = Gesture.Pressed;
            ArmByTap();
        }

        /// <summary>Submits if the drop produced an intent. Clears the gesture either way.</summary>
        private bool TryResolve(PointerSubject released)
        {
            PlayerIntent intent = _model.Resolve(_source, released);
            CancelGesture();
            if (intent == null) return false;
            Submit(intent);
            return true;
        }

        private void ArmByTap()
        {
            // An untargeted hero power has nothing to aim at: a tap IS the whole gesture, so fire it
            // rather than arming a state no subsequent tap could ever resolve.
            if (_source.Kind == DragSourceKind.HeroPower && !_source.NeedsTarget)
            {
                CancelGesture();
                Submit(new HeroPowerIntent(_p0Id, System.Array.Empty<TargetRef>()));
                return;
            }
            _gesture = Gesture.ArmedByTap;
            ArmVisuals();
        }

        private bool IsArmedSource(PointerSubject s)
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
            RefreshTargeting();
        }

        private void CancelGesture()
        {
            _sourcePresenter?.SetSelectionHighlight(false);
            _p0HeroPlate?.SetSelected(false);
            _sourcePresenter = null;
            _sourceTransform = null;
            _sourceIsOwnHero = false;
            _source          = DragSource.None;
            _gesture         = Gesture.None;
            _aim.Hide();
            ClearTargeting();
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

        private void OnDestroy() => _aim.Dispose();

        // ── Raycast: screen point → what the pointer is over ────────────────────────

        // ONE raycast per pointer event, resolved against the four Clickable* descriptors. Anything
        // else the ray hits (the end-turn button, the mulligan confirm, the toast) reads as None here
        // and cancels an in-flight gesture, while that object's own OnMouseDown still fires — which is
        // the correct reading of "the player pressed something that is not the board".
        private PointerSubject Probe(Vector2 screenPos, out Transform hitTransform, out CardPresenter presenter)
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
            if (!Physics.Raycast(cam.ScreenPointToRay(screenPos), out RaycastHit hit, 100f))
                return PointerSubject.None;

            var card = hit.collider.GetComponentInParent<ClickableCard>();
            if (card != null)
            {
                hitTransform = card.transform;
                presenter    = card.Presenter;
                return PointerSubject.OfCard(card.CardId, card.OwnerId, card.Zone);
            }

            var slot = hit.collider.GetComponentInParent<ClickableBoardSlot>();
            if (slot != null)
            {
                hitTransform = slot.transform;
                return PointerSubject.OfBoardSlot(slot.OwnerId, slot.SlotIndex);
            }

            var hero = hit.collider.GetComponentInParent<ClickableHero>();
            if (hero != null)
            {
                hitTransform = hero.transform;
                return PointerSubject.OfHero(_model.HeroIdOf(hero.OwnerId), hero.OwnerId);
            }

            var power = hit.collider.GetComponentInParent<ClickableHeroPower>();
            if (power != null)
            {
                hitTransform = power.transform;
                return PointerSubject.OfHeroPower(power.OwnerId);
            }

            return PointerSubject.None;
        }

        private void UpdateAim(Vector2 screenPos)
        {
            if (_sourceTransform == null) { _aim.Hide(); return; }
            Camera cam = Camera.main;
            if (cam == null) { _aim.Hide(); return; }

            float depth = Mathf.Abs(AimLineView.AimZ - cam.transform.position.z);
            Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, depth));
            _aim.Show(_sourceTransform.position, world);
        }

        // ── Targeting-state visibility ─────────────────────────────────────────────

        private void RefreshTargeting()
        {
            if (_model == null) { ClearTargeting(); return; }
            if (_gesture != Gesture.Dragging && _gesture != Gesture.ArmedByTap)
            {
                ClearTargeting();
                return;
            }

            HashSet<InstanceId> targets = _model.TargetsFor(_source);
            if (targets == null) ClearTargeting();
            else                 SetTargeting(targets, _model.BannerFor(_source));
        }

        private void SetTargeting(HashSet<InstanceId> targets, string bannerText)
        {
            _activeTargets = targets;
            ShowBanner(bannerText);
            TargetingChanged?.Invoke(targets);
            ApplyHeroTargetCues();
        }

        private void ClearTargeting()
        {
            if (_activeTargets == null) return;
            _activeTargets = null;
            HideBanner();
            TargetingChanged?.Invoke(null);
            _p0HeroPlate?.SetTargetable(false);
            _enemyHeroPlate?.SetTargetable(false);
        }

        private void ApplyHeroTargetCues()
        {
            if (_currentState == null || _model == null) return;
            _p0HeroPlate?.SetTargetable(
                _activeTargets != null && _activeTargets.Contains(_model.HeroIdOf(_p0Id)));
            _enemyHeroPlate?.SetTargetable(
                _activeTargets != null && _activeTargets.Contains(_model.HeroIdOf(_model.EnemyId())));
        }

        // ── Armed banner (built lazily; same primitives approach as the reject toast) ──

        private void ShowBanner(string text)
        {
            if (_bannerGo == null) BuildBanner();
            _bannerLabel.text = text;
            _bannerGo.SetActive(true);
        }

        private void HideBanner()
        {
            if (_bannerGo != null) _bannerGo.SetActive(false);
        }

        private void BuildBanner()
        {
            _bannerGo = new GameObject("TargetingBanner");
            _bannerGo.transform.position = new Vector3(0f, -2.35f, 0f);

            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(_bannerGo.transform, false);
            faceGo.transform.localScale = new Vector3(4.6f, 0.42f, 1f);
            Destroy(faceGo.GetComponent<MeshCollider>());
            var face = faceGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(face, MaterialCatalog.Plate);
            var mpb = new MaterialPropertyBlock();
            face.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", new Color(0.13f, 0.22f, 0.35f));
            face.SetPropertyBlock(mpb);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(_bannerGo.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _bannerLabel = labelGo.AddComponent<TextMeshPro>();
            _bannerLabel.fontSize  = 1.1f;
            _bannerLabel.alignment = TextAlignmentOptions.Center;
            _bannerLabel.color     = Color.white;
            _bannerLabel.rectTransform.sizeDelta = new Vector2(4.6f, 0.42f);
            _bannerLabel.textWrappingMode = TextWrappingModes.NoWrap;
        }

        // ── Keyboard shortcuts ─────────────────────────────────────────────────────

        private void HandleKeyboard()
        {
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_currentState.ActivePlayer != _p0Id) return;

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))
            {
                CancelGesture();
                Submit(new EndTurnIntent(_p0Id));
            }
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

        // clearMarks: false when the cards are already gone (phase advanced / our redraw landed) —
        // touching a destroyed presenter would throw, so just drop the bookkeeping.
        private void ResetMulliganSelection(bool clearMarks)
        {
            if (clearMarks)
                foreach (var kv in _tossPresenters)
                    kv.Value?.SetMulliganMark(false);
            _tossPresenters.Clear();
            _mulliganToss.Clear();
            MulliganSelectionChanged?.Invoke(0);
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
        private void Submit(PlayerIntent intent)
        {
            if (_validator != null && _currentState != null
                && !_validator.CanExecute(_currentState, intent, out RejectReason? reason))
            {
                // §14 step 4 diagnostic: a local reject names the mirror state it judged
                // against — stale-version rejects are invisible without this.
                Debug.LogWarning($"[InputRouter] local reject {reason} judged on mirror " +
                                 $"v{_currentState.StateVersion} phase={_currentState.Phase} " +
                                 $"active={_currentState.ActivePlayer} intent={intent.GetType().Name}");
                _p0Input.OnRejected(reason ?? RejectReason.NotImplemented);
                return;
            }
            _p0Input.Enqueue(intent);
        }
    }
}
