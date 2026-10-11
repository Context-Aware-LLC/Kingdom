using System;
using System.Collections.Generic;

namespace Kingdom.Core.Contracts
{
    /// <summary>
    /// Outcome of <see cref="ICommandProcessor.Execute"/>. Invariant (L2 contract test,
    /// hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §3): <c>Reason == null</c> iff <c>Accepted</c>, and a rejected
    /// command produces zero events and does not bump the version.
    /// </summary>
    public sealed class CommandResult
    {
        public bool Accepted { get; }
        public RejectReason? Reason { get; }
        public IReadOnlyList<GameEvent> Events { get; }
        public ulong StateVersion { get; }

        private static readonly IReadOnlyList<GameEvent> Empty = Array.Empty<GameEvent>();

        private CommandResult(bool accepted, RejectReason? reason, IReadOnlyList<GameEvent> events, ulong stateVersion)
        {
            Accepted = accepted;
            Reason = reason;
            Events = events ?? Empty;
            StateVersion = stateVersion;
        }

        public static CommandResult Rejected(RejectReason reason, ulong stateVersion)
            => new CommandResult(false, reason, Empty, stateVersion);

        public static CommandResult Ok(IReadOnlyList<GameEvent> events, ulong stateVersion)
            => new CommandResult(true, null, events, stateVersion);
    }
}
