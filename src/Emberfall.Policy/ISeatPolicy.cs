using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;

namespace Emberfall.Policy
{
    /// <summary>
    /// One seat's decision policy. Callers provide only the actor-visible snapshot; adapters own
    /// validation, inference, sampling, and any match-local memory needed to return a legal intent.
    /// </summary>
    public interface ISeatPolicy
    {
        /// <summary>Stable provenance label for matches and rollout records.</summary>
        string Identity { get; }

        /// <summary>The intent this seat wants next, or null when it has no decision in this state.</summary>
        ValueTask<PlayerIntent?> ChooseAsync(
            GameState state,
            PlayerId player,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Optional policy capability for reconstructing actor-visible memory from the same projected
    /// snapshot/event transitions a human client receives.
    /// </summary>
    public interface IObservedSeatPolicy : ISeatPolicy
    {
        void ObserveTransition(
            GameState before,
            IReadOnlyList<GameEvent> events,
            GameState after,
            PlayerId player);
    }
}
