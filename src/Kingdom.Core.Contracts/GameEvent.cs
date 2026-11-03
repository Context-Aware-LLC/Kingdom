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
        /// <summary>Server-only observation: the target's Health the instant before a
        /// <see cref="GameEventKind.DamageApplied"/> that landed on a minion or hero (armor not
        /// included). Null on every other event, an Aegis-absorbed hit included. It is what the King's
        /// Overkill commission is scored from, because Health replayed through the event stream misses
        /// a stat change made earlier in the same command. Same contract as <see cref="TimedOut"/>:
        /// never read by rules, never hashed, and dropped by the projector and the codec, so it never
        /// reaches the wire.</summary>
        public readonly int? HealthBefore;

        public GameEvent(GameEventKind kind, ulong sequence, InstanceId? source, InstanceId? target,
                         int? amount, EventFlags flags, PresentationCue? cueOverride = null, bool timedOut = false,
                         int? healthBefore = null)
        {
            Kind = kind;
            Sequence = sequence;
            Source = source;
            Target = target;
            Amount = amount;
            Flags = flags;
            CueOverride = cueOverride;
            TimedOut = timedOut;
            HealthBefore = healthBefore;
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
