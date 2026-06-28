using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// Accumulates the ordered D7 event stream for one command, assigning each event a global,
    /// monotonic <see cref="GameEvent.Sequence"/> from the match-wide cursor on the state
    /// (game-state-and-actions.md §5.2). The sequence is deterministic and is part of what the
    /// golden-replay test asserts.
    /// </summary>
    internal sealed class EventBuffer : IGameEventSink
    {
        private readonly GameState _state;
        private readonly List<GameEvent> _events = new List<GameEvent>();

        public EventBuffer(GameState state) => _state = state;

        public IReadOnlyList<GameEvent> Events => _events;

        public void Emit(in GameEvent e) => _events.Add(e);

        public void Emit(VfxEvent kind, InstanceId? source = null, InstanceId? target = null,
                         int? amount = null, EventFlags flags = EventFlags.None)
        {
            _events.Add(new GameEvent(kind, _state.NextEventSequence++, source, target, amount, flags));
        }
    }
}
