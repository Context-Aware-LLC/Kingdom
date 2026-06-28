using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// One outward D7 event (game-state-and-actions.md §5.2). Purely observational — it never
    /// feeds back into the core, so different animation speeds across clients cannot desync state.
    /// The full ordered stream is part of what the golden-replay test asserts (golden-replay.md §1).
    /// </summary>
    public readonly struct GameEvent
    {
        /// <summary>What to animate (closed D7 vocabulary).</summary>
        public readonly VfxEvent Kind;
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

        public GameEvent(VfxEvent kind, ulong sequence, InstanceId? source, InstanceId? target, int? amount, EventFlags flags)
        {
            Kind = kind;
            Sequence = sequence;
            Source = source;
            Target = target;
            Amount = amount;
            Flags = flags;
        }

        public override string ToString()
        {
            string src = Source.HasValue ? Source.Value.ToString() : "null";
            string tgt = Target.HasValue ? Target.Value.ToString() : "null";
            string amt = Amount.HasValue ? Amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
            return $"{{ seq={Sequence} kind={Kind} src={src} tgt={tgt} amt={amt} flags={Flags} }}";
        }
    }

    /// <summary>The core writes events here during Apply.</summary>
    public interface IGameEventSink
    {
        void Emit(in GameEvent e);
    }

    /// <summary>Presentation reads ordered batches off this delegate.</summary>
    public delegate void GameEventStream(IReadOnlyList<GameEvent> batch);
}
