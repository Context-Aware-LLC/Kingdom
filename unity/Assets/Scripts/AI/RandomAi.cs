using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.AI
{
    /// <summary>
    /// Minimal dummy AI for the M1 vertical slice. Keeps all cards in mulligan, then immediately
    /// ends every turn. Sufficient to drive the match loop through fatigue-to-game-over for testing.
    /// Replace with a real search-based AI in M2.
    /// </summary>
    public sealed class RandomAi : IIntentSource
    {
        private readonly PlayerId _playerId;
        private GameState _state;
        private PlayerIntent _pending;

        public RandomAi(PlayerId playerId) => _playerId = playerId;

        public void NotifyStateUpdate(GameState state)
        {
            _state = state;
            _pending = ComputeIntent(state);
        }

        public bool TryGetIntent(out PlayerIntent intent)
        {
            intent = _pending;
            _pending = null;
            return intent != null;
        }

        public void OnRejected(RejectReason reason)
        {
            Debug.LogWarning($"[RandomAi:{_playerId}] intent rejected — {reason}. Queuing EndTurn as fallback.");
            if (_state != null && _state.ActivePlayer == _playerId && _state.Phase == TurnPhase.Main)
                _pending = new EndTurnIntent(_playerId);
        }

        private PlayerIntent ComputeIntent(GameState state)
        {
            if (state == null || state.Outcome != MatchOutcome.InProgress)
                return null;

            if (state.Phase == TurnPhase.Mulligan)
            {
                int seat = state.SeatOf(_playerId);
                bool done = seat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
                // Keep the entire opening hand — pass an empty replace list.
                return done ? null : new MulliganIntent(_playerId, System.Array.Empty<InstanceId>());
            }

            if (state.Phase == TurnPhase.Main && state.ActivePlayer == _playerId)
                return new EndTurnIntent(_playerId);

            return null;
        }
    }
}
