using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.Presentation
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

        // The intent MatchRunner took last. The submit seam is single-in-flight
        // (NetworkedMatchSession §7.1), so an authority rejection always answers this one.
        private PlayerIntent _inFlight;

        public HotSeatInput(PlayerId playerId) => _playerId = playerId;

        /// <summary>The authority refused the intent it was sent. InputRouter puts a refused
        /// play's card back in the hand: nothing else would, because a refusal emits no snapshot.
        /// This is the card's position, not a player-facing message — rejections have no toast by
        /// decision (#268, 2026-08-26), and this event must not grow one.</summary>
        public event System.Action<PlayerIntent> InFlightRejected;

        // Called by InputRouter to push a human-driven intent into the queue.
        public void Enqueue(PlayerIntent intent)
        {
            if (intent != null) _queue.Enqueue(intent);
        }

        public bool TryGetIntent(out PlayerIntent intent)
        {
            if (_queue.Count > 0) { intent = _inFlight = _queue.Dequeue(); return true; }
            intent = null;
            return false;
        }

        /// <summary>An AUTHORITY rejection (MatchRunner, after a submit resolves). The log is the
        /// only player-invisible trace of it.</summary>
        public void OnRejected(RejectReason reason)
        {
            Debug.LogWarning($"[HotSeatInput:{_playerId}] intent rejected — {reason}");
            PlayerIntent rejected = _inFlight;
            _inFlight = null;
            if (rejected != null) InFlightRejected?.Invoke(rejected);
        }

        /// <summary>A LOCAL rejection (InputRouter's courtesy validator). Never sent, so it is not
        /// the in-flight intent, and routing it through <see cref="OnRejected"/> would hand the
        /// refusal to whatever play is genuinely still waiting on the server.</summary>
        public void OnLocalRejected(RejectReason reason)
        {
            Debug.LogWarning($"[HotSeatInput:{_playerId}] intent rejected — {reason}");
        }

        // No-op: the human's mulligan is submitted by the mulligan picker via
        // InputRouter.OnMulliganConfirmClicked, not auto-confirmed here. Kept to satisfy
        // IIntentSource; the AI sources use their own NotifyStateUpdate to auto-keep.
        public void NotifyStateUpdate(GameState state) { }
    }
}
