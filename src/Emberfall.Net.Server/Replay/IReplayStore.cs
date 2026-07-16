using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Replay
{
    /// <summary>
    /// The persistence seam (b1-match-server-design.md §9). B1 records `(seed, ordered accepted
    /// intents)` — synthetic intents included, so a replay reproduces timer outcomes bit-for-bit.
    /// B2 plugs Postgres in here; until then a restart forfeits in-flight matches (design §1).
    /// </summary>
    public interface IReplayStore
    {
        void Begin(MatchId id, ulong seed, PlayerId p0, PlayerId p1);
        void Append(MatchId id, PlayerIntent intent);
        void Complete(MatchId id, MatchOutcome outcome);
    }
}
