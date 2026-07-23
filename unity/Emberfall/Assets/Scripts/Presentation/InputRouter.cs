using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Click state machine: converts Unity clicks into PlayerIntents for the local human
    // player (P0 in hot-seat mode) and routes them to HotSeatInput (ARCHITECTURE §6).
    //
    // States:
    //   Idle
    //     → CardSelectedFromHand  : click own hand card while it's your Main phase
    //     → AttackerSelected      : click own board minion while it's your Main phase
    //   CardSelectedFromHand
    //     → Idle + PlayCardIntent(slot, [])              : click own board slot
    //     → Idle + PlayCardIntent(0, [target])           : click enemy board card (targeted spell)
    //     → Idle + PlayCardIntent(0, [target])           : click OWN board card, when the selected
    //                                                      card's effect declares an ANY_* selector
    //                                                      (friendly-target battlecries/spells — the
    //                                                      engine rejects a target-less play of these)
    //     → Idle (deselect)                              : click same card again or Escape
    //   AttackerSelected
    //     → Idle + AttackIntent(attacker, defender)      : click enemy board card
    //     → Idle (deselect)                              : click same card again or Escape
    //
    // Hero power : click HeroPowerAnchor. Untargeted powers (SUMMON, self-heal) fire
    //   HeroPowerIntent([]) immediately. Powers whose effect declares an explicit-choice
    //   selector (ANY_*, e.g. Searing Bolt's ANY_CHARACTER) enter HeroPowerTargeting —
    //   the next click on any minion or hero supplies the target; the engine rejects an
    //   empty-target intent for these, so firing blind is never an option.
    //
    // End turn : Enter or Space key during Main phase.
    // Cancel   : Escape clears any selection.
    public sealed class InputRouter : MonoBehaviour
    {
        private enum State { Idle, CardSelectedFromHand, AttackerSelected, HeroPowerTargeting }

        private State        _state = State.Idle;
        private GameState    _currentState;
        private HotSeatInput _p0Input;
        private PlayerId     _p0Id;
        private ICardDatabase _db;

        private InstanceId    _selectedId;
        private CardPresenter _selectedPresenter;
        private HeroPlateView _p0HeroPlate;      // selection + target cue for our hero
        private HeroPlateView _enemyHeroPlate;   // target cue for the enemy hero
        private ICommandValidator _validator;

        // Set by the bootstrap to the settings overlay's IsBlockingInput. UI Toolkit does NOT
        // block Unity physics, so while a UI surface owns input we suppress board clicks here —
        // otherwise a click passes through the scrim to the cards behind it, and a card-selected
        // click on the (collider-less) corner button plays a card into empty space.
        public System.Func<bool> UiBlocksInput;
        private bool UiBlocked => UiBlocksInput != null && UiBlocksInput();

        // ── Targeting-state visibility (ui-polish 2026-07-17) ──────────────────────
        // While a targeting state is armed: a persistent banner says so, and the set of
        // legal targets is broadcast so BoardController can glow ONLY those (readiness
        // glow is suppressed for the duration). Null = targeting cleared.
        public event System.Action<HashSet<InstanceId>> TargetingChanged;
        private HashSet<InstanceId> _activeTargets;
        private GameObject  _bannerGo;
        private TextMeshPro _bannerLabel;

        private const string AimBanner    = "Choose a target — Esc cancels";
        private const string AttackBanner = "Choose a target to attack — Esc cancels";

        /// <summary>Wired by the bootstraps after the hero plates exist — heroes have no
        /// CardPresenter, so their selection/target highlights route through the plate views.</summary>
        public void SetHeroPlates(HeroPlateView p0Plate, HeroPlateView enemyPlate)
        {
            _p0HeroPlate    = p0Plate;
            _enemyHeroPlate = enemyPlate;
        }

        public void Init(PlayerId p0Id, HotSeatInput p0Input, ICardDatabase db = null,
                         ICommandValidator validator = null)
        {
            _p0Id      = p0Id;
            _p0Input   = p0Input;
            _db        = db;
            _validator = validator;
        }

        // Subscribed to the P0 session's OnSnapshot — the IMMEDIATE truth cadence, NOT the
        // queue-delayed MatchRunner.OnStateUpdated (a4-session-seam-design.md §7 / §11 finding 7):
        // turn gating and selection-clearing must not lag behind the authority while the playback
        // queue drains, or the player is locked out / acts on a stale turn.
        public void OnStateUpdated(GameState state)
        {
            _currentState = state;
            if (_state != State.Idle && state != null && state.ActivePlayer != _p0Id)
            {
                ClearSelection();
                return;
            }
            // Refresh the armed-target set on every snapshot — board changes (a taunt dying
            // to a deathrattle, a minion gaining stealth) can alter what's legal mid-aim.
            RefreshTargeting();
        }

        private void RefreshTargeting()
        {
            switch (_state)
            {
                case State.CardSelectedFromHand:
                    if (SelectedCardRequiresTarget())
                        SetTargeting(ComputeSpellTargets(), AimBanner);
                    else
                        ClearTargeting();
                    break;
                case State.AttackerSelected:
                    SetTargeting(ComputeAttackTargets(_selectedId), AttackBanner);
                    break;
                case State.HeroPowerTargeting:
                    SetTargeting(ComputeHeroPowerTargets(), AimBanner);
                    break;
                default:
                    ClearTargeting();
                    break;
            }
        }

        // ── Entry points called by ClickableCard / ClickableBoardSlot / ClickableHeroPower ──

        public void OnCardClicked(ClickableCard card)
        {
            if (UiBlocked) return;
            if (_currentState == null || _p0Input == null) return;

            switch (_state)
            {
                case State.Idle:
                    // Only respond to P0's cards on P0's Main turn.
                    if (card.OwnerId != _p0Id) return;
                    if (_currentState.ActivePlayer != _p0Id) return;
                    if (_currentState.Phase != TurnPhase.Main) return;

                    if (card.Zone == Zone.Hand)
                        SelectCard(card, State.CardSelectedFromHand);
                    else if (card.Zone == Zone.Board)
                        SelectCard(card, State.AttackerSelected);
                    break;

                case State.CardSelectedFromHand:
                    if (card.CardId.Equals(_selectedId))
                    {
                        ClearSelection(); // deselect on second click
                    }
                    else if (card.Zone == Zone.Board
                             && (card.OwnerId != _p0Id || SelectedCardRequiresTarget()))
                    {
                        // Targeted play: aim the selected hand card at this board minion. Enemy minions
                        // are always clickable targets; own minions only when the selected card actually
                        // wants a chosen target (ANY_* selector), so a stray click on your own board
                        // doesn't fire a vanilla card at slot 0.
                        Submit(new PlayCardIntent(
                            _p0Id, _selectedId, 0,
                            new[] { new TargetRef(card.CardId) }));
                        ClearSelection();
                    }
                    else if (card.OwnerId == _p0Id)
                    {
                        Reselect(card); // clicking another of our cards switches selection, not a dead click
                    }
                    break;

                case State.AttackerSelected:
                    if (card.CardId.Equals(_selectedId))
                    {
                        ClearSelection();
                    }
                    else if (card.OwnerId != _p0Id)
                    {
                        Submit(new AttackIntent(
                            _p0Id, _selectedId, new TargetRef(card.CardId)));
                        ClearSelection();
                    }
                    else
                    {
                        Reselect(card); // clicking another of our cards switches selection, not a dead click
                    }
                    break;

                case State.HeroPowerTargeting:
                    // ANY_CHARACTER: any board minion (either side) is a legal pick; hand cards are not.
                    if (card.Zone == Zone.Board)
                    {
                        Submit(new HeroPowerIntent(
                            _p0Id, new[] { new TargetRef(card.CardId) }));
                        ClearSelection();
                    }
                    break;
            }
        }

        public void OnBoardSlotClicked(ClickableBoardSlot slot)
        {
            if (UiBlocked) return;
            if (_currentState == null || _p0Input == null) return;
            if (_state != State.CardSelectedFromHand) return;
            if (slot.OwnerId != _p0Id) return;
            if (_currentState.ActivePlayer != _p0Id) return;

            Submit(new PlayCardIntent(
                _p0Id, _selectedId, slot.SlotIndex,
                System.Array.Empty<TargetRef>()));
            ClearSelection();
        }

        // Heroes are click targets, not selectables: attack the enemy hero with the selected
        // minion ("going face"), or aim a targeted spell at either hero. Illegal picks
        // (friendly-fire attacks, Taunt on board) are rejected by the engine, not filtered here.
        public void OnHeroClicked(PlayerId owner)
        {
            if (UiBlocked) return;
            if (_currentState == null || _p0Input == null) return;

            switch (_state)
            {
                case State.Idle:
                    // Our hero is also an attacker when armed (engine: ValidateAttack IsHero +
                    // weapon). Select it like a board minion; the validator rejects a weaponless
                    // swing. Highlight routes through the plate view (no CardPresenter on heroes).
                    if (owner == _p0Id
                        && _currentState.ActivePlayer == _p0Id
                        && _currentState.Phase == TurnPhase.Main)
                    {
                        _selectedId        = HeroIdOf(owner);
                        _selectedPresenter = null;
                        _state             = State.AttackerSelected;
                        _p0HeroPlate?.SetSelected(true);
                        RefreshTargeting();
                    }
                    break;

                case State.AttackerSelected:
                    if (owner != _p0Id)
                    {
                        Submit(new AttackIntent(
                            _p0Id, _selectedId, new TargetRef(HeroIdOf(owner))));
                        ClearSelection();
                    }
                    else if (_selectedId.Equals(HeroIdOf(owner)))
                    {
                        ClearSelection(); // second click on our selected hero deselects
                    }
                    break;

                case State.CardSelectedFromHand:
                    Submit(new PlayCardIntent(
                        _p0Id, _selectedId, 0,
                        new[] { new TargetRef(HeroIdOf(owner)) }));
                    ClearSelection();
                    break;

                case State.HeroPowerTargeting:
                    Submit(new HeroPowerIntent(
                        _p0Id, new[] { new TargetRef(HeroIdOf(owner)) }));
                    ClearSelection();
                    break;
            }
        }

        private InstanceId HeroIdOf(PlayerId owner)
        {
            foreach (var ps in _currentState.Players)
                if (ps.Id == owner) return ps.Hero.Id;
            return default;
        }

        public void OnHeroPowerClicked(PlayerId owner)
        {
            if (UiBlocked) return;
            if (_currentState == null || _p0Input == null) return;
            if (owner != _p0Id) return;
            if (_currentState.ActivePlayer != _p0Id) return;
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_state == State.HeroPowerTargeting) { ClearSelection(); return; } // second click cancels
            if (_state != State.Idle) return;

            if (HeroPowerRequiresTarget())
            {
                _state = State.HeroPowerTargeting; // next minion/hero click supplies the target
                RefreshTargeting();
                return;
            }
            Submit(new HeroPowerIntent(_p0Id, System.Array.Empty<TargetRef>()));
        }

        // Mirrors the engine's ValidatePlayCard rule: a play-trigger effect with an explicit-choice
        // (ANY_*) selector needs a player-chosen target. With no db wired this returns false and the
        // engine's rejection is the backstop.
        private bool SelectedCardRequiresTarget()
        {
            if (_db == null || _currentState == null) return false;
            foreach (var ps in _currentState.Players)
            {
                if (ps.Id != _p0Id) continue;
                foreach (var ci in ps.Hand)
                {
                    if (!ci.Id.Equals(_selectedId)) continue;
                    if (!_db.TryGet(ci.DefinitionId, out var def)) return false;
                    foreach (var eff in def.Effects)
                        if ((eff.Trigger == Trigger.ON_PLAY || eff.Trigger == Trigger.ON_CAST)
                            && eff.Selector != null && eff.Selector.StartsWith("ANY_"))
                            return true;
                    return false;
                }
            }
            return false;
        }

        // Mirrors the engine's ValidateHeroPower rule: an ON_CAST effect with an explicit-choice
        // (ANY_*) selector needs a player-chosen target. With no db wired the click falls through
        // to the immediate fire and the engine's rejection is the backstop.
        private bool HeroPowerRequiresTarget()
        {
            if (_db == null) return false;
            foreach (var ps in _currentState.Players)
            {
                if (ps.Id != _p0Id) continue;
                if (!_db.TryGet(ps.Hero.HeroPowerId, out var def)) return false;
                foreach (var eff in def.Effects)
                    if (eff.Trigger == Trigger.ON_CAST && eff.Selector != null && eff.Selector.StartsWith("ANY_"))
                        return true;
            }
            return false;
        }

        // ── Targeting-state visibility ─────────────────────────────────────────────

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
            if (_currentState == null) return;
            _p0HeroPlate?.SetTargetable(
                _activeTargets != null && _activeTargets.Contains(HeroIdOf(_p0Id)));
            _enemyHeroPlate?.SetTargetable(
                _activeTargets != null && _activeTargets.Contains(HeroIdOf(EnemyId())));
        }

        private PlayerId EnemyId()
        {
            foreach (var ps in _currentState.Players)
                if (ps.Id != _p0Id) return ps.Id;
            return default;
        }

        private HashSet<InstanceId> ComputeSpellTargets()
        {
            var set = new HashSet<InstanceId>();
            if (_db == null || _currentState == null) return set;
            foreach (var ps in _currentState.Players)
            {
                if (ps.Id != _p0Id) continue;
                foreach (var ci in ps.Hand)
                    if (ci.Id.Equals(_selectedId) && _db.TryGet(ci.DefinitionId, out var def))
                    {
                        CollectSelectorTargets(def, set);
                        return set;
                    }
            }
            return set;
        }

        private HashSet<InstanceId> ComputeHeroPowerTargets()
        {
            var set = new HashSet<InstanceId>();
            if (_db == null || _currentState == null) return set;
            foreach (var ps in _currentState.Players)
                if (ps.Id == _p0Id && _db.TryGet(ps.Hero.HeroPowerId, out var def))
                    CollectSelectorTargets(def, set);
            return set;
        }

        // Mirrors CommandProcessor.TargetsSatisfySelectors + the enemy-STEALTH targeting
        // exclusion (ValidatePlayCard). Courtesy affordance — the validator still rules.
        private void CollectSelectorTargets(CardDefinition def, HashSet<InstanceId> set)
        {
            var selectors = new List<string>();
            foreach (var eff in def.Effects)
                if ((eff.Trigger == Trigger.ON_PLAY || eff.Trigger == Trigger.ON_CAST)
                    && eff.Selector != null && eff.Selector.StartsWith("ANY_"))
                    selectors.Add(eff.Selector);
            if (selectors.Count == 0) return;

            foreach (var ps in _currentState.Players)
            {
                bool friendly = ps.Id == _p0Id;
                if (SatisfiesSelectors(selectors, isMinion: false, friendly))
                    set.Add(ps.Hero.Id);
                foreach (var m in ps.Board)
                {
                    if (!friendly && m.Keywords.Contains(Keyword.STEALTH)) continue;
                    if (SatisfiesSelectors(selectors, isMinion: true, friendly))
                        set.Add(m.Id);
                }
            }
        }

        private static bool SatisfiesSelectors(List<string> selectors, bool isMinion, bool friendly)
        {
            foreach (var s in selectors)
                switch (s)
                {
                    case "ANY_FRIENDLY":        if (!friendly)             return false; break;
                    case "ANY_ENEMY":           if (friendly)              return false; break;
                    case "ANY_MINION":          if (!isMinion)             return false; break;
                    case "ANY_FRIENDLY_MINION": if (!isMinion || !friendly) return false; break;
                    // ANY_CHARACTER (and future ANY_*) accepts every live character.
                }
            return true;
        }

        // Mirrors the defender-side half of CommandProcessor.ValidateAttack: stealth hides,
        // a non-stealthed taunt wall restricts to taunts, rush-on-summon-turn can't go face.
        private HashSet<InstanceId> ComputeAttackTargets(InstanceId attackerId)
        {
            var set = new HashSet<InstanceId>();
            if (_currentState == null) return set;

            bool rushBlocksFace = false;
            PlayerState enemy = null;
            foreach (var ps in _currentState.Players)
            {
                if (ps.Id == _p0Id)
                {
                    foreach (var m in ps.Board)
                        if (m.Id.Equals(attackerId) && m.SummonedThisTurn
                            && m.Keywords.Contains(Keyword.RUSH))
                            rushBlocksFace = true;
                }
                else enemy = ps;
            }
            if (enemy == null) return set;

            bool anyTaunt = false;
            foreach (var m in enemy.Board)
                if (m.Keywords.Contains(Keyword.TAUNT) && !m.Keywords.Contains(Keyword.STEALTH))
                { anyTaunt = true; break; }

            foreach (var m in enemy.Board)
            {
                if (m.Keywords.Contains(Keyword.STEALTH)) continue;
                if (anyTaunt && !m.Keywords.Contains(Keyword.TAUNT)) continue;
                set.Add(m.Id);
            }
            if (!anyTaunt && !rushBlocksFace)
                set.Add(enemy.Hero.Id);
            return set;
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

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                ClearSelection();

            if (UiBlocked) return;
            if (_currentState == null || _p0Input == null) return;
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_currentState.ActivePlayer != _p0Id) return;

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))
            {
                ClearSelection();
                Submit(new EndTurnIntent(_p0Id));
                return;
            }

            // Generous play area (Sam, 2026-07-17): with an untargeted hand card selected,
            // a click on empty space plays it — the precise slot quads were frustrating.
            // Clicks that hit any collider keep their existing meaning (cards, slots,
            // heroes, buttons all handle themselves); targeted cards still require an aim.
            if (_state == State.CardSelectedFromHand
                && Input.GetMouseButtonDown(0)
                && !SelectedCardRequiresTarget())
            {
                var cam = Camera.main;
                if (cam != null && !Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out _, 100f))
                {
                    Submit(new PlayCardIntent(_p0Id, _selectedId, OwnBoardCount(),
                                              System.Array.Empty<TargetRef>()));
                    ClearSelection();
                }
            }
        }

        private int OwnBoardCount()
        {
            foreach (var ps in _currentState.Players)
                if (ps.Id == _p0Id) return ps.Board.Count;
            return 0;
        }

        // On-screen end-turn button (ui/match-hud.md §6 item 1) — same guards as the
        // keyboard path; keyboard stays.
        public void OnEndTurnClicked()
        {
            if (_currentState == null || _p0Input == null) return;
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_currentState.ActivePlayer != _p0Id) return;

            ClearSelection();
            Submit(new EndTurnIntent(_p0Id));
        }

        // In-match concede (C-G1-3), invoked by the settings overlay's confirm. Deliberately
        // UNGUARDED by turn/phase: concede is legal at any time (CommandProcessor: KnownPlayer +
        // not GameOver), unlike end-turn — so a player can forfeit on the opponent's turn. Routed
        // through the same Submit pipe; the local validator passes it, so it reaches the authority,
        // which is the sole forfeit authority (decision 23). Not UiBlocked-gated — the UI is the caller.
        public void OnConcedeClicked()
        {
            if (_p0Input == null) return;
            ClearSelection();
            Submit(new ConcedeIntent(_p0Id));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────

        // Courtesy pre-validation over the PROJECTED truth state (a4 design §7; ADR-0004 — an
        // affordance, not prediction). The viewer's own hand/mana/board are un-redacted in their
        // own projection, so verdicts match the authority for everything except hidden-info
        // rejects, which the authority still catches. A local reject routes to the same
        // OnRejected path a server reject would take (toast copy + grey-out styling land in E2).
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

        // Switch selection to another of our own cards mid-selection, mirroring the Idle-state
        // entry rules (Main phase, our turn). Hand → CardSelectedFromHand, board → AttackerSelected.
        private void Reselect(ClickableCard card)
        {
            if (_currentState.ActivePlayer != _p0Id) return;
            if (_currentState.Phase != TurnPhase.Main) return;
            if (card.Zone == Zone.Hand)
                SelectCard(card, State.CardSelectedFromHand);
            else if (card.Zone == Zone.Board)
                SelectCard(card, State.AttackerSelected);
        }

        private void SelectCard(ClickableCard card, State newState)
        {
            ClearSelection();
            _selectedId        = card.CardId;
            _selectedPresenter = card.Presenter;
            _state             = newState;
            _selectedPresenter?.SetSelectionHighlight(true);
            RefreshTargeting();
        }

        private void ClearSelection()
        {
            _selectedPresenter?.SetSelectionHighlight(false);
            _p0HeroPlate?.SetSelected(false);
            _selectedPresenter = null;
            _selectedId        = default;
            _state             = State.Idle;
            ClearTargeting();
        }
    }
}
