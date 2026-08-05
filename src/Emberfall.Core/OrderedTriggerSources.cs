using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// The sole persistent trigger-source walk. It snapshots authored rules in deterministic order:
    /// incoming board left-to-right, weapon, ongoing by identity, then the other side likewise.
    /// Ordinary card zones are intentionally absent.
    /// </summary>
    public static class OrderedTriggerSources
    {
        public static IReadOnlyList<EffectSourceContext> Snapshot(GameState state,
            ICardDatabase database, PlayerId incomingPlayer, Trigger trigger)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (database == null) throw new ArgumentNullException(nameof(database));

            int incomingSeat = state.SeatOf(incomingPlayer);
            int otherSeat = incomingSeat == 0 ? 1 : 0;
            var result = new List<EffectSourceContext>();
            AppendSide(result, state, state.Players[incomingSeat], database, trigger);
            AppendSide(result, state, state.Players[otherSeat], database, trigger);
            return result;
        }

        private static void AppendSide(List<EffectSourceContext> result, GameState state,
            PlayerState player, ICardDatabase database, Trigger trigger)
        {
            for (int i = 0; i < player.Board.Count; i++)
            {
                CardInstance carrier = player.Board[i];
                if (carrier.Zone != Zone.Board || carrier.Controller != player.Id ||
                    carrier.IsSilenced)
                    continue;
                AppendCarrierRules(result, carrier, player.Id, database,
                    EffectSourceKind.BoardMinion, CardType.MINION, trigger);
            }

            CardInstance? weapon = player.Weapon;
            if (weapon != null && weapon.Zone == Zone.Weapon && weapon.Controller == player.Id &&
                !weapon.IsSilenced)
            {
                AppendCarrierRules(result, weapon, player.Id, database,
                    EffectSourceKind.EquippedWeapon, CardType.WEAPON, trigger);
            }

            if (!EffectSourceCapabilities.Supports(EffectSourceKind.OngoingEffect, trigger))
                return;

            var eligible = new List<OngoingEffectInstance>();
            for (int i = 0; i < state.OngoingEffects.Count; i++)
            {
                OngoingEffectInstance source = state.OngoingEffects[i];
                if (source.Controller == player.Id && !source.IsExpired)
                    eligible.Add(source);
            }
            eligible.Sort((left, right) => left.Id.CompareTo(right.Id));
            for (int i = 0; i < eligible.Count; i++)
            {
                OngoingEffectInstance instance = eligible[i];
                if (!database.TryGet(instance.SourceDefinitionId, out CardDefinition definition) ||
                    definition.Type != CardType.SPELL || string.IsNullOrEmpty(instance.RuleId))
                    continue;

                RuleDefinition? rule = null;
                for (int r = 0; r < definition.Rules.Count; r++)
                    if (definition.Rules[r].Id.Value == instance.RuleId)
                    {
                        rule = definition.Rules[r];
                        break;
                    }
                if (rule == null || !RuleSemantics.ActivatesOn(rule, trigger))
                    continue;

                result.Add(new EffectSourceContext(instance.Id, instance.Controller, definition,
                    rule, EffectSourceKind.OngoingEffect, currentCarrier: null));
            }
        }

        private static void AppendCarrierRules(List<EffectSourceContext> result,
            CardInstance carrier, PlayerId controller, ICardDatabase database,
            EffectSourceKind sourceKind, CardType expectedCardType, Trigger trigger)
        {
            if (!EffectSourceCapabilities.Supports(sourceKind, trigger) ||
                !database.TryGet(carrier.DefinitionId, out CardDefinition definition) ||
                definition.Type != expectedCardType)
                return;

            IReadOnlyList<RuleDefinition> rules = definition.Rules;
            for (int i = 0; i < rules.Count; i++)
            {
                RuleDefinition rule = rules[i];
                if (RuleSemantics.ActivatesOn(rule, trigger))
                {
                    result.Add(new EffectSourceContext(carrier.Id, controller, definition, rule,
                        sourceKind, carrier));
                }
            }
        }
    }
}
