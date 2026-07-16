using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Replay
{
    /// <summary>In-process store: the replay-determinism test reads these back (design §14.1).</summary>
    public sealed class InMemoryReplayStore : IReplayStore
    {
        public sealed class Record
        {
            public Record(ulong seed, PlayerId p0, PlayerId p1)
            {
                Seed = seed;
                P0 = p0;
                P1 = p1;
            }

            public ulong Seed { get; }
            public PlayerId P0 { get; }
            public PlayerId P1 { get; }
            public List<PlayerIntent> Intents { get; } = new List<PlayerIntent>();
            public MatchOutcome? Outcome { get; set; }
        }

        private readonly Dictionary<string, Record> _matches = new Dictionary<string, Record>();

        public IReadOnlyDictionary<string, Record> Matches => _matches;

        public void Begin(MatchId id, ulong seed, PlayerId p0, PlayerId p1)
            => _matches[id.Value] = new Record(seed, p0, p1);

        public void Append(MatchId id, PlayerIntent intent)
            => _matches[id.Value].Intents.Add(intent);

        public void Complete(MatchId id, MatchOutcome outcome)
            => _matches[id.Value].Outcome = outcome;
    }
}
