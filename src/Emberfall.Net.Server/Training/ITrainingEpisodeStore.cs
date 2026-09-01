using Emberfall.Core;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Training
{
    /// <summary>
    /// Optional observational sink for turning a live authoritative match into a raw training
    /// episode. Implementations must never influence rules or wire output; <see cref="MatchSession"/>
    /// disables capture for the affected match if any call fails.
    /// </summary>
    public interface ITrainingEpisodeStore
    {
        void Begin(
            MatchId id,
            ulong seed,
            PlayerId p0,
            PlayerId p1,
            MatchSetup setup,
            string p0Policy,
            string p1Policy);

        void RecordAccepted(
            MatchId id,
            PlayerId actor,
            string beforeObservationSha256,
            string afterObservationSha256);

        void Complete(
            MatchId id,
            MatchOutcome outcome,
            string finalP0ObservationSha256,
            string finalP1ObservationSha256);

        void Abandon(MatchId id);
    }
}
