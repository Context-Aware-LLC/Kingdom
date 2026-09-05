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
    /// RNG use. Every target policy is explicit: an action that can take a target always carries a
    /// <see cref="TargetSpec"/> with a selector, enforced at load by <c>CardJson.TargetFromElement</c>
    /// and at construction by <c>TargetedRuleAction</c>. There is no selector-less effect and no
    /// verb-relative default to fall back to.</para>
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
            int created = 0;
            bool createdAura = false;
            for (int i = 0; i < spell.Rules.Count; i++)
            {
                RuleDefinition rule = spell.Rules[i];
                Trigger trigger = RuleSemantics.TriggerOf(rule);
                if (!RuleLifetimeCapabilities.TryCreateOngoingEffect(spell.Type, trigger,
                        rule.Lifetime, out OngoingEffectLifetime lifetime,
                        out int? remainingUses))
                    continue;

                InstanceId id = _state.NextInstanceId();
                OngoingEffectInstance instance = OngoingEffectInstance.Create(id, controller,
                    spell.Id, rule.Id.Value, lifetime, remainingUses);
                _state.OngoingEffects.Add(instance);
                _buf.Emit(GameEventKind.OngoingEffectCreated, source: instance.Id);
                created++;
                createdAura |= trigger == Trigger.AURA;
            }

            // An aura begins folding the moment it exists, and NOTHING ELSE ON THIS PATH WILL FOLD
            // IT. Every other way a source appears goes on to move the board — a summon, a death —
            // and the aura fold rides along on that. A spell that only creates an ongoing effect
            // changes no zone and kills nobody, so it reaches no existing recompute site, and the
            // aura would sit inert until something unrelated happened to disturb the board.
            //
            // The failure this prevents is asymmetric and would have been very hard to read: a
            // minion summoned AFTER the spell gets buffed (its own summon recomputes), while the
            // minions that were already standing there do not. Half a working card.
            if (createdAura) AuraEngine.Recompute(_state, _db);
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
        ///
        /// <para><paramref name="observerController"/> is REQUIRED for a trigger
        /// <see cref="OrderedTriggerSources.IsControllerScoped"/> answers true for, and meaningless
        /// otherwise. It names the side the sweep is confined to — for ON_FRIENDLY_SUMMON, whoever
        /// summoned. It is a separate parameter from <paramref name="excluded"/> and NOT derived
        /// from it, because a summon is not always the active player's: a toll firing on the
        /// opponent's turn summons a token, and <c>_state.ActivePlayer</c> would name the wrong
        /// board. Omitting it throws rather than defaulting, so a future raise site cannot inherit
        /// a scope it never chose — the mistake #272 existed to record.</para>
        /// </summary>
        public void RaiseObservers(Trigger trigger, CardInstance? excluded,
            TriggerEventContext context = default,
            InstanceId? triggerSource = null,
            PlayerId? observerController = null)
        {
            bool controllerScoped = OrderedTriggerSources.IsControllerScoped(trigger);
            if (controllerScoped && observerController == null)
                throw new System.InvalidOperationException(
                    $"{trigger} is controller-scoped: its raise site must name the controller whose " +
                    "permanents observe it. See OrderedTriggerSources.IsControllerScoped.");

            PlayerId firstPlayer = controllerScoped
                ? observerController!.Value
                : context.IncomingTurnPlayer ?? _state.ActivePlayer;

            // ONE context, built once, used at BOTH gates (docs/effect-model-scheduling.md §5).
            // Two gates exist on purpose — §1's "reconsider, not act" rule needs the dequeue-time
            // re-check — but they must read the SAME environment. This previously qualified each
            // subscriber against `context` and enqueued `eventContext`, so a condition reading the
            // trigger source could pass one gate and fail the other.
            //
            // Reassigning the parameter rather than introducing a second name is the fix: it takes
            // the un-augmented context out of scope entirely, so the two gates below cannot drift
            // apart again without someone deliberately reconstructing it.
            context = triggerSource.HasValue
                ? context.WithTriggerSource(triggerSource.Value)
                : context;

            EffectSourceContext? previous = null;
            var bindings = new TargetBindingContext();
            foreach (EffectSourceContext source in OrderedTriggerSources.Snapshot(
                         _state, _db, firstPlayer, trigger))
            {
                if (excluded != null && source.Identity == excluded.Id) continue;
                if (!ConditionEvaluator.Holds(_state, _db, source, context, bindings)) continue;
                if (previous == null || previous.Identity != source.Identity ||
                    previous.Kind != source.Kind)
                    bindings = new TargetBindingContext();
                _queue.Enqueue(new QueuedEffect(source, System.Array.Empty<TargetRef>(), bindings,
                    context));
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
                if (!ConditionEvaluator.Holds(_state, _db, qe.Source, qe.TriggerEvent, qe.Bindings))
                    continue; // defense in depth for self-triggers or invalid data; not a resolution

                if (++_state.ActionResolutionsThisTurn > MatchEngine.ActionResolutionCap) break;
                ResolveOne(qe);

                DeathCheck();
                RulesEngine.CheckWin(_state, _buf);
                if (_state.Outcome != MatchOutcome.InProgress)
                {
                    _queue.Clear();
                    return;   // checkpoint already ran on this path
                }
            }

            // The checkpoint sequence is a property of the DRAIN, not of the caller
            // (docs/effect-model-scheduling.md §3.2) — it runs on EVERY exit, never as something six
            // call sites each remember. This covers the two exits the loop body could not: an empty
            // queue at the loop top, and a `break` on the resolution cap.
            //
            // Only the game-end half is repeated here, and that is deliberate rather than partial:
            // deaths are already settled on both of those paths — the empty-queue exit is preceded by
            // the entry DeathCheck, and the cap `break` fires BEFORE ResolveOne, so nothing has died
            // since the previous iteration's DeathCheck. Adding a redundant board scan would be a
            // second full pass over a hash-canonical engine for no state change.
            RulesEngine.CheckWin(_state, _buf);
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
            bool expired = RuleLifetimeCapabilities.ConsumeOngoingEffect(instance.Lifetime,
                instance.RemainingUses, out int? remainingUses);
            instance.RemainingUses = remainingUses;
            if (!expired) return;
            _buf.Emit(GameEventKind.OngoingEffectExpired, source: instance.Id);
            _state.OngoingEffects.RemoveAt(index);
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

    /// <summary>
    /// Pure condition gate evaluated immediately before a queued effect resolves.
    ///
    /// <para><b>Still pure, and neither the bindings nor the state argument changes that</b> — it
    /// reads what the play has already recorded and writes nothing. A cost is paid by the cost RULE
    /// resolving, one dequeue earlier; this only asks whether it did. CONTROLS reads the board the
    /// same way, at both gates, so a condition that stopped holding between enqueue and dequeue
    /// drops its effect rather than resolving on a board that no longer qualifies.</para>
    /// </summary>
    internal static class ConditionEvaluator
    {
        public static bool Holds(GameState state, ICardDatabase db, EffectSourceContext source,
            TriggerEventContext context, TargetBindingContext? bindings = null)
        {
            switch (source.Rule.Condition)
            {
                case CostPaidCondition _:
                    // Fail closed with no binding context: a gate whose cost cannot be observed has
                    // not seen a cost paid. The observer snapshot is the caller that has none, and
                    // C28 keeps costs off observer triggers precisely so that is unreachable.
                    return bindings != null && bindings.CostPaid;
                case AlwaysCondition _:
                    return RuleSemantics.TriggerOf(source.Rule) != Trigger.ON_TURN_START;
                case ControlsCondition controls:
                    return ControlsMinionOf(state, db, source, controls);
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

        /// <summary>
        /// Whether the rule's controller has a board minion of the named tribe.
        ///
        /// <para><b>The source is excluded by ID, not by position.</b> A minion is on the board
        /// before its own ON_PLAY resolves, so a Construct asking about Constructs would otherwise
        /// answer itself; and a Toll fires from a minion that has already left, where the exclusion
        /// costs nothing and an index-based one would have nothing to point at. What the flag says
        /// is what the card FACE says — "another Construct" — which is why CardValidator C29 pins
        /// the two together rather than letting the runtime decide.</para>
        /// </summary>
        private static bool ControlsMinionOf(GameState state, ICardDatabase db,
            EffectSourceContext source, ControlsCondition controls)
        {
            if (state == null || db == null) return false;   // fail closed, as COST_PAID does
            List<CardInstance> board = state.PlayerOf(source.Controller).Board;
            for (int i = 0; i < board.Count; i++)
            {
                CardInstance minion = board[i];
                if (controls.ExcludesSource && minion.Id == source.Identity) continue;
                if (db.TryGet(minion.DefinitionId, out CardDefinition def) &&
                    def.Tribe == controls.Tribe)
                    return true;
            }
            return false;
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
