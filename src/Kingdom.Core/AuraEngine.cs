using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Core
{
    /// <summary>
    /// Continuous, deterministic AURA fold. It never consumes RNG, enqueues no rule, and owns the
    /// remove-then-reapply lifecycle for every board topology or aura-eligibility change.
    /// </summary>
    internal static class AuraEngine
    {
        public static void Recompute(GameState state, ICardDatabase db)
        {
            // First remove marked provenance from every possible aura target. Board order is the
            // canonical order and no ordinary enchantment is considered, even when sources match.
            for (int seat = 0; seat < 2; seat++)
                foreach (CardInstance target in state.Players[seat].Board)
                    DerivedStats.RemoveAuras(target, db.Get(target.DefinitionId));

            // Then reapply sources in fixed seat, board, authored-rule order.
            for (int seat = 0; seat < 2; seat++)
            {
                foreach (CardInstance source in state.Players[seat].Board)
                {
                    if (source.IsSilenced || source.Zone != Zone.Board) continue;
                    CardDefinition definition = db.Get(source.DefinitionId);
                    if (definition.Type != CardType.MINION) continue;
                    foreach (RuleDefinition rule in definition.Rules)
                    {
                        if (!(rule.Action is ModifyStatsAction) ||
                            !AuraCapabilities.Supports(definition.Type, rule)) continue;
                        Apply(state, db, source.Id, source.Controller, (ModifyStatsAction)rule.Action);
                    }
                }
            }

            // Then the carrierless spell auras, after every board minion so that a minion aura and a
            // spell aura landing on the same target stack in a fixed order. `OngoingEffects` is
            // append-ordered and its ids are monotonic, which is the same stable tie-breaker the
            // trigger walk uses — no separate sort is needed to make this replay-stable.
            //
            // Note what is NOT here: an expiry branch. A spell aura ends by being REMOVED from
            // OngoingEffects (CommandProcessor.ApplyEndTurn) and this fold simply stops finding it.
            // RemoveAuras above has already stripped its enchantments this pass, so the effect
            // reverses itself with no un-apply path anywhere.
            foreach (OngoingEffectInstance effect in state.OngoingEffects)
            {
                CardDefinition definition = db.Get(effect.SourceDefinitionId);
                foreach (RuleDefinition rule in definition.Rules)
                {
                    if (rule.Id.Value != effect.RuleId) continue;
                    if (!(rule.Action is ModifyStatsAction) ||
                        !AuraCapabilities.Supports(definition.Type, rule)) continue;
                    Apply(state, db, effect.Id, effect.Controller, (ModifyStatsAction)rule.Action);
                }
            }
        }

        private static void Apply(GameState state, ICardDatabase db, InstanceId sourceId,
            PlayerId controller, ModifyStatsAction action)
        {
            foreach (CardInstance target in Targets(state, db, sourceId, controller, action))
            {
                target.Enchantments.Add(new Enchantment
                {
                    Source = sourceId,
                    AttackDelta = action.AttackDelta,
                    HealthDelta = action.HealthDelta,
                    IsAura = true,
                });
                DerivedStats.Recompute(target, db.Get(target.DefinitionId));
            }
        }

        /// <summary>
        /// An aura's domain, resolved by <see cref="Targeting"/> — the same evaluator every other
        /// effect uses, under the stricter subset <see cref="AuraCapabilities"/> admits.
        ///
        /// <para>This method used to be a second copy of the resolver: five selector names, their
        /// own seat arithmetic, and its own hand-written adjacency. That is the duplication the
        /// effect-model doc names outright, and the two copies were never checked against each
        /// other — a name the gate accepted and this switch lacked fell through to an empty
        /// sequence, producing an aura that validated and affected nobody, silently.</para>
        ///
        /// <para>Passing the ACTION matters: the capability relation is what excludes the hero from
        /// a stat aura's domain, rather than this method excluding it by only ever having looked at
        /// board lists. Candidate order changes from seat-absolute to controller-first, which is
        /// unobservable here — each target receives its own enchantment and the order enchantments
        /// land WITHIN one target is fixed by the source loop in <see cref="Recompute"/>, not by
        /// this one.</para>
        /// </summary>
        /// <param name="sourceId">
        /// The aura's provenance — a board minion's instance id, or the id of the
        /// <see cref="OngoingEffectInstance"/> a spell left behind. Both are <see cref="InstanceId"/>
        /// and <see cref="Enchantment.Source"/> is already one, so a spell aura needs NO new field on
        /// any state type, and therefore moves neither the canonical hash nor the codec.
        /// </param>
        private static IEnumerable<CardInstance> Targets(GameState state, ICardDatabase db,
            InstanceId sourceId, PlayerId controller, ModifyStatsAction action)
        {
            foreach (TargetCandidate candidate in Targeting.ResolutionCandidates(
                         state, db, controller, action.Target, action, sourceId))
                if (candidate.Card != null)
                    yield return candidate.Card;
        }
    }
}
