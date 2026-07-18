using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
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
        private HeroPlateView _p0HeroPlate;   // selection cue for hero-as-attacker
        private ICommandValidator _validator;

        /// <summary>Wired by GameBootstrap after the hero plates exist — heroes have no
        /// CardPresenter, so their selection highlight routes through the plate view.</summary>
        public void SetHeroPlate(HeroPlateView p0Plate) => _p0HeroPlate = p0Plate;

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
                ClearSelection();
        }

        // ── Entry points called by ClickableCard / ClickableBoardSlot / ClickableHeroPower ──

        public void OnCardClicked(ClickableCard card)
        {
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
            if (_currentState == null || _p0Input == null) return;
            if (owner != _p0Id) return;
            if (_currentState.ActivePlayer != _p0Id) return;
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_state == State.HeroPowerTargeting) { ClearSelection(); return; } // second click cancels
            if (_state != State.Idle) return;

            if (HeroPowerRequiresTarget())
            {
                _state = State.HeroPowerTargeting; // next minion/hero click supplies the target
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

        // ── Keyboard shortcuts ─────────────────────────────────────────────────────

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                ClearSelection();

            if (_currentState == null || _p0Input == null) return;
            if (_currentState.Phase != TurnPhase.Main) return;
            if (_currentState.ActivePlayer != _p0Id) return;

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space))
            {
                ClearSelection();
                Submit(new EndTurnIntent(_p0Id));
            }
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
        }

        private void ClearSelection()
        {
            _selectedPresenter?.SetSelectionHighlight(false);
            _p0HeroPlate?.SetSelected(false);
            _selectedPresenter = null;
            _selectedId        = default;
            _state             = State.Idle;
        }
    }
}
