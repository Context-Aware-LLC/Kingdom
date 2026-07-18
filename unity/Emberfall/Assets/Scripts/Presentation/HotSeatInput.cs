using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Human player intent source for hot-seat local mode (ARCHITECTURE §6).
    // InputRouter pushes intents here; MatchRunner drains them each frame.
    // Mulligan is auto-confirmed (keep all) so the match starts without a mulligan UI.
    public sealed class HotSeatInput : IIntentSource
    {
        private readonly PlayerId _playerId;
        private readonly Queue<PlayerIntent> _queue = new();
        private bool _mulliganConfirmed;

        public HotSeatInput(PlayerId playerId) => _playerId = playerId;

        // Called by InputRouter to push a human-driven intent into the queue.
        public void Enqueue(PlayerIntent intent)
        {
            if (intent != null) _queue.Enqueue(intent);
        }

        public bool TryGetIntent(out PlayerIntent intent)
        {
            if (_queue.Count > 0) { intent = _queue.Dequeue(); return true; }
            intent = null;
            return false;
        }

        /// <summary>Raised on every rejection — local validator and authority both land here
        /// (InputRouter.Submit and MatchRunner both call OnRejected). Feeds the reject toast.</summary>
        public event Action<RejectReason> Rejected;

        public void OnRejected(RejectReason reason)
        {
            Debug.LogWarning($"[HotSeatInput:{_playerId}] intent rejected — {reason}");
            Rejected?.Invoke(reason);
        }

        public void NotifyStateUpdate(GameState state)
        {
            if (state == null || _mulliganConfirmed) return;
            if (state.Phase != TurnPhase.Mulligan) return;
            int seat = state.SeatOf(_playerId);
            bool done = seat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
            if (done) return;
            // Keep the entire opening hand (empty replace list).
            _queue.Enqueue(new MulliganIntent(_playerId, Array.Empty<InstanceId>()));
            _mulliganConfirmed = true;
        }
    }
}
