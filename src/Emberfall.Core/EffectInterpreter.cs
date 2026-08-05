using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// The runtime tree-walking interpreter over <see cref="RuleDefinition"/> data (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §3 — chosen
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

        /// <summary>Create one carrierless observer for each future-trigger rule on a resolved spell.</summary>
        public int CreateOngoingEffects(CardDefinition spell, PlayerId controller)
        {
            if (spell.Type != CardType.SPELL) return 0;
            int created = 0;
            for (int i = 0; i < spell.Rules.Count; i++)
            {
                RuleDefinition rule = spell.Rules[i];
                Trigger trigger = RuleSemantics.TriggerOf(rule);
                if (!EffectSourceCapabilities.Supports(EffectSourceKind.OngoingEffect, trigger))
                    continue;

                InstanceId id = _state.NextInstanceId();
                OngoingEffectInstance? instance = rule.Lifetime switch
                {
                    OneShotLifetime => OngoingEffectInstance.CreateOneShot(id, controller,
                        spell.Id, rule.Id.Value),
                    FiniteLifetime finite when finite.Uses > 0 =>
                        OngoingEffectInstance.CreateFinite(id, controller, spell.Id,
                            rule.Id.Value, finite.Uses),
                    PermanentLifetime => OngoingEffectInstance.CreatePermanent(id, controller,
                        spell.Id, rule.Id.Value),
                    _ => null,
                };
                if (instance == null) continue; // invalid authored combination fails closed
                _state.OngoingEffects.Add(instance);
                _buf.Emit(GameEventKind.OngoingEffectCreated, source: instance.Id);
                created++;
            }
            return created;
        }

        // ── Raising triggers ─────────────────────────────────────────────────────

        /// <summary>
        /// Enqueue a card's OWN effects matching <paramref name="trigger"/>. Player-authored choices are
        /// already validated; automatic and random target specs resolve later against authoritative state.
        /// </summary>
        public void RaiseSelf(CardInstance source, Trigger trigger, IReadOnlyList<TargetRef> chosenTargets,
            TriggerEventContext context = default)
        {
            // Do not form new work from a silenced permanent. Already queued work deliberately reaches
            // Drain unchanged: the subscription snapshot was taken before silence and must resolve.
            if (source.IsSilenced) return;
            CardDefinition definition = _db.Get(source.DefinitionId);
            if (!EffectSourceCapabilities.TryForCardType(definition.Type,
                    out EffectSourceKind sourceKind))
                return;
            RaiseSelf(source.Id, source.Controller, definition, sourceKind, source, trigger,
                chosenTargets, context);
        }

        /// <summary>Enqueue a carrierless source such as a hero power or future ongoing effect.</summary>
        public void RaiseSelf(InstanceId identity, PlayerId controller, CardDefinition definition,
            EffectSourceKind sourceKind, CardInstance? currentCarrier, Trigger trigger,
            IReadOnlyList<TargetRef> chosenTargets, TriggerEventContext context = default)
        {
            if (!EffectSourceCapabilities.Supports(sourceKind, trigger)) return;
            IReadOnlyList<RuleDefinition> rules = definition.Rules;
            var bindings = new TargetBindingContext();
            context = context.WithTriggerSource(identity);
            for (int i = 0; i < rules.Count; i++)
                if (RuleSemantics.ActivatesOn(rules[i], trigger))
                {
                    var source = new EffectSourceContext(identity, controller, definition, rules[i],
                        sourceKind, currentCarrier);
                    _queue.Enqueue(new QueuedEffect(source, chosenTargets, bindings, context));
                }
        }

        /// <summary>
        /// Enqueue every persistent source subscribed to <paramref name="trigger"/> in the shared
        /// deterministic board-minion, equipped-weapon, future-ongoing order.
        /// Conditions qualify effects while this snapshot is built, before queue/cap accounting.
        /// </summary>
        public void RaiseObservers(Trigger trigger, CardInstance? excluded,
            TriggerEventContext context = default,
            InstanceId? triggerSource = null)
        {
            PlayerId firstPlayer = context.IncomingTurnPlayer ?? _state.ActivePlayer;
            TriggerEventContext eventContext = triggerSource.HasValue
                ? context.WithTriggerSource(triggerSource.Value)
                : context;
            EffectSourceContext? previous = null;
            var bindings = new TargetBindingContext();
            foreach (EffectSourceContext source in OrderedTriggerSources.Snapshot(
                         _state, _db, firstPlayer, trigger))
            {
                if (excluded != null && source.Identity == excluded.Id) continue;
                if (!ConditionEvaluator.Holds(source, context)) continue;
                if (previous == null || previous.Identity != source.Identity ||
                    previous.Kind != source.Kind)
                    bindings = new TargetBindingContext();
                _queue.Enqueue(new QueuedEffect(source, System.Array.Empty<TargetRef>(), bindings,
                    eventContext));
                previous = source;
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
                if (!ConditionEvaluator.Holds(qe.Source, qe.TriggerEvent))
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
            var targets = ResolveTargets(qe);
            if (qe.Source.Kind == EffectSourceKind.OngoingEffect)
                _buf.Emit(GameEventKind.OngoingEffectTriggered, source: qe.Source.Identity);
            ActionHandlers.Execute(new EffectContext(this, qe.Source, targets));
            if (qe.Source.Kind == EffectSourceKind.OngoingEffect)
                ConsumeOngoingEffect(qe.Source);
        }

        private void ConsumeOngoingEffect(EffectSourceContext source)
        {
            int index = -1;
            for (int i = 0; i < _state.OngoingEffects.Count; i++)
                if (_state.OngoingEffects[i].Id == source.Identity &&
                    _state.OngoingEffects[i].RuleId == source.RuleReference.RuleId.Value)
                {
                    index = i;
                    break;
                }
            if (index < 0) return;

            OngoingEffectInstance instance = _state.OngoingEffects[index];
            if (instance.Lifetime == OngoingEffectLifetime.PERMANENT) return;
            instance.RemainingUses = instance.RemainingUses.GetValueOrDefault() - 1;
            if (!instance.IsExpired) return;
            _state.OngoingEffects.RemoveAt(index);
            _buf.Emit(GameEventKind.OngoingEffectExpired, source: instance.Id);
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
                    AuraEngine.Recompute(_state, _db);
                    _buf.Emit(GameEventKind.CardDestroyed, source: minion.Id);

                    var def = _db.Get(minion.DefinitionId);
                    RaiseSelf(minion, Trigger.ON_DEATH, System.Array.Empty<TargetRef>(),
                        TriggerEventContext.ForSource(minion.Id));

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
            if (!RulesEngine.EnterBoard(_state, copy, def, seat, atIndex, _db)) return;
            copy.CurrentHealth = 1;            // Rekindle returns at 1 health
            copy.HasUsedRekindle = true;         // never rekindles again
            _buf.Emit(GameEventKind.CardSummoned, source: copy.Id, flags: EventFlags.IsToken);
        }

        // ── Target resolution ────────────────────────────────────────────────────

        private IReadOnlyList<TargetCandidate> ResolveTargets(QueuedEffect qe)
        {
            return Targeting.ResolveTargets(_state, _db, qe.Source, qe.ChosenTargets,
                qe.Bindings, qe.TriggerEvent.TriggerSource);
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
        public readonly EffectSourceContext Source;
        public readonly IReadOnlyList<TargetRef> ChosenTargets;
        public readonly TargetBindingContext Bindings;
        public readonly TriggerEventContext TriggerEvent;

        public QueuedEffect(EffectSourceContext source, IReadOnlyList<TargetRef> chosenTargets,
            TargetBindingContext bindings, TriggerEventContext triggerEvent = default)
        {
            Source = source;
            ChosenTargets = chosenTargets ?? System.Array.Empty<TargetRef>();
            Bindings = bindings;
            TriggerEvent = triggerEvent;
        }
    }

    /// <summary>Pure condition gate evaluated immediately before a queued effect resolves.</summary>
    internal static class ConditionEvaluator
    {
        public static bool Holds(EffectSourceContext source, TriggerEventContext context)
        {
            switch (source.Rule.Condition)
            {
                case AlwaysCondition _:
                    return RuleSemantics.TriggerOf(source.Rule) != Trigger.ON_TURN_START;
                case TurnOwnerCondition condition:
                    if (!context.IncomingTurnPlayer.HasValue) return false;
                    bool controllerTurn = source.Controller == context.IncomingTurnPlayer.Value;
                    switch (condition.Owner)
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
    /// <see cref="State"/> through the shared pipelines, emits semantic events via <see cref="Events"/>, and may
    /// enqueue follow-on triggers via the back-reference to the interpreter.
    /// </summary>
    internal readonly struct EffectContext
    {
        public readonly EffectInterpreter Interpreter;
        public readonly EffectSourceContext Source;
        public readonly IReadOnlyList<TargetCandidate> Targets;

        public EffectContext(EffectInterpreter interpreter, EffectSourceContext source,
            IReadOnlyList<TargetCandidate> targets)
        {
            Interpreter = interpreter;
            Source = source;
            Targets = targets;
        }

        public RuleDefinition Rule => Source.Rule;
        public GameState State => Interpreter.State;
        public ICardDatabase Db => Interpreter.Db;
        public EventBuffer Events => Interpreter.Events;

    }

    /// <summary>One reusable engine primitive for a closed typed <see cref="RuleAction"/> node.</summary>
    internal interface IActionHandler
    {
        void Execute(in EffectContext ctx);
    }
}
