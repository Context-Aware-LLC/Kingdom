using Emberfall.Core.Contracts;

namespace Emberfall.Session
{
    /// <summary>
    /// Provides PlayerIntents to the MatchRunner loop (ARCHITECTURE §5 / §6).
    /// Human players implement this via UI input queues; AI players implement it algorithmically.
    /// The interface is intentionally narrow — the runner polls each frame, sources set their own pace.
    /// </summary>
    public interface IIntentSource
    {
        /// <summary>Returns true and sets <paramref name="intent"/> when a move is ready; false to skip this tick.</summary>
        bool TryGetIntent(out PlayerIntent intent);

        /// <summary>Called when the last returned intent was rejected by the core. Source must not re-queue it verbatim.</summary>
        void OnRejected(RejectReason reason);

        /// <summary>Called every time the authoritative GameState changes so the source can replan.</summary>
        void NotifyStateUpdate(GameState state);
    }
}
