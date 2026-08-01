using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Replay
{
    /// <summary>
    /// The persistence seam (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §9). B1 records `(seed, ordered accepted
    /// intents)` — synthetic intents included, so a replay reproduces timer outcomes bit-for-bit.
    /// B2 plugs Postgres in here; until then a restart forfeits in-flight matches (design §1).
    /// </summary>
    public interface IReplayStore
    {
        void Begin(MatchId id, ulong seed, PlayerId p0, PlayerId p1);
        void Append(MatchId id, PlayerIntent intent);
        void Complete(MatchId id, MatchOutcome outcome);

        /// <summary>
        /// The match ended with NO result (decision 24 — double-disconnect is a void): zero seats
        /// were connected, so nobody won, nobody lost, and nothing reaches the ladder. Distinct from
        /// <see cref="Complete"/> on purpose — a void carries no <see cref="MatchOutcome"/> at all,
        /// which is why it takes none. The journal keeps the intents (the match still happened and
        /// is still replayable); it just records no outcome against either player.
        /// </summary>
        void Abandon(MatchId id);
    }
}
