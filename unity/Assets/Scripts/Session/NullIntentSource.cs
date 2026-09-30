using Emberfall.Core.Contracts;

namespace Emberfall.Session
{
    /// <summary>
    /// An <see cref="IIntentSource"/> that never acts (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b3-networked-session-design.md §3.1). Sits at
    /// the REMOTE seat index of a networked <c>MatchRunner</c>: under <see cref="SeatOwnership"/>.Single
    /// the runner's fan-out no longer reaches it, so its inertness is no longer load-bearing (ruling
    /// 3 retired that invariant) — but the opponent's seat still needs an index-parallel source that
    /// yields nothing, because the opponent's moves arrive as server snapshots, never as local intents.
    /// </summary>
    public sealed class NullIntentSource : IIntentSource
    {
        public bool TryGetIntent(out PlayerIntent intent)
        {
            intent = null!;
            return false;
        }

        public void OnRejected(RejectReason reason) { }

        public void NotifyStateUpdate(GameState state) { }
    }
}
