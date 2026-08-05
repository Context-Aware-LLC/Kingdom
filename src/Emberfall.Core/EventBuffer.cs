using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;

namespace Emberfall.Core
{
    /// <summary>
    /// Accumulates the ordered D7 event stream for one command, assigning each event a global,
    /// monotonic <see cref="GameEvent.Sequence"/> from the match-wide cursor on the state
    /// (dev-docs/architecture/determinism-and-projection.md:5). The sequence is deterministic and is part of what the
    /// golden-replay test asserts.
    ///
    /// Also captures the per-event <see cref="EventVisibility"/> sidecar AT EMIT TIME
    /// (dev-docs/architecture/determinism-and-projection.md:24): post-command state cannot classify a broken weapon (nulled)
    /// or the ephemeral hero-power source (never zoned), so the verdict is taken while it is
    /// still true. The sidecar is server-side only — never serialized, never hashed.
    /// </summary>
    internal sealed class EventBuffer
    {
        private readonly GameState _state;
        private readonly List<GameEvent> _events = new List<GameEvent>();
        private readonly List<EventVisibility> _visibility = new List<EventVisibility>();
        private HashSet<int>? _publicMarks;

        public EventBuffer(GameState state) => _state = state;

        public IReadOnlyList<GameEvent> Events => _events;

        /// <summary>Emit-time visibility verdicts, index-parallel to <see cref="Events"/>.</summary>
        public IReadOnlyList<EventVisibility> Visibility => _visibility;

        /// <summary>
        /// Declare an id public for this command even though it resolves to no zone — the
        /// ephemeral hero-power source (its act is public) is the one M2 case. Default-deny
        /// stands for everything unmarked.
        /// </summary>
        public void MarkPublic(InstanceId id) => (_publicMarks ??= new HashSet<int>()).Add(id.Value);

        /// <summary>
        /// Semantic event seam. Validation happens before sequence allocation so malformed events
        /// cannot leave a gap in the deterministic stream.
        /// </summary>
        public void Emit(GameEventKind kind, InstanceId? source = null, InstanceId? target = null,
                         int? amount = null, EventFlags flags = EventFlags.None,
                         PresentationCue? cueOverride = null)
        {
            GameEventPolicy.Validate(kind, amount, flags, cueOverride);

            var e = new GameEvent(kind, _state.NextEventSequence, source, target, amount, flags, cueOverride);
            var visibility = new EventVisibility(Classify(source), Classify(target));
            _state.NextEventSequence++;
            _events.Add(e);
            _visibility.Add(visibility);
        }

        private IdVisibility Classify(InstanceId? idOpt)
        {
            if (!idOpt.HasValue) return IdVisibility.Public;   // null carries nothing; projector passes it through
            if (_publicMarks != null && _publicMarks.Contains(idOpt.Value.Value)) return IdVisibility.Public;
            return GameStateProjector.Classify(_state, idOpt.Value);
        }
    }
}
