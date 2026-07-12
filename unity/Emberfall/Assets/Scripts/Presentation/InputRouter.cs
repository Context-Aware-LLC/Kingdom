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
    // Hero power : click HeroPowerAnchor → HeroPowerIntent([]) immediately.
    //   Engine resolves target via DefaultTargets (DEAL_DAMAGE → enemy hero, HEAL → friendly hero,
    //   SUMMON → no target). Explicit targeting cursor is deferred to Phase C step 5.
    //
    // End turn : Enter or Space key during Main phase.
    // Cancel   : Escape clears any selection.
    public sealed class InputRouter : MonoBehaviour
    {
        private enum State { Idle, CardSelectedFromHand, AttackerSelected }

        private State        _state = State.Idle;
        private GameState    _currentState;
        private HotSeatInput _p0Input;
        private PlayerId     _p0Id;

        private InstanceId    _selectedId;
        private CardPresenter _selectedPresenter;

        public void Init(PlayerId p0Id, HotSeatInput p0Input)
        {
            _p0Id    = p0Id;
            _p0Input = p0Input;
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
            if (_state != State.Idle) return;

            _p0Input.Enqueue(new HeroPowerIntent(_p0Id, System.Array.Empty<TargetRef>()));
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
