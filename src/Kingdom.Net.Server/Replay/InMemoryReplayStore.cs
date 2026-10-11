using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Net.Server.Replay
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
            /// <summary>Voided per decision 24 — <see cref="Outcome"/> stays null by definition.</summary>
            public bool Abandoned { get; set; }
        }

        private readonly Dictionary<string, Record> _matches = new Dictionary<string, Record>();

        public IReadOnlyDictionary<string, Record> Matches => _matches;

        public void Begin(MatchId id, ulong seed, PlayerId p0, PlayerId p1)
            => _matches[id.Value] = new Record(seed, p0, p1);

        public void Append(MatchId id, PlayerIntent intent)
            => _matches[id.Value].Intents.Add(intent);

        public void Complete(MatchId id, MatchOutcome outcome)
            => _matches[id.Value].Outcome = outcome;

        public void Abandon(MatchId id)
            => _matches[id.Value].Abandoned = true;
    }
}
