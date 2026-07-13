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

        public void Init(PlayerId p0Id, HotSeatInput p0Input, ICardDatabase db = null)
        {
            _p0Id    = p0Id;
            _p0Input = p0Input;
            _db      = db;
        }

        // Subscribed to MatchRunner.OnStateUpdated; clears selection on turn change.
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
                    else if (card.Zone == Zone.Board && card.OwnerId != _p0Id)
                    {
                        // Targeted spell: play the selected hand card targeting this enemy minion.
                        _p0Input.Enqueue(new PlayCardIntent(
                            _p0Id, _selectedId, 0,
                            new[] { new TargetRef(card.CardId) }));
                        ClearSelection();
                    }
                    break;

                case State.AttackerSelected:
                    if (card.CardId.Equals(_selectedId))
                    {
                        ClearSelection();
                    }
                    else if (card.OwnerId != _p0Id)
                    {
                        _p0Input.Enqueue(new AttackIntent(
                            _p0Id, _selectedId, new TargetRef(card.CardId)));
                        ClearSelection();
                    }
                    break;

                case State.HeroPowerTargeting:
                    // ANY_CHARACTER: any board minion (either side) is a legal pick; hand cards are not.
                    if (card.Zone == Zone.Board)
                    {
                        _p0Input.Enqueue(new HeroPowerIntent(
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

            _p0Input.Enqueue(new PlayCardIntent(
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
                case State.AttackerSelected:
                    if (owner != _p0Id)
                    {
                        _p0Input.Enqueue(new AttackIntent(
                            _p0Id, _selectedId, new TargetRef(HeroIdOf(owner))));
                        ClearSelection();
                    }
                    break;

                case State.CardSelectedFromHand:
                    _p0Input.Enqueue(new PlayCardIntent(
                        _p0Id, _selectedId, 0,
                        new[] { new TargetRef(HeroIdOf(owner)) }));
                    ClearSelection();
                    break;

                case State.HeroPowerTargeting:
                    _p0Input.Enqueue(new HeroPowerIntent(
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
            _p0Input.Enqueue(new HeroPowerIntent(_p0Id, System.Array.Empty<TargetRef>()));
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
                _p0Input.Enqueue(new EndTurnIntent(_p0Id));
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────

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
            _selectedPresenter = null;
            _selectedId        = default;
            _state             = State.Idle;
        }
    }
}
