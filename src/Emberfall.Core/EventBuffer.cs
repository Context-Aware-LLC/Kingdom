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
    internal sealed class EventBuffer : IGameEventSink
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

        public void Emit(in GameEvent e)
        {
            _events.Add(e);
            _visibility.Add(new EventVisibility(Classify(e.Source), Classify(e.Target)));
        }

        public void Emit(VfxEvent kind, InstanceId? source = null, InstanceId? target = null,
                         int? amount = null, EventFlags flags = EventFlags.None)
        {
            Emit(new GameEvent(kind, _state.NextEventSequence++, source, target, amount, flags));
        }

        /// <summary>
        /// Semantic event seam. Validation happens before sequence allocation so malformed events
        /// cannot leave a gap in the deterministic stream.
        /// </summary>
        public void Emit(GameEventKind kind, InstanceId? source = null, InstanceId? target = null,
                         int? amount = null, EventFlags flags = EventFlags.None,
                         PresentationCue? cueOverride = null)
        {
            GameEventPolicy.Validate(kind, amount, flags, cueOverride);

            PresentationCue cue = cueOverride ?? PresentationCueDefaults.For(kind);
            Emit(new GameEvent(ToLegacyKind(cue), _state.NextEventSequence++, source, target, amount, flags));
        }

        private static VfxEvent ToLegacyKind(PresentationCue cue)
        {
            return cue switch
            {
                PresentationCue.Play => VfxEvent.onPlay,
                PresentationCue.Summon => VfxEvent.onSummon,
                PresentationCue.Attack => VfxEvent.onAttack,
                PresentationCue.Damage => VfxEvent.onDamage,
                PresentationCue.Heal => VfxEvent.onHeal,
                PresentationCue.Death => VfxEvent.onDeath,
                PresentationCue.Buff => VfxEvent.onBuff,
                PresentationCue.Draw => VfxEvent.onDraw,
                PresentationCue.Silence => VfxEvent.onSilence,
                PresentationCue.Frost => VfxEvent.onFreeze,
                PresentationCue.Transform => VfxEvent.onTransform,
                PresentationCue.Return => VfxEvent.onReturn,
                PresentationCue.ManaGain => VfxEvent.onManaGain,
                PresentationCue.Equip => VfxEvent.onEquip,
                PresentationCue.Unearth => VfxEvent.onDiscover,
                PresentationCue.TurnStart => VfxEvent.onTurnStart,
                PresentationCue.TurnEnd => VfxEvent.onTurnEnd,
                PresentationCue.HeroPower => VfxEvent.onHeroPower,
                PresentationCue.Fatigue => VfxEvent.onFatigue,
                PresentationCue.GameWin => VfxEvent.onGameWin,
                PresentationCue.GameOver => VfxEvent.onGameOver,
                _ => throw new System.ArgumentOutOfRangeException(nameof(cue), cue, "Unknown presentation cue."),
            };
        }

        private IdVisibility Classify(InstanceId? idOpt)
        {
            if (!idOpt.HasValue) return IdVisibility.Public;   // null carries nothing; projector passes it through
            if (_publicMarks != null && _publicMarks.Contains(idOpt.Value.Value)) return IdVisibility.Public;
            return GameStateProjector.Classify(_state, idOpt.Value);
        }
    }
}
