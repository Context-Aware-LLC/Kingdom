using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>A stable, card-scoped identity for one authored rule.</summary>
    public readonly struct RuleId : IEquatable<RuleId>
    {
        public string Value { get; }

        public RuleId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A rule id cannot be empty.", nameof(value));
            Value = value;
        }

        public bool Equals(RuleId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is RuleId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
        public static bool operator ==(RuleId left, RuleId right) => left.Equals(right);
        public static bool operator !=(RuleId left, RuleId right) => !left.Equals(right);
    }

    /// <summary>
    /// One normalized rule. Activation, condition, lifetime and action are independent closed
    /// hierarchies, so an action never carries fields belonging to another action.
    /// </summary>
    public sealed class RuleDefinition
    {
        public RuleId Id { get; }
        public RuleActivation Activation { get; }
        public RuleCondition Condition { get; }
        public RuleLifetime Lifetime { get; }
        public RuleAction Action { get; }

        public RuleDefinition(RuleId id, RuleActivation activation, RuleCondition condition,
            RuleLifetime lifetime, RuleAction action)
        {
            Id = id;
            Activation = activation ?? throw new ArgumentNullException(nameof(activation));
            Condition = condition ?? throw new ArgumentNullException(nameof(condition));
            Lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            Action = action ?? throw new ArgumentNullException(nameof(action));
        }
    }

    public abstract class RuleActivation { private protected RuleActivation() { } }

    public sealed class TriggeredActivation : RuleActivation
    {
        public Trigger Trigger { get; }
        public TriggeredActivation(Trigger trigger) => Trigger = trigger;
    }

    public sealed class AuraActivation : RuleActivation
    {
        public static readonly AuraActivation Instance = new AuraActivation();
        private AuraActivation() { }
    }

    public abstract class RuleCondition { private protected RuleCondition() { } }

    public sealed class AlwaysCondition : RuleCondition
    {
        public static readonly AlwaysCondition Instance = new AlwaysCondition();
        private AlwaysCondition() { }
    }

    public sealed class TurnOwnerCondition : RuleCondition
    {
        public TurnOwner Owner { get; }
        public TurnOwnerCondition(TurnOwner owner) => Owner = owner;
    }

    public abstract class RuleLifetime { private protected RuleLifetime() { } }

    public sealed class SourceBoundLifetime : RuleLifetime
    {
        public static readonly SourceBoundLifetime Instance = new SourceBoundLifetime();
        private SourceBoundLifetime() { }
    }

    public sealed class OneShotLifetime : RuleLifetime
    {
        public static readonly OneShotLifetime Instance = new OneShotLifetime();
        private OneShotLifetime() { }
    }

    public sealed class FiniteLifetime : RuleLifetime
    {
        public int Uses { get; }
        public FiniteLifetime(int uses) => Uses = uses;
    }

    public sealed class PermanentLifetime : RuleLifetime
    {
        public static readonly PermanentLifetime Instance = new PermanentLifetime();
        private PermanentLifetime() { }
    }

    public abstract class RuleAction { private protected RuleAction() { } }

    public abstract class TargetedRuleAction : RuleAction
    {
        public TargetSpec Target { get; }

        private protected TargetedRuleAction(TargetSpec target)
            => Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public sealed class DamageAction : TargetedRuleAction
    {
        public int Amount { get; }
        public DamageAction(int amount, TargetSpec target) : base(target) => Amount = amount;
    }

    public sealed class HealAction : TargetedRuleAction
    {
        public int Amount { get; }
        public HealAction(int amount, TargetSpec target) : base(target) => Amount = amount;
    }

    public sealed class ModifyStatsAction : TargetedRuleAction
    {
        public int AttackDelta { get; }
        public int HealthDelta { get; }
        public ModifyStatsAction(int attackDelta, int healthDelta, TargetSpec target) : base(target)
        {
            AttackDelta = attackDelta;
            HealthDelta = healthDelta;
        }
    }

    public sealed class ReinforceAction : TargetedRuleAction
    {
        public ReinforceAction(TargetSpec target) : base(target) { }
    }

    public sealed class GrantKeywordAction : TargetedRuleAction
    {
        public Keyword Keyword { get; }
        public GrantKeywordAction(Keyword keyword, TargetSpec target) : base(target) => Keyword = keyword;
    }

    public sealed class DestroyAction : TargetedRuleAction
    {
        public DestroyAction(TargetSpec target) : base(target) { }
    }

    public sealed class ReturnToHandAction : TargetedRuleAction
    {
        public ReturnToHandAction(TargetSpec target) : base(target) { }
    }

    public sealed class SilenceAction : TargetedRuleAction
    {
        public SilenceAction(TargetSpec target) : base(target) { }
    }

    public sealed class TransformAction : TargetedRuleAction
    {
        public string CardId { get; }
        public TransformAction(string cardId, TargetSpec target) : base(target)
            => CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
    }

    public sealed class FrostAction : TargetedRuleAction
    {
        public FrostAction(TargetSpec target) : base(target) { }
    }

    /// <summary>Transfers a minion or equipped weapon to the rule controller.</summary>
    public sealed class TakeControlAction : TargetedRuleAction
    {
        public TakeControlAction(TargetSpec target) : base(target) { }
    }

    public sealed class DrawAction : RuleAction
    {
        public int Count { get; }
        public DrawAction(int count) => Count = count;
    }

    public sealed class SummonAction : RuleAction
    {
        public string CardId { get; }
        public int Count { get; }
        public SummonAction(string cardId, int count)
        {
            CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
            Count = count;
        }
    }

    public sealed class AddToHandAction : RuleAction
    {
        public string CardId { get; }
        public int Count { get; }
        public AddToHandAction(string cardId, int count)
        {
            CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
            Count = count;
        }
    }

    public sealed class EquipAction : RuleAction
    {
        public string CardId { get; }
        public EquipAction(string cardId)
            => CardId = cardId ?? throw new ArgumentNullException(nameof(cardId));
    }

    public enum ManaGainKind { TEMPORARY, PERMANENT_FILLED, PERMANENT_EMPTY }

    public sealed class GainManaAction : RuleAction
    {
        public int Amount { get; }
        public ManaGainKind Kind { get; }
        public GainManaAction(int amount, ManaGainKind kind)
        {
            Amount = amount;
            Kind = kind;
        }
    }

    public sealed class BackdraftAction : RuleAction
    {
        public int Amount { get; }
        public BackdraftAction(int amount) => Amount = amount;
    }

    public sealed class GainArmorAction : RuleAction
    {
        public int Amount { get; }
        public GainArmorAction(int amount) => Amount = amount;
    }

    public sealed class UnearthAction : RuleAction
    {
        public int Count { get; }
        public UnearthAction(int count) => Count = count;
    }

    /// <summary>Exhaustive semantics shared by validation, runtime targeting and presentation.</summary>
    public static class RuleSemantics
    {
        public static Trigger TriggerOf(RuleDefinition rule)
        {
            if (rule.Activation is TriggeredActivation triggered) return triggered.Trigger;
            if (rule.Activation is AuraActivation) return Trigger.AURA;
            throw new InvalidOperationException(
                $"Unknown activation node '{rule.Activation.GetType().Name}'.");
        }

        public static bool ActivatesOn(RuleDefinition rule, Trigger trigger)
            => TriggerOf(rule) == trigger;

        public static TargetSpec? TargetOf(RuleAction action)
            => action is TargetedRuleAction targeted ? targeted.Target : null;
    }

    public static class RuleActionCapabilities
    {
        public static bool ConsumesTargets(RuleAction action) => action is TargetedRuleAction;

        public static bool Allows(RuleAction action, TargetCandidateKind kind)
            => (AllowedKinds(action) & (TargetCandidateKinds)(1 << (int)kind)) != 0;

        public static bool HasCompatibleTarget(RuleAction action, string selector,
            CardType sourceType, Trigger trigger)
        {
            if (!ConsumesTargets(action)) return false;
            TargetCandidateKinds selected = selector == "SELF"
                ? SourceKinds(sourceType, trigger)
                : selector == "TRIGGER_SOURCE"
                    ? TriggerSourceKinds(sourceType, trigger)
                    : SelectorKinds(selector);
            return (AllowedKinds(action) & selected) != 0;
        }

        /// <summary>
        /// Actions that may only ever affect the controller's OWN side. This is the allegiance
        /// axis, deliberately separate from the <see cref="AllowedKinds"/> KIND axis: "Minion" and
        /// "a minion I control" are different questions, and the kind relation alone cannot answer
        /// the second — which is how <c>REINFORCE</c> on <c>ENEMY_MINIONS</c> stayed authorable and
        /// runnable (Sam, 2026-08-10: buffing the enemy board is never the intent).
        /// </summary>
        public static bool RequiresFriendlyTarget(RuleAction action) => action is ReinforceAction;

        /// <summary>
        /// Whether every candidate a selector can produce is on the controller's own side.
        /// <c>ADJACENT</c> qualifies because it walks the SOURCE minion's own board row, and
        /// <c>SELF</c> is the source itself. <c>TRIGGER_SOURCE</c> deliberately does NOT: on
        /// <c>ON_DAMAGED</c> the trigger source is whoever hit you.
        /// </summary>
        public static bool IsFriendlyOnlySelector(string selector)
        {
            switch (selector)
            {
                case "SELF":
                case "ADJACENT":
                case "FRIENDLY_HERO":
                case "FRIENDLY_MINIONS":
                case "OTHER_FRIENDLY_MINIONS":
                case "ANY_FRIENDLY":
                case "ANY_FRIENDLY_MINION":
                case "FRIENDLY_WEAPON":
                case "FRIENDLY_HAND":
                case "FRIENDLY_DECK":
                case "FRIENDLY_GRAVEYARD":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Whether <paramref name="selector"/> respects the action's allegiance rule.</summary>
        public static bool HasCompatibleAllegiance(RuleAction action, string selector)
            => !RequiresFriendlyTarget(action) || IsFriendlyOnlySelector(selector);

        public static TargetCandidateKinds AllowedKinds(RuleAction action)
        {
            switch (action)
            {
                case DamageAction _:
                case HealAction _:
                case FrostAction _:
                    return TargetCandidateKinds.Hero | TargetCandidateKinds.Minion;
                case ModifyStatsAction _:
                case ReinforceAction _:
                case GrantKeywordAction _:
                    return TargetCandidateKinds.Minion;
                case DestroyAction _:
                    return TargetCandidateKinds.Hero | TargetCandidateKinds.Minion |
                           TargetCandidateKinds.Weapon | TargetCandidateKinds.HandCard;
                case ReturnToHandAction _:
                    return TargetCandidateKinds.Minion | TargetCandidateKinds.Weapon |
                           TargetCandidateKinds.DeckCard | TargetCandidateKinds.GraveyardCard;
                case SilenceAction _:
                case TransformAction _:
                case TakeControlAction _:
                    return TargetCandidateKinds.Minion | TargetCandidateKinds.Weapon;
                case DrawAction _:
                case SummonAction _:
                case AddToHandAction _:
                case EquipAction _:
                case GainManaAction _:
                case GainArmorAction _:
                case BackdraftAction _:
                case UnearthAction _:
                    return TargetCandidateKinds.None;
                default:
                    throw new InvalidOperationException(
                        $"Rule action '{action.GetType().Name}' is absent from the target capability relation.");
            }
        }

        private static TargetCandidateKinds SelectorKinds(string selector)
        {
            switch (selector)
            {
                case "FRIENDLY_HERO":
                case "ENEMY_HERO":
                    return TargetCandidateKinds.Hero;
                case "FRIENDLY_MINIONS":
                case "OTHER_FRIENDLY_MINIONS":
                case "ENEMY_MINIONS":
                case "ALL_MINIONS":
                case "ANY_MINION":
                case "ANY_FRIENDLY_MINION":
                case "ADJACENT":
                    return TargetCandidateKinds.Minion;
                case "ANY_CHARACTER":
                case "ANY_FRIENDLY":
                case "ANY_ENEMY":
                    return TargetCandidateKinds.Hero | TargetCandidateKinds.Minion;
                case "FRIENDLY_WEAPON":
                case "ENEMY_WEAPON":
                    return TargetCandidateKinds.Weapon;
                case "FRIENDLY_HAND":
                case "ENEMY_HAND":
                case "ANY_HAND_CARD":
                    return TargetCandidateKinds.HandCard;
                case "FRIENDLY_DECK":
                case "ENEMY_DECK":
                case "ANY_DECK_CARD":
                    return TargetCandidateKinds.DeckCard;
                case "FRIENDLY_GRAVEYARD":
                case "ENEMY_GRAVEYARD":
                case "ANY_GRAVEYARD_CARD":
                    return TargetCandidateKinds.GraveyardCard;
                case "SELF":
                case "TRIGGER_SOURCE":
                    return TargetCandidateKinds.Any;
                default:
                    return TargetCandidateKinds.None;
            }
        }

        private static TargetCandidateKinds SourceKinds(CardType sourceType, Trigger trigger)
        {
            if (trigger == Trigger.ON_DEATH) return TargetCandidateKinds.GraveyardCard;
            switch (sourceType)
            {
                case CardType.MINION: return TargetCandidateKinds.Minion;
                case CardType.WEAPON: return TargetCandidateKinds.Weapon;
                case CardType.SPELL: return TargetCandidateKinds.GraveyardCard;
                default: return TargetCandidateKinds.None;
            }
        }

        private static TargetCandidateKinds TriggerSourceKinds(CardType sourceType, Trigger trigger)
        {
            switch (trigger)
            {
                case Trigger.ON_FRIENDLY_SUMMON:
                case Trigger.ON_DAMAGED:
                case Trigger.ON_HEAL:
                    return TargetCandidateKinds.Minion;
                case Trigger.ON_SPELL_CAST:
                case Trigger.ON_DEATH:
                    return TargetCandidateKinds.GraveyardCard;
                case Trigger.ON_ATTACK:
                    return TargetCandidateKinds.Weapon;
                case Trigger.ON_PLAY:
                case Trigger.ON_CAST:
                    return SourceKinds(sourceType, trigger);
                default:
                    return TargetCandidateKinds.None;
            }
        }
    }

}
