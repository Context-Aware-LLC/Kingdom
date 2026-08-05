using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// The runtime tree-walking interpreter over <see cref="EffectDef"/> data (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §3 — chosen
    /// over codegen so balance is a data patch, not a rebuild). One instance is created per command; it owns
    /// the deterministic effect queue and drains it to a fixed point, running the three post-mutation checks
    /// (DeathCheck / CapCheck / WinCheck) after every step. Ordering is part of the determinism contract and
    /// is exercised by the golden-replay gate (dev-docs/quality/testing-and-ci.md:24) — no Dictionary/HashSet iteration ever
    /// drives order here; only sorted lists and ascending <see cref="InstanceId"/> tie-breaks.
    ///
    /// <para><b>Targeting.</b> Every authored <see cref="TargetSpec"/> is resolved through
    /// <see cref="Targeting"/>, which owns candidate order, filters, bindings, multiplicity and canonical
    /// RNG use. Selector-less legacy effects retain their verb-relative defaults until the typed-rule
    /// migration makes every target policy explicit.</para>
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
        /// Enqueue a card's OWN effects matching <paramref name="trigger"/>. Player-authored choices are
        /// already validated; automatic and random target specs resolve later against authoritative state.
        /// </summary>
        public void RaiseSelf(CardInstance source, Trigger trigger, IReadOnlyList<TargetRef> chosenTargets,
            TriggerContext context = default)
        {
            var def = _db.Get(source.DefinitionId);
            var effects = def.Effects;
            var bindings = new TargetBindingContext();
            context = context.WithTriggerSource(source.Id);
            for (int i = 0; i < effects.Count; i++)
                if (effects[i].Trigger == trigger)
                    _queue.Enqueue(new QueuedEffect(effects[i], source, chosenTargets, bindings, context));
        }

        /// <summary>
        /// Enqueue every board-minion trigger source's effects subscribed to <paramref name="trigger"/>
        /// (observer triggers such as ON_FRIENDLY_SUMMON), in canonical order: active player's side first,
        /// then board left→right by position. This is the single source-enumeration seam; weapons and
        /// ongoing effects extend it only when their runtime lifecycle and validation land together.
        /// Conditions qualify effects while this snapshot is built, before queue/cap accounting.
        /// </summary>
        public void RaiseObservers(Trigger trigger, CardInstance? excluded, TriggerContext context = default,
            InstanceId? triggerSource = null)
        {
            foreach (var src in OrderedTriggerSources())
            {
                if (excluded != null && ReferenceEquals(src, excluded)) continue;
                var def = _db.Get(src.DefinitionId);
                var effects = def.Effects;
                var bindings = new TargetBindingContext();
                TriggerContext sourceContext = triggerSource.HasValue
                    ? context.WithTriggerSource(triggerSource.Value)
                    : context;
                for (int i = 0; i < effects.Count; i++)
                    if (effects[i].Trigger == trigger &&
                        ConditionEvaluator.Holds(effects[i], src, context))
                        _queue.Enqueue(new QueuedEffect(effects[i], src, System.Array.Empty<TargetRef>(),
                            bindings, sourceContext));
            }
        }

        // ── Draining to a fixed point ────────────────────────────────────────────

        /// <summary>
        /// Settle the board: resolve any pending deaths from the primary mutation, then drain queued effects
        /// breadth-first — each effect followed by a fresh DeathCheck + WinCheck so chained tolls
        /// cascade deterministically. Bounded by <see cref="MatchEngine.ActionResolutionCap"/>.
        /// </summary>
        public void Drain()
        {
            DeathCheck();
            while (_queue.Count > 0)
            {
                var qe = _queue.Dequeue();
                if (!ConditionEvaluator.Holds(qe.Effect, qe.Source, qe.TriggerContext))
                    continue; // defense in depth for self-triggers or invalid data; not a resolution

                if (++_state.ActionResolutionsThisTurn > MatchEngine.ActionResolutionCap) break;
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
                return; // unimplemented verb (e.g. UNEARTH pending its choice flow) — graceful no-op

            var targets = ResolveTargets(qe);
            handler.Execute(new EffectContext(this, qe.Effect, qe.Source, targets));
        }

        // ── Death resolution (the toll cascade engine, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §3.3) ─

        /// <summary>
        /// Resolve all minions currently at ≤0 health as a single batch in deterministic death order, firing
        /// each one's ON_DEATH (toll) onto the queue and resummoning REKINDLE copies. Repeats until no
        /// fresh corpses remain in this settle; queued tolls themselves drain back in <see cref="Drain"/>.
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
                    _buf.Emit(GameEventKind.CardDestroyed, source: minion.Id);

                    var def = _db.Get(minion.DefinitionId);
                    var effects = def.Effects;
                    var bindings = new TargetBindingContext();
                    for (int i = 0; i < effects.Count; i++)
                        if (effects[i].Trigger == Trigger.ON_DEATH)
                            _queue.Enqueue(new QueuedEffect(effects[i], minion, System.Array.Empty<TargetRef>(),
                                bindings, TriggerContext.ForSource(minion.Id)));

                    // REKINDLE: the first death resummons a 1-health copy at the same slot (dev-docs/specs/card-schema.md §4).
                    if (minion.Keywords.Contains(Keyword.REKINDLE) && !minion.HasUsedRekindle)
                    {
                        minion.HasUsedRekindle = true; // mark original consumed; copy gets its own flag in Resummon
                        Resummon(seat, def, idx);
                    }
                }
                // loop: REKINDLE copies / chained kills may have produced fresh corpses to settle
            }
        }

        private void Resummon(int seat, CardDefinition def, int atIndex)
        {
            var ps = _state.Players[seat];
            if (ps.Board.Count >= MatchEngine.BoardCap) return;

            var copy = new CardInstance(_state.NextInstanceId(), def.Id, ps.Id);
            if (!RulesEngine.EnterBoard(_state, copy, def, seat, atIndex)) return;
            copy.CurrentHealth = 1;            // Rekindle returns at 1 health
            copy.HasUsedRekindle = true;         // never rekindles again
            _buf.Emit(GameEventKind.CardSummoned, source: copy.Id, flags: EventFlags.IsToken);
        }

        // ── Target resolution ────────────────────────────────────────────────────

        private IReadOnlyList<TargetCandidate> ResolveTargets(QueuedEffect qe)
        {
            return Targeting.ResolveTargets(_state, _db, qe.Source, qe.Effect, qe.ChosenTargets,
                qe.Bindings, qe.TriggerContext.TriggerSource);
        }

        // ── Deterministic trigger-source ordering ────────────────────────────────

        /// <summary>Board-minion sources only: active player's side first, each board left→right.</summary>
        private IEnumerable<CardInstance> OrderedTriggerSources()
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
        public readonly IReadOnlyList<TargetRef> ChosenTargets;
        public readonly TargetBindingContext Bindings;
        public readonly TriggerContext TriggerContext;

        public QueuedEffect(EffectDef effect, CardInstance source, IReadOnlyList<TargetRef> chosenTargets,
            TargetBindingContext bindings, TriggerContext triggerContext = default)
        {
            Effect = effect;
            Source = source;
            ChosenTargets = chosenTargets ?? System.Array.Empty<TargetRef>();
            Bindings = bindings;
            TriggerContext = triggerContext;
        }
    }

    /// <summary>Event-specific values conditions can inspect without conflating them with the source.</summary>
    internal readonly struct TriggerContext
    {
        public readonly PlayerId? TurnPlayer;
        public readonly InstanceId? TriggerSource;

        private TriggerContext(PlayerId? turnPlayer, InstanceId? triggerSource)
        {
            TurnPlayer = turnPlayer;
            TriggerSource = triggerSource;
        }

        public static TriggerContext ForTurnStart(PlayerId turnPlayer) => new TriggerContext(turnPlayer, null);
        public static TriggerContext ForSource(InstanceId source) => new TriggerContext(null, source);
        public TriggerContext WithTriggerSource(InstanceId source) => new TriggerContext(TurnPlayer, source);
    }

    /// <summary>Pure condition gate evaluated immediately before a queued effect resolves.</summary>
    internal static class ConditionEvaluator
    {
        public static bool Holds(EffectDef effect, CardInstance source, TriggerContext context)
        {
            var condition = effect.Condition;
            if (condition == null)
                return effect.Trigger != Trigger.ON_TURN_START; // invalid card data fails closed at runtime too

            switch (condition.Type)
            {
                case ConditionType.TURN_OWNER:
                    if (!condition.TurnOwner.HasValue || !context.TurnPlayer.HasValue)
                        return false;

                    bool controllerTurn = source.Controller == context.TurnPlayer.Value;
                    switch (condition.TurnOwner.Value)
                    {
                        case TurnOwner.CONTROLLER: return controllerTurn;
                        case TurnOwner.OPPONENT: return !controllerTurn;
                        case TurnOwner.ANY: return true;
                        default: return false;
                    }
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Everything a single primitive needs to apply itself (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §3.1). The handler mutates
    /// <see cref="State"/> through the shared pipelines, emits D7 events via <see cref="Events"/>, and may
    /// enqueue follow-on triggers via the back-reference to the interpreter.
    /// </summary>
    internal readonly struct EffectContext
    {
        public readonly EffectInterpreter Interpreter;
        public readonly EffectDef Effect;
        public readonly CardInstance Source;
        public readonly IReadOnlyList<TargetCandidate> Targets;

        public EffectContext(EffectInterpreter interpreter, EffectDef effect, CardInstance source,
            IReadOnlyList<TargetCandidate> targets)
        {
            Interpreter = interpreter;
            Effect = effect;
            Source = source;
            Targets = targets;
        }

        public GameState State => Interpreter.State;
        public ICardDatabase Db => Interpreter.Db;
        public EventBuffer Events => Interpreter.Events;

        /// <summary>The effect's VFX override if present, else the action's default event (dev-docs/specs/card-schema.md §5.2).</summary>
    }

    /// <summary>One reusable engine primitive — a verb in the closed <see cref="Contracts.Action"/> set.</summary>
    internal interface IActionHandler
    {
        Contracts.Action Verb { get; }
        void Execute(in EffectContext ctx);
    }
}
