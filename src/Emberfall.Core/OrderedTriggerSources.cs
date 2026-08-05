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
            ICardDatabase database, PlayerId incomingPlayer, Trigger trigger,
            IReadOnlyList<EffectSourceContext>? ongoingEffects = null)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (database == null) throw new ArgumentNullException(nameof(database));

            int incomingSeat = state.SeatOf(incomingPlayer);
            int otherSeat = incomingSeat == 0 ? 1 : 0;
            var result = new List<EffectSourceContext>();
            AppendSide(result, state.Players[incomingSeat], database, trigger, ongoingEffects);
            AppendSide(result, state.Players[otherSeat], database, trigger, ongoingEffects);
            return result;
        }

        private static void AppendSide(List<EffectSourceContext> result, PlayerState player,
            ICardDatabase database, Trigger trigger,
            IReadOnlyList<EffectSourceContext>? ongoingEffects)
        {
            for (int i = 0; i < player.Board.Count; i++)
            {
                CardInstance carrier = player.Board[i];
                if (carrier.Zone != Zone.Board || carrier.Controller != player.Id) continue;
                AppendCarrierRules(result, carrier, player.Id, database,
                    EffectSourceKind.BoardMinion, CardType.MINION, trigger);
            }

            CardInstance? weapon = player.Weapon;
            if (weapon != null && weapon.Zone == Zone.Weapon && weapon.Controller == player.Id)
            {
                AppendCarrierRules(result, weapon, player.Id, database,
                    EffectSourceKind.EquippedWeapon, CardType.WEAPON, trigger);
            }

            if (ongoingEffects == null ||
                !EffectSourceCapabilities.Supports(EffectSourceKind.OngoingEffect, trigger))
                return;

            var eligible = new List<EffectSourceContext>();
            for (int i = 0; i < ongoingEffects.Count; i++)
            {
                EffectSourceContext source = ongoingEffects[i];
                if (source != null &&
                    source.Controller == player.Id &&
                    source.Kind == EffectSourceKind.OngoingEffect &&
                    RuleSemantics.ActivatesOn(source.Rule, trigger))
                    eligible.Add(source);
            }
            eligible.Sort((left, right) => left.Identity.CompareTo(right.Identity));
            result.AddRange(eligible);
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
