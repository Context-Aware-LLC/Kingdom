using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// The runtime tree-walking interpreter over <see cref="EffectDef"/> data (card-system.md §3 — chosen
    /// over codegen so balance is a data patch, not a rebuild). One instance is created per command; it owns
    /// the deterministic effect queue and drains it to a fixed point, running the three post-mutation checks
    /// (DeathCheck / CapCheck / WinCheck) after every step. Ordering is part of the determinism contract and
    /// is exercised by the golden-replay gate (golden-replay.md §1) — no Dictionary/HashSet iteration ever
    /// drives order here; only sorted lists and ascending <see cref="InstanceId"/> tie-breaks.
    ///
    /// <para><b>M1 targeting model.</b> The committed <see cref="EffectDef"/> carries no <c>TargetSpec</c>
    /// (design's card-schema.md §5.3 is not yet in the data model). So targets resolve two ways: a
    /// player-initiated trigger (ON_PLAY / ON_CAST) uses the validated targets from the intent; a fired
    /// trigger (ON_DEATH / observers) has no player, so it falls back to a verb-relative default
    /// (DEAL_DAMAGE→enemy hero, HEAL→friendly hero, BUFF/GIVE_KEYWORD→self). Selector-based AoE awaits a
    /// TargetSpec primitive via the <c>requiresEngineeringPrimitive</c> gate (card-system.md §7).</para>
    /// </summary>
    internal sealed class EffectInterpreter
    {
        private static readonly IReadOnlyDictionary<Contracts.Action, IActionHandler> Handlers = ActionHandlers.Registry;

        private readonly GameState _state;
        private readonly ICardDatabase _db;
        private readonly EventBuffer _buf;
        private readonly Queue<QueuedEffect> _queue = new Queue<QueuedEffect>();

        public EffectInterpreter(GameState state, ICardDatabase db, EventBuffer buf)
        {
            _state = state;
            _db = db;
            _buf = buf;
        }

        public GameState State => _state;
        public ICardDatabase Db => _db;
        public EventBuffer Events => _buf;

        // ── Raising triggers ─────────────────────────────────────────────────────

        /// <summary>
        /// Enqueue a card's OWN effects matching <paramref name="trigger"/> (ON_PLAY battlecry, ON_CAST spell,
        /// ON_DEATH deathrattle). <paramref name="chosenTargets"/> are the player-resolved targets (empty for
        /// fired triggers, which fall back to verb defaults at resolution time).
        /// </summary>
        public void RaiseSelf(CardInstance source, Trigger trigger, IReadOnlyList<CharacterRef> chosenTargets)
        {
            var def = _db.Get(source.DefinitionId);
            var effects = def.Effects;
            for (int i = 0; i < effects.Count; i++)
                if (effects[i].Trigger == trigger)
                    _queue.Enqueue(new QueuedEffect(effects[i], source, chosenTargets));
        }

        /// <summary>
        /// Enqueue every OTHER permanent's effects subscribed to <paramref name="trigger"/> (observer triggers
        /// such as ON_FRIENDLY_SUMMON), in the canonical order: active player's side first, board left→right by
        /// position, then hero; ties by ascending instance id (game-state-and-actions.md §5.1).
        /// </summary>
        public void RaiseObservers(Trigger trigger, CardInstance? excluded)
        {
            foreach (var src in OrderedPermanents())
            {
                if (excluded != null && ReferenceEquals(src, excluded)) continue;
                var def = _db.Get(src.DefinitionId);
                var effects = def.Effects;
                for (int i = 0; i < effects.Count; i++)
                    if (effects[i].Trigger == trigger)
                        _queue.Enqueue(new QueuedEffect(effects[i], src, System.Array.Empty<CharacterRef>()));
            }
        }

        // ── Draining to a fixed point ────────────────────────────────────────────

        /// <summary>
        /// Settle the board: resolve any pending deaths from the primary mutation, then drain queued effects
        /// breadth-first — each effect followed by a fresh DeathCheck + WinCheck so chained deathrattles
        /// cascade deterministically. Bounded by <see cref="MatchEngine.ActionResolutionCap"/>.
        /// </summary>
        public void Drain()
        {
            DeathCheck();
            while (_queue.Count > 0)
            {
                if (++_state.ActionResolutionsThisTurn > MatchEngine.ActionResolutionCap) break;

                var qe = _queue.Dequeue();
                ResolveOne(qe);

                DeathCheck();
                RulesEngine.CheckWin(_state, _buf);
                if (_state.Outcome != MatchOutcome.InProgress)
                {
                    _queue.Clear();
                    return;
                }
            }
        }

        private void ResolveOne(QueuedEffect qe)
        {
            if (!Handlers.TryGetValue(qe.Effect.Action, out var handler))
                return; // unimplemented verb (e.g. DISCOVER pending its choice flow) — graceful no-op

            var targets = ResolveTargets(qe);
            handler.Execute(new EffectContext(this, qe.Effect, qe.Source, targets));
        }

        // ── Death resolution (the deathrattle cascade engine, card-system.md §3.3) ─

        /// <summary>
        /// Resolve all minions currently at ≤0 health as a single batch in deterministic death order, firing
        /// each one's ON_DEATH (deathrattle) onto the queue and resummoning REBORN copies. Repeats until no
        /// fresh corpses remain in this settle; queued deathrattles themselves drain back in <see cref="Drain"/>.
        /// </summary>
        private void DeathCheck()
        {
            int guard = 0;
            while (++guard <= MatchEngine.ActionResolutionCap)
            {
                List<CharacterRef>? dead = null;
                for (int seat = 0; seat < 2; seat++)
                {
                    var board = _state.Players[seat].Board;
                    for (int i = 0; i < board.Count; i++)
                        if (board[i].CurrentHealth <= 0)
                            (dead ??= new List<CharacterRef>()).Add(CharacterRef.OfMinion(seat, board[i]));
                }
                if (dead == null) return;

                dead.Sort(DeathOrder);

                foreach (var d in dead)
                {
                    int seat = d.Seat;
                    var ps = _state.Players[seat];
                    var minion = d.Minion!;
                    int idx = ps.Board.IndexOf(minion);
                    if (idx < 0) continue; // already removed earlier this pass

                    ps.Board.RemoveAt(idx);
                    RulesEngine.Reindex(ps.Board);
                    minion.Zone = Zone.Graveyard;
                    ps.Graveyard.Add(minion);
                    _buf.Emit(VfxEvent.onDeath, source: minion.Id);

                    var def = _db.Get(minion.DefinitionId);
                    var effects = def.Effects;
                    for (int i = 0; i < effects.Count; i++)
                        if (effects[i].Trigger == Trigger.ON_DEATH)
                            _queue.Enqueue(new QueuedEffect(effects[i], minion, System.Array.Empty<CharacterRef>()));

                    // REBORN: the first death resummons a 1-health copy at the same slot (card-schema.md §4).
                    if (minion.Keywords.Contains(Keyword.REBORN) && !minion.HasUsedReborn)
                    {
                        minion.HasUsedReborn = true; // mark original consumed; copy gets its own flag in Resummon
                        Resummon(seat, def, idx);
                    }
                }
                // loop: REBORN copies / chained kills may have produced fresh corpses to settle
            }
        }

        private void Resummon(int seat, CardDefinition def, int atIndex)
        {
            var ps = _state.Players[seat];
            if (ps.Board.Count >= MatchEngine.BoardCap) return;

            var copy = new CardInstance(_state.NextInstanceId(), def.Id, ps.Id);
            if (!RulesEngine.EnterBoard(_state, copy, def, seat, atIndex)) return;
            copy.CurrentHealth = 1;            // Reborn returns at 1 health
            copy.HasUsedReborn = true;         // never reborns again
            _buf.Emit(VfxEvent.onSummon, source: copy.Id, flags: EventFlags.IsToken);
        }

        // ── Target resolution ────────────────────────────────────────────────────

        private IReadOnlyList<CharacterRef> ResolveTargets(QueuedEffect qe)
        {
            if (qe.ChosenTargets.Count > 0) return qe.ChosenTargets;
            if (qe.Effect.Selector != null) return SelectorTargets(qe.Effect, qe.Source);
            return DefaultTargets(qe.Effect, qe.Source);
        }

        /// <summary>
        /// Auto-resolve targets from the effect's declared selector (card-schema.md §5.3). Activated only
        /// when no player-chosen targets are present — covers AoE sweeps (ENEMY_MINIONS), unconditional
        /// auto-targets (ENEMY_HERO), and SELF. Explicit-choice (ANY_*) selectors are never auto-resolved:
        /// CommandProcessor validation rejects a player intent that omits their target, so reaching here
        /// with one means a fired trigger authored an ANY_* selector — a data bug, and it throws.
        /// Unimplemented selectors (ADJACENT, TRIGGER_SOURCE) and unknown strings also throw: the M2
        /// Phase 0 grammar-freeze ruling — silent fallback was silent mistargeting.
        /// </summary>
        private IReadOnlyList<CharacterRef> SelectorTargets(EffectDef eff, CardInstance source)
        {
            int seat = _state.SeatOf(source.Controller);
            int foe = seat == 0 ? 1 : 0;
            switch (eff.Selector)
            {
                case "SELF":
                    return source.Zone == Zone.Board
                        ? new[] { CharacterRef.OfMinion(seat, source) }
                        : (IReadOnlyList<CharacterRef>)System.Array.Empty<CharacterRef>();
                case "ENEMY_MINIONS":
                    {
                        var board = _state.Players[foe].Board;
                        var refs = new CharacterRef[board.Count];
                        for (int i = 0; i < board.Count; i++) refs[i] = CharacterRef.OfMinion(foe, board[i]);
                        return refs;
                    }
                case "FRIENDLY_MINIONS":
                    {
                        var board = _state.Players[seat].Board;
                        var refs = new CharacterRef[board.Count];
                        for (int i = 0; i < board.Count; i++) refs[i] = CharacterRef.OfMinion(seat, board[i]);
                        return refs;
                    }
                case "ALL_MINIONS":
                    {
                        var list = new List<CharacterRef>();
                        var b0 = _state.Players[seat].Board;
                        var b1 = _state.Players[foe].Board;
                        for (int i = 0; i < b0.Count; i++) list.Add(CharacterRef.OfMinion(seat, b0[i]));
                        for (int i = 0; i < b1.Count; i++) list.Add(CharacterRef.OfMinion(foe, b1[i]));
                        return list;
                    }
                case "ENEMY_HERO":
                    return new[] { CharacterRef.OfHero(foe, _state.Players[foe].Hero) };
                case "FRIENDLY_HERO":
                    return new[] { CharacterRef.OfHero(seat, _state.Players[seat].Hero) };
                default:
                    throw new System.InvalidOperationException(
                        $"Card '{source.DefinitionId}': effect {eff.Action} declares selector '{eff.Selector}' " +
                        "which the interpreter cannot auto-resolve (explicit-choice ANY_* selectors require " +
                        "player-chosen targets; ADJACENT/TRIGGER_SOURCE are unimplemented). Fix the card row " +
                        "or gate it behind requiresEngineeringPrimitive — silent fallback is mistargeting.");
            }
        }

        private IReadOnlyList<CharacterRef> DefaultTargets(EffectDef eff, CardInstance source)
        {
            int seat = _state.SeatOf(source.Controller);
            int foe = seat == 0 ? 1 : 0;
            switch (eff.Action)
            {
                case Contracts.Action.DEAL_DAMAGE:
                    return new[] { CharacterRef.OfHero(foe, _state.Players[foe].Hero) };
                case Contracts.Action.HEAL:
                    return new[] { CharacterRef.OfHero(seat, _state.Players[seat].Hero) };
                case Contracts.Action.BUFF:
                case Contracts.Action.GIVE_KEYWORD:
                    return source.Zone == Zone.Board
                        ? new[] { CharacterRef.OfMinion(seat, source) }
                        : System.Array.Empty<CharacterRef>();
                default:
                    // DESTROY / RETURN_TO_HAND / SILENCE / TRANSFORM need an explicit target; none → no-op.
                    return System.Array.Empty<CharacterRef>();
            }
        }

        // ── Deterministic permanent ordering ─────────────────────────────────────

        /// <summary>Active player's permanents first; within a side board left→right then hero (game-state §5.1).</summary>
        private IEnumerable<CardInstance> OrderedPermanents()
        {
            int active = _state.SeatOf(_state.ActivePlayer);
            int other = active == 0 ? 1 : 0;
            foreach (var m in _state.Players[active].Board) yield return m;
            foreach (var m in _state.Players[other].Board) yield return m;
        }

        private int DeathOrder(CharacterRef a, CharacterRef b)
        {
            int active = _state.SeatOf(_state.ActivePlayer);
            // active player's dead resolve first
            int sa = a.Seat == active ? 0 : 1;
            int sb = b.Seat == active ? 0 : 1;
            if (sa != sb) return sa - sb;
            // then board position (left→right), then ascending instance id
            int pa = a.Minion!.Position, pb = b.Minion!.Position;
            if (pa != pb) return pa - pb;
            return a.Id.CompareTo(b.Id);
        }
    }

    /// <summary>One effect waiting in the resolution queue, bound to the source that fired it and its targets.</summary>
    internal readonly struct QueuedEffect
    {
        public readonly EffectDef Effect;
        public readonly CardInstance Source;
        public readonly IReadOnlyList<CharacterRef> ChosenTargets;

        public QueuedEffect(EffectDef effect, CardInstance source, IReadOnlyList<CharacterRef> chosenTargets)
        {
            Effect = effect;
            Source = source;
            ChosenTargets = chosenTargets ?? System.Array.Empty<CharacterRef>();
        }
    }

    /// <summary>
    /// Everything a single primitive needs to apply itself (card-system.md §3.1). The handler mutates
    /// <see cref="State"/> through the shared pipelines, emits D7 events via <see cref="Events"/>, and may
    /// enqueue follow-on triggers via the back-reference to the interpreter.
    /// </summary>
    internal readonly struct EffectContext
    {
        public readonly EffectInterpreter Interpreter;
        public readonly EffectDef Effect;
        public readonly CardInstance Source;
        public readonly IReadOnlyList<CharacterRef> Targets;

        public EffectContext(EffectInterpreter interpreter, EffectDef effect, CardInstance source,
            IReadOnlyList<CharacterRef> targets)
        {
            Interpreter = interpreter;
            Effect = effect;
            Source = source;
            Targets = targets;
        }

        public GameState State => Interpreter.State;
        public ICardDatabase Db => Interpreter.Db;
        public EventBuffer Events => Interpreter.Events;

        /// <summary>The effect's VFX override if present, else the action's default event (card-schema.md §5.2).</summary>
        public VfxEvent Vfx(VfxEvent fallback) => Effect.VfxOverride ?? fallback;
    }

    /// <summary>One reusable engine primitive — a verb in the closed <see cref="Contracts.Action"/> set.</summary>
    internal interface IActionHandler
    {
        Contracts.Action Verb { get; }
        void Execute(in EffectContext ctx);
    }
}
