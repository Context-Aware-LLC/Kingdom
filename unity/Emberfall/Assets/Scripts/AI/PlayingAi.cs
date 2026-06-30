using System;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.AI
{
    /// <summary>
    /// Greedy AI for the M1 vertical slice. Each turn: play the first affordable card, then
    /// attack with each ready minion (validator enforces taunt), then end turn. Uses
    /// ICommandValidator to gate every intent before queuing so rejections are nearly impossible.
    /// Replace with a search-based AI in M2.
    /// </summary>
    public sealed class PlayingAi : IIntentSource
    {
        private readonly PlayerId _playerId;
        private readonly ICommandValidator _validator;
        private readonly ICardDatabase _db;

        private GameState _state;
        private PlayerIntent _pending;

        public PlayingAi(PlayerId playerId, ICardDatabase db)
        {
            _playerId  = playerId;
            _db        = db;
            _validator = new CommandProcessor(db);
        }

        public void NotifyStateUpdate(GameState state)
        {
            _state   = state;
            _pending = ComputeIntent(state);
        }

        public bool TryGetIntent(out PlayerIntent intent)
        {
            intent   = _pending;
            _pending = null;
            return intent != null;
        }

        public void OnRejected(RejectReason reason)
        {
            Debug.LogWarning($"[PlayingAi:{_playerId}] intent rejected — {reason}. Falling back to EndTurn.");
            if (_state != null && _state.ActivePlayer == _playerId && _state.Phase == TurnPhase.Main)
                _pending = new EndTurnIntent(_playerId);
        }

        private PlayerIntent ComputeIntent(GameState state)
        {
            if (state == null || state.Outcome != MatchOutcome.InProgress)
                return null;

            // Mulligan: keep entire opening hand.
            if (state.Phase == TurnPhase.Mulligan)
            {
                int seat = state.SeatOf(_playerId);
                bool done = seat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
                return done ? null : new MulliganIntent(_playerId, Array.Empty<InstanceId>());
            }

            if (state.Phase != TurnPhase.Main || state.ActivePlayer != _playerId)
                return null;

            int mySeat  = state.SeatOf(_playerId);
            int foeSeat = mySeat == 0 ? 1 : 0;
            var ps      = state.Players[mySeat];
            var foe     = state.Players[foeSeat];

            // 1. Play the first card in hand we can legally play (no targeting — empty target list).
            foreach (var card in ps.Hand)
            {
                var def = _db.Get(card.DefinitionId);
                if (ps.Mana.Available < def.Cost) continue;

                // Append to the end of board; validator will reject if board is full.
                var play = new PlayCardIntent(_playerId, card.Id, ps.Board.Count,
                                             Array.Empty<TargetRef>());
                if (_validator.CanExecute(state, play, out _))
                    return play;
            }

            // 2. Attack with the first ready minion on our board.
            foreach (var attacker in ps.Board)
            {
                // Try every foe minion first (validator enforces taunt and stealth rules).
                foreach (var defender in foe.Board)
                {
                    var atk = new AttackIntent(_playerId, attacker.Id, new TargetRef(defender.Id));
                    if (_validator.CanExecute(state, atk, out _))
                        return atk;
                }

                // Then try the foe hero.
                var heroAtk = new AttackIntent(_playerId, attacker.Id, new TargetRef(foe.Hero.Id));
                if (_validator.CanExecute(state, heroAtk, out _))
                    return heroAtk;
            }

            // 3. Nothing left to do.
            return new EndTurnIntent(_playerId);
        }
    }
}
