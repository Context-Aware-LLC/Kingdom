using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Core
{
    /// <summary>
    /// The sole persistent trigger-source walk. It snapshots authored rules in deterministic order:
    /// incoming board left-to-right, weapon, ongoing by identity, then the other side likewise.
    /// Ordinary card zones are intentionally absent.
    ///
    /// <para><b>Which side or sides it walks is <see cref="IsControllerScoped"/>'s answer, not this
    /// walk's.</b> Both sides was the sweep's only behaviour until 2026-09-04 (#272), when
    /// ON_FRIENDLY_SUMMON was ruled controller-scoped and needed the second side dropped.</para>
    /// </summary>
    public static class OrderedTriggerSources
    {
        /// <summary>
        /// Whether an observer sweep sees ONLY <c>incomingPlayer</c>'s permanents, or both sides.
        ///
        /// <para><b>Scope is a per-trigger RULING, not a property of the sweep</b> (#272, Sam,
        /// 2026-09-04). ON_FRIENDLY_SUMMON was both-sided for no reason other than inheriting this
        /// walk: the enum name, both shipped card texts and the schema doc all said friendly-only,
        /// and the implementation was the one thing that disagreed — by inheritance rather than by
        /// decision. <c>ON_TURN_END</c> (EF-012) and <c>ON_SPELL_CAST</c> (EF-016) are both-sided
        /// BY decision and stay that way: their names carry no qualifier and their card grammar
        /// says "a spell is cast", not "you cast".</para>
        ///
        /// <para><b>Every Trigger member is tabled by name and the default arm THROWS.</b> A member
        /// added without an arm here fails the first time it is swept, which is the whole point —
        /// scope must not be inherited silently a second time. Non-observer triggers are tabled as
        /// <c>false</c> rather than left to the default, because <see cref="Snapshot"/> is legally
        /// reachable with one (it answers with the empty list, via EffectSourceCapabilities) and a
        /// throw there would be a new failure mode rather than a caught mistake.</para>
        /// </summary>
        public static bool IsControllerScoped(Trigger trigger) => trigger switch
        {
            // The one controller-scoped trigger. "Friendly" is enforced HERE and nowhere else —
            // `excluded` removes only the summoned card itself and never spoke about sides.
            Trigger.ON_FRIENDLY_SUMMON => true,

            // Both sides by decision; each carries its liveness note in Enums.cs.
            Trigger.ON_TURN_START => false,
            Trigger.ON_TURN_END => false,
            Trigger.ON_SPELL_CAST => false,

            // Never swept — raised through RaiseSelf, or not raised at all. Tabled, not omitted.
            Trigger.ON_PLAY => false,
            Trigger.ON_DEATH => false,
            Trigger.ON_ATTACK => false,
            Trigger.ON_DAMAGED => false,
            Trigger.ON_HEAL => false,
            Trigger.ON_CAST => false,
            Trigger.AURA => false,

            _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger,
                "A trigger reaching the observer sweep must declare its scope: one side or both. " +
                "Add an arm to OrderedTriggerSources.IsControllerScoped — inheriting the walk is " +
                "what #272 was."),
        };

        public static IReadOnlyList<EffectSourceContext> Snapshot(GameState state,
            ICardDatabase database, PlayerId incomingPlayer, Trigger trigger)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (database == null) throw new ArgumentNullException(nameof(database));

            int incomingSeat = state.SeatOf(incomingPlayer);
            int otherSeat = incomingSeat == 0 ? 1 : 0;
            var result = new List<EffectSourceContext>();
            AppendSide(result, state, state.Players[incomingSeat], database, trigger);
            if (IsControllerScoped(trigger)) return result;
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
