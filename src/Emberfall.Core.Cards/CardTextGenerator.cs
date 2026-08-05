using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// Deterministically renders the normalized rules that a card already owns into player-facing
    /// text. This is deliberately one-way: authored <see cref="CardDefinition.CardText"/> remains
    /// untouched until the content and presentation migrations elect to consume generated text.
    /// </summary>
    public static class CardTextGenerator
    {
        public static string Generate(IReadOnlyList<RuleDefinition> rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            return string.Join(" ", rules.Select(RenderRule));
        }

        private static string RenderRule(RuleDefinition rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            return TriggerMarker(rule.Activation) + ConditionPrefix(rule.Condition) +
                LifetimePrefix(rule.Lifetime) + RenderAction(rule.Action);
        }

        private static string TriggerMarker(RuleActivation activation)
        {
            if (activation is AuraActivation) return "[Aura] ";
            if (!(activation is TriggeredActivation triggered))
                throw Unsupported("activation", activation);

            switch (triggered.Trigger)
            {
                case Trigger.ON_PLAY: return "[Herald] ";
                case Trigger.ON_DEATH: return "[Toll] ";
                case Trigger.ON_ATTACK: return "[Attack] ";
                case Trigger.ON_CAST: return "";
                default:
                    throw new NotSupportedException(
                    $"Card text generation does not support trigger '{triggered.Trigger}'.");
            }
        }

        private static string ConditionPrefix(RuleCondition condition)
        {
            if (condition is AlwaysCondition) return "";
            if (condition is TurnOwnerCondition turnOwner)
            {
                switch (turnOwner.Owner)
                {
                    case TurnOwner.CONTROLLER: return "While it is your turn, ";
                    case TurnOwner.OPPONENT: return "While it is your opponent's turn, ";
                    default:
                        throw new NotSupportedException(
                        $"Card text generation does not support turn owner '{turnOwner.Owner}'.");
                }
            }
            throw Unsupported("condition", condition);
        }

        private static string LifetimePrefix(RuleLifetime lifetime)
        {
            if (lifetime is SourceBoundLifetime || lifetime is OneShotLifetime) return "";
            if (lifetime is FiniteLifetime finite)
            {
                if (finite.Uses <= 0)
                    throw new NotSupportedException("Card text generation requires a positive finite lifetime.");
                return finite.Uses == 1 ? "For its next use, " : $"For its next {finite.Uses} uses, ";
            }
            if (lifetime is PermanentLifetime) return "Permanently, ";
            throw Unsupported("lifetime", lifetime);
        }

        private static string RenderAction(RuleAction action)
        {
            switch (action)
            {
                case DamageAction a: return $"Deal {a.Amount} damage to {Target(a.Target)}.";
                case HealAction a: return $"Restore {a.Amount} Health to {Target(a.Target)}.";
                case ModifyStatsAction a: return $"Give {Target(a.Target)} {Delta(a.AttackDelta)}/{Delta(a.HealthDelta)}.";
                case ReinforceAction a: return $"[Reinforce] {Target(a.Target)}.";
                case GrantKeywordAction a: return $"Give {Target(a.Target)} {Name(a.Keyword)}.";
                case DestroyAction a: return $"Destroy {Target(a.Target)}.";
                case ReturnToHandAction a: return $"Return {Target(a.Target)} to hand.";
                case SilenceAction a: return $"Silence {Target(a.Target)}.";
                case TransformAction a: return $"Transform {Target(a.Target)} into {Name(a.CardId)}.";
                case FrostAction a: return $"Frost {Target(a.Target)}.";
                case TakeControlAction a: return $"Take control of {Target(a.Target)}.";
                case DrawAction a: return $"Draw {Cards(a.Count)}.";
                case SummonAction a: return $"Summon {Copies(a.Count, Name(a.CardId))}.";
                case AddToHandAction a: return $"Add {Copies(a.Count, Name(a.CardId))} to your hand.";
                case EquipAction a: return $"Equip {Name(a.CardId)}.";
                case GainManaAction a: return Mana(a);
                case BackdraftAction a: return $"[Backdraft] ({a.Amount}).";
                case GainArmorAction a: return $"Gain {a.Amount} Armor.";
                case UnearthAction a: return $"Unearth from {Cards(a.Count)}.";
                default: throw Unsupported("action", action);
            }
        }

        private static string Target(TargetSpec target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target.Count.HasValue && target.Count != 1 && target.Count != -1)
                throw new NotSupportedException(
                    $"Card text generation does not support target count '{target.Count}'.");

            bool random = false;
            if (target.ChosenBy.HasValue)
            {
                if (target.ChosenBy == TargetChosenBy.RANDOM) random = true;
                else if (target.ChosenBy != TargetChosenBy.PLAYER)
                    throw new NotSupportedException(
                        $"Card text generation does not support target choice '{target.ChosenBy}'.");
            }

            string description;
            switch (target.Selector)
            {
                case "ALL_MINIONS": description = "all minions"; break;
                case "ANY_CHARACTER": description = "a character"; break;
                case "ANY_ENEMY": description = "an enemy character"; break;
                case "ANY_FRIENDLY": description = "a friendly character"; break;
                case "ANY_FRIENDLY_MINION": description = "a friendly minion"; break;
                case "ANY_MINION": description = "a minion"; break;
                case "ENEMY_HERO": description = "the enemy hero"; break;
                case "ENEMY_MINIONS": description = "all enemy minions"; break;
                case "FRIENDLY_HERO": description = "your hero"; break;
                case "FRIENDLY_MINIONS": description = "all friendly minions"; break;
                default:
                    throw new NotSupportedException(
                    $"Card text generation does not support target selector '{target.Selector}'.");
            }

            if (target.Filter != null) description = Filter(description, target.Filter);
            if (random) description = Randomize(description);
            return description;
        }

        private static string Filter(string description, TargetFilterDef filter)
        {
            string prefix = "";
            if (filter.Damaged.HasValue) prefix += filter.Damaged.Value ? "damaged " : "undamaged ";
            if (filter.Tribe.HasValue) prefix += Name(filter.Tribe.Value.ToString()) + " ";
            if (prefix.Length == 0) return description;

            if (description.StartsWith("a ", StringComparison.Ordinal))
                return "a " + prefix + description.Substring(2);
            if (description.StartsWith("an ", StringComparison.Ordinal))
                return "a " + prefix + description.Substring(3);
            if (description.StartsWith("all ", StringComparison.Ordinal))
                return "all " + prefix + description.Substring(4);
            return prefix + description;
        }

        private static string Randomize(string description)
        {
            if (description.StartsWith("a ", StringComparison.Ordinal))
                return "a random " + description.Substring(2);
            if (description.StartsWith("an ", StringComparison.Ordinal))
                return "a random " + description.Substring(3);
            if (description.StartsWith("the ", StringComparison.Ordinal))
                return "the random " + description.Substring(4);
            if (description.StartsWith("your ", StringComparison.Ordinal))
                return "your random " + description.Substring(5);
            return "random " + description;
        }

        private static string Mana(GainManaAction action)
        {
            string crystals = action.Amount == 1 ? "Mana Crystal" : "Mana Crystals";
            switch (action.Kind)
            {
                case ManaGainKind.TEMPORARY: return $"Gain {action.Amount} {crystals} this turn only.";
                case ManaGainKind.PERMANENT_FILLED: return $"Gain {action.Amount} {crystals}.";
                case ManaGainKind.PERMANENT_EMPTY: return $"Gain {action.Amount} empty {crystals}.";
                default:
                    throw new NotSupportedException(
                    $"Card text generation does not support mana kind '{action.Kind}'.");
            }
        }

        private static string Cards(int count)
        {
            if (count <= 0) throw new NotSupportedException("Card text generation requires a positive card count.");
            return count == 1 ? "a card" : $"{count} cards";
        }

        private static string Copies(int count, string cardName)
        {
            if (count <= 0) throw new NotSupportedException("Card text generation requires a positive copy count.");
            return count == 1 ? "a " + cardName : $"{count} copies of {cardName}";
        }

        private static string Delta(int amount) => amount >= 0 ? "+" + amount : amount.ToString(CultureInfo.InvariantCulture);

        private static string Name(Keyword keyword) => Name(keyword.ToString());

        private static string Name(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                throw new NotSupportedException("Card text generation requires a non-empty identifier.");

            string[] parts = identifier.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            int start = parts.Length > 0 && (parts[0] == "token" || parts[0] == "hero") ? 1 : 0;
            var builder = new StringBuilder();
            for (int i = start; i < parts.Length; i++)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1) builder.Append(parts[i].Substring(1).ToLowerInvariant());
            }
            if (builder.Length == 0)
                throw new NotSupportedException("Card text generation requires a readable identifier.");
            return builder.ToString();
        }

        private static NotSupportedException Unsupported(string node, object value)
            => new NotSupportedException(
                $"Card text generation does not support {node} node '{value.GetType().Name}'.");
    }
}
