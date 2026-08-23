using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
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
                        if (!AuraCapabilities.Supports(definition.Type, rule)) continue;
                        Apply(state, db, source, (ModifyStatsAction)rule.Action);
                    }
                }
            }
        }

        private static void Apply(GameState state, ICardDatabase db, CardInstance source,
            ModifyStatsAction action)
        {
            foreach (CardInstance target in Targets(state, db, source, action))
            {
                target.Enchantments.Add(new Enchantment
                {
                    Source = source.Id,
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
        private static IEnumerable<CardInstance> Targets(GameState state, ICardDatabase db,
            CardInstance source, ModifyStatsAction action)
        {
            foreach (TargetCandidate candidate in Targeting.ResolutionCandidates(
                         state, db, source.Controller, action.Target, action, source.Id))
                if (candidate.Card != null)
                    yield return candidate.Card;
        }
    }
}
