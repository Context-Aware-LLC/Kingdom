using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>Concise typed-rule builders for hand-authored engine fixtures.</summary>
    internal static class TestRules
    {
        public static RuleDefinition Rule(string id, Trigger trigger, RuleAction action)
            => Rule(id, trigger, AlwaysCondition.Instance, action);

        public static RuleDefinition Rule(string id, Trigger trigger, RuleCondition condition,
            RuleAction action)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(trigger), condition,
                trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? (RuleLifetime)OneShotLifetime.Instance
                    : SourceBoundLifetime.Instance,
                action);

        public static DamageAction Damage(Trigger trigger, int amount, string? selector = null)
            => new DamageAction(amount, new TargetSpec(selector ??
                (trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? "ANY_CHARACTER" : "ENEMY_HERO")));

        public static HealAction Heal(Trigger trigger, int amount, string? selector = null)
            => new HealAction(amount, new TargetSpec(selector ??
                (trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? "ANY_CHARACTER" : "FRIENDLY_HERO")));

        public static ModifyStatsAction ModifyStats(Trigger trigger, int attack, int health,
            string? selector = null)
            => new ModifyStatsAction(attack, health, new TargetSpec(selector ??
                (trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? "ANY_MINION" : "SELF")));
    }
}
