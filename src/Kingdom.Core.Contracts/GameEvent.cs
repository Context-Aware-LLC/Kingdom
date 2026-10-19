using System.Collections.Generic;

namespace Kingdom.Core.Contracts
{
    /// <summary>
    /// One outward semantic event (dev-docs/contracts/gameplay-and-session.md:46). Purely observational — it never
    /// feeds back into the core, so different animation speeds across clients cannot desync state.
    /// The full ordered stream is part of what the golden-replay test asserts (dev-docs/quality/testing-and-ci.md:24).
    /// </summary>
    public readonly struct GameEvent
    {
        /// <summary>What happened in game rules (closed semantic vocabulary).</summary>
        public readonly GameEventKind Kind;
        /// <summary>Global, monotonic order within the match.</summary>
        public readonly ulong Sequence;
        /// <summary>Who caused it (nullable).</summary>
        public readonly InstanceId? Source;
        /// <summary>Who it landed on (nullable).</summary>
        public readonly InstanceId? Target;
        /// <summary>Magnitude for number pops (nullable).</summary>
        public readonly int? Amount;
        /// <summary>Per-event flags (AegisAbsorbed/Lethal/Overdraw, ...).</summary>
        public readonly EventFlags Flags;
        /// <summary>Optional presentation override; null selects <see cref="PresentationCueDefaults"/>.</summary>
        public readonly PresentationCue? CueOverride;
        /// <summary>Session-only presentation metadata: the server clock ended this turn.
        /// False for core/replay events; never changes rules or the canonical state hash.</summary>
        public readonly bool TimedOut;

        public GameEvent(GameEventKind kind, ulong sequence, InstanceId? source, InstanceId? target,
                         int? amount, EventFlags flags, PresentationCue? cueOverride = null, bool timedOut = false)
        {
            Kind = kind;
            Sequence = sequence;
            Source = source;
            Target = target;
            Amount = amount;
            Flags = flags;
            CueOverride = cueOverride;
            TimedOut = timedOut;
        }

        public override string ToString()
        {
            string src = Source.HasValue ? Source.Value.ToString() : "null";
            string tgt = Target.HasValue ? Target.Value.ToString() : "null";
            string amt = Amount.HasValue ? Amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
            string cue = CueOverride.HasValue ? CueOverride.Value.ToString() : "default";
            return $"{{ seq={Sequence} kind={Kind} cue={cue} src={src} tgt={tgt} amt={amt} flags={Flags} }}";
        }
    }

    /// <summary>Presentation reads ordered batches off this delegate.</summary>
    public delegate void GameEventStream(IReadOnlyList<GameEvent> batch);
}
