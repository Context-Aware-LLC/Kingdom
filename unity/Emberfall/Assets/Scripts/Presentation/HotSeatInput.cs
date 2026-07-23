using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Human player intent source for hot-seat local mode (ARCHITECTURE §6).
    // InputRouter pushes intents here; MatchRunner drains them each frame.
    // Mulligan is driven by the player through the mulligan picker (C-G1-2b) — the old silent
    // auto-keep here was the "correctness bug online" M2-KICKOFF §E2 called out, now gone. The
    // AI/remote seat confirms itself (PlayingAi / the other client); only this human seat waits
    // on the UI, which every bootstrap that creates a HotSeatInput also wires.
    public sealed class HotSeatInput : IIntentSource
    {
        private readonly PlayerId _playerId;
        private readonly Queue<PlayerIntent> _queue = new();

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

        // No-op: the human's mulligan is submitted by the mulligan picker via
        // InputRouter.OnMulliganConfirmClicked, not auto-confirmed here. Kept to satisfy
        // IIntentSource; the AI sources use their own NotifyStateUpdate to auto-keep.
        public void NotifyStateUpdate(GameState state) { }
    }
}
