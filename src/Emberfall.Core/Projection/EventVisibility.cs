using Emberfall.Core.Contracts;

namespace Emberfall.Core.Projection
{
    /// <summary>
    /// Where an <see cref="InstanceId"/> referent lived <b>at the moment its event was emitted</b>
    /// (dev-docs/architecture/determinism-and-projection.md:24). Captured by <c>EventBuffer</c> because post-command state
    /// cannot answer it: a broken weapon is nulled and the ephemeral hero-power source never
    /// enters a zone. Server-side sidecar only — never serialized, never hashed.
    /// </summary>
    public enum IdVisibility : byte
    {
        /// <summary>Public zone (hero, equipped weapon, board, graveyard) or explicitly marked public.</summary>
        Public = 0,
        /// <summary>Seat 0's hand — visible to seat 0 only.</summary>
        Owner0 = 1,
        /// <summary>Seat 1's hand — visible to seat 1 only.</summary>
        Owner1 = 2,
        /// <summary>Deck-resident or unresolvable — visible to no client (default-deny).</summary>
        Hidden = 3,
    }

    /// <summary>Per-event emit-time verdicts for the two identity fields.</summary>
    public readonly struct EventVisibility
    {
        public readonly IdVisibility Source;
        public readonly IdVisibility Target;

        public EventVisibility(IdVisibility source, IdVisibility target)
        {
            Source = source;
            Target = target;
        }
    }
}
