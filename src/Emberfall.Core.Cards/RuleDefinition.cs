using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Verb = Emberfall.Core.Contracts.Action;

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

    /// <summary>
    /// Transitional runtime boundary. Typed rules are canonical; existing interpreter code may
    /// consume the projected EffectDef until its handlers move to RuleAction directly.
    /// </summary>
    public static class RuleRuntimeAdapter
    {
        public static IReadOnlyList<RuleDefinition> FromLegacy(string cardId,
            IReadOnlyList<EffectDef> effects)
        {
            var rules = new List<RuleDefinition>(effects.Count);
            for (int i = 0; i < effects.Count; i++)
                rules.Add(FromLegacy(new RuleId($"rule_{i + 1}"), effects[i]));
            return rules;
        }

        public static RuleDefinition FromLegacy(RuleId id, EffectDef effect)
        {
            if (effect == null) throw new ArgumentNullException(nameof(effect));
            RuleActivation activation = effect.Trigger == Trigger.AURA
                ? (RuleActivation)AuraActivation.Instance
                : new TriggeredActivation(effect.Trigger);
            RuleCondition condition = effect.Condition == null
                ? (RuleCondition)AlwaysCondition.Instance
                : effect.Condition.Type == ConditionType.TURN_OWNER && effect.Condition.TurnOwner.HasValue
                    ? new TurnOwnerCondition(effect.Condition.TurnOwner.Value)
                    : throw new InvalidOperationException("The legacy condition cannot be represented as a typed rule.");
            RuleLifetime lifetime = effect.Trigger == Trigger.ON_PLAY || effect.Trigger == Trigger.ON_CAST
                ? (RuleLifetime)OneShotLifetime.Instance
                : SourceBoundLifetime.Instance;
            if (effect.VfxOverride.HasValue)
                throw new InvalidOperationException(
                    "Legacy rule-level VFX overrides are not part of the normalized rule model.");
            return new RuleDefinition(id, activation, condition, lifetime, FromLegacyAction(effect));
        }

        public static IReadOnlyList<EffectDef> ToLegacy(IReadOnlyList<RuleDefinition> rules)
        {
            var effects = new List<EffectDef>(rules.Count);
            foreach (RuleDefinition rule in rules) effects.Add(ToLegacy(rule));
            return effects;
        }

        public static EffectDef ToLegacy(RuleDefinition rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            Trigger trigger = rule.Activation is TriggeredActivation triggered
                ? triggered.Trigger
                : rule.Activation is AuraActivation
                    ? Trigger.AURA
                    : throw new InvalidOperationException($"Unknown activation node '{rule.Activation.GetType().Name}'.");
            EffectConditionDef? condition = rule.Condition is AlwaysCondition
                ? null
                : rule.Condition is TurnOwnerCondition turnOwner
                    ? new EffectConditionDef(ConditionType.TURN_OWNER, turnOwner.Owner)
                    : throw new InvalidOperationException($"Unknown condition node '{rule.Condition.GetType().Name}'.");
            if (!(rule.Lifetime is SourceBoundLifetime) && !(rule.Lifetime is OneShotLifetime))
                throw new InvalidOperationException(
                    $"Lifetime '{rule.Lifetime.GetType().Name}' requires the ongoing-effect runtime slice.");
            return ToLegacyEffect(trigger, condition, rule.Action);
        }

        private static RuleAction FromLegacyAction(EffectDef effect)
        {
            switch (effect.Action)
            {
                case Verb.DEAL_DAMAGE: return new DamageAction(effect.Amount ?? 0, RequireTarget(effect));
                case Verb.HEAL: return new HealAction(effect.Amount ?? 0, RequireTarget(effect));
                case Verb.DRAW: return new DrawAction(effect.Amount ?? 1);
                case Verb.SUMMON: return new SummonAction(effect.TokenId ?? "", effect.Amount ?? 1);
                case Verb.BUFF: return new ModifyStatsAction(effect.Amount ?? 0, effect.Amount2 ?? 0, RequireTarget(effect));
                case Verb.GIVE_KEYWORD:
                    if (!effect.GrantedKeyword.HasValue)
                        throw new InvalidOperationException("GIVE_KEYWORD requires a keyword before typed adaptation.");
                    return new GrantKeywordAction(effect.GrantedKeyword.Value, RequireTarget(effect));
                case Verb.DESTROY: return new DestroyAction(RequireTarget(effect));
                case Verb.RETURN_TO_HAND: return new ReturnToHandAction(RequireTarget(effect));
                case Verb.SILENCE: return new SilenceAction(RequireTarget(effect));
                case Verb.UNEARTH: return new UnearthAction(effect.Amount ?? 1);
                case Verb.TRANSFORM: return new TransformAction(effect.TokenId ?? "", RequireTarget(effect));
                case Verb.EQUIP: return new EquipAction(effect.TokenId ?? "");
                case Verb.GAIN_MANA:
                    return new GainManaAction(effect.Amount ?? 0,
                        effect.Permanent
                            ? effect.Empty ? ManaGainKind.PERMANENT_EMPTY : ManaGainKind.PERMANENT_FILLED
                            : ManaGainKind.TEMPORARY);
                case Verb.ADD_TO_HAND: return new AddToHandAction(effect.TokenId ?? "", effect.Amount ?? 1);
                case Verb.FROST: return new FrostAction(RequireTarget(effect));
                case Verb.BACKDRAFT: return new BackdraftAction(effect.Amount ?? 0);
                default:
                    throw new InvalidOperationException($"Action '{effect.Action}' has no typed rule node.");
            }
        }

        private static TargetSpec RequireTarget(EffectDef effect)
            => effect.Target ?? throw new InvalidOperationException(
                $"{effect.Action} requires an explicit target before typed adaptation.");

        private static EffectDef ToLegacyEffect(Trigger trigger, EffectConditionDef? condition,
            RuleAction action)
        {
            switch (action)
            {
                case DamageAction a: return Effect(trigger, Verb.DEAL_DAMAGE, a.Amount, null, null, null, a.Target, condition);
                case HealAction a: return Effect(trigger, Verb.HEAL, a.Amount, null, null, null, a.Target, condition);
                case DrawAction a: return Effect(trigger, Verb.DRAW, a.Count, null, null, null, null, condition);
                case SummonAction a: return Effect(trigger, Verb.SUMMON, a.Count, null, a.CardId, null, null, condition);
                case ModifyStatsAction a: return Effect(trigger, Verb.BUFF, a.AttackDelta, a.HealthDelta, null, null, a.Target, condition);
                case ReinforceAction a: return Effect(trigger, Verb.BUFF, 1, 1, null, null, a.Target, condition);
                case GrantKeywordAction a: return Effect(trigger, Verb.GIVE_KEYWORD, null, null, null, a.Keyword, a.Target, condition);
                case DestroyAction a: return Effect(trigger, Verb.DESTROY, null, null, null, null, a.Target, condition);
                case ReturnToHandAction a: return Effect(trigger, Verb.RETURN_TO_HAND, null, null, null, null, a.Target, condition);
                case SilenceAction a: return Effect(trigger, Verb.SILENCE, null, null, null, null, a.Target, condition);
                case UnearthAction a: return Effect(trigger, Verb.UNEARTH, a.Count, null, null, null, null, condition);
                case TransformAction a: return Effect(trigger, Verb.TRANSFORM, null, null, a.CardId, null, a.Target, condition);
                case EquipAction a: return Effect(trigger, Verb.EQUIP, null, null, a.CardId, null, null, condition);
                case GainManaAction a:
                    return new EffectDef(trigger, Verb.GAIN_MANA, a.Amount, null, null, null, null,
                        permanent: a.Kind != ManaGainKind.TEMPORARY,
                        empty: a.Kind == ManaGainKind.PERMANENT_EMPTY, condition: condition);
                case AddToHandAction a: return Effect(trigger, Verb.ADD_TO_HAND, a.Count, null, a.CardId, null, null, condition);
                case FrostAction a: return Effect(trigger, Verb.FROST, null, null, null, null, a.Target, condition);
                case BackdraftAction a: return Effect(trigger, Verb.BACKDRAFT, a.Amount, null, null, null, null, condition);
                default:
                    throw new InvalidOperationException($"Rule action '{action.GetType().Name}' has no legacy runtime adapter.");
            }
        }

        private static EffectDef Effect(Trigger trigger, Verb action, int? amount, int? amount2,
            string? cardId, Keyword? keyword, TargetSpec? target, EffectConditionDef? condition)
            => new EffectDef(trigger, action, amount, amount2, cardId, keyword, null,
                condition: condition, target: target);
    }
}
