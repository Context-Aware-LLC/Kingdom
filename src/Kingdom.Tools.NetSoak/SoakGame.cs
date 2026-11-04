using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Training.Corpus;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// Holds one match's telemetry until the run is ready to write it. A match finishes whenever
    /// its worker gets there; the telemetry file is ordered by schedule. The calls are kept as
    /// calls, in the order the session made them, so replaying them writes exactly the rows a
    /// direct sink would have.
    /// </summary>
    internal sealed class BufferedTelemetrySink : ITelemetrySink
    {
        private readonly List<Action<ITelemetrySink>> _calls = new List<Action<ITelemetrySink>>();

        public void MatchComplete(MatchCompleteEvent e) => _calls.Add(sink => sink.MatchComplete(e));
        public void MatchVoid(MatchVoidEvent e) => _calls.Add(sink => sink.MatchVoid(e));
        public void CardsSeen(CardSeenEvent e) => _calls.Add(sink => sink.CardsSeen(e));

        public void ReplayInto(ITelemetrySink sink)
        {
            foreach (Action<ITelemetrySink> call in _calls) call(sink);
        }
    }

    /// <summary>
    /// Everything one soak match produced, carried from the worker that played it to the single
    /// committer that tallies and writes it. Nothing here is shared between matches.
    /// </summary>
    internal sealed class SoakGame
    {
        public BufferedTelemetrySink Telemetry { get; } = new BufferedTelemetrySink();
        public List<string> RejectReasons { get; } = new List<string>();

        public TrainingCaptureGame? Planned;
        public CardClass Class0, Class1;
        public MatchOutcome Outcome = MatchOutcome.InProgress;
        public int Resyncs, Misattributions, Turns, Rejects, TerminalRaceRejects;
        public int AimedPlays, BarePlays, MinionAttacks, HeroSwings, HeroPowers;
        public bool Capped, OutcomeDisagrees;

        /// <summary>Clean as far as the match itself can tell; its telemetry is judged at commit.</summary>
        public bool PlayedClean;

        /// <summary>Set only for a capture match that played clean.</summary>
        public EpisodeManifest? Manifest;
    }

    /// <summary>One on-policy rollout game, carried from its worker to the committer.</summary>
    internal sealed class RolloutEpisode
    {
        public int Rejects, Stalls, Resyncs;
        public bool ReplayDisagrees;

        /// <summary>A fair-search capture's decisions by how they were made, and the rollouts the
        /// two seats ran. Zero on every other rollout.</summary>
        public int Searched, OnePlay, PendingChoice, Unresolved, Lethal;
        /// <summary>How many of <see cref="Searched"/> were the seat's own pending choice. Not a
        /// class of its own: <see cref="PendingChoice"/> is the ones that were NOT searched.</summary>
        public int SearchedChoices;
        public long SearchRollouts;

        /// <summary>Set only for a clean episode; a null manifest is a game that is not written.</summary>
        public EpisodeManifest? Manifest;
    }
}
