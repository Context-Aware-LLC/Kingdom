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

        // `selection` is required and has no default, the same rule the card files are held to.
        // The old signature let a selector imply one — "ANY_CHARACTER" quietly meant the player
        // picks — and a helper that can imply a selection is a helper that can build a rule no
        // author could write.
        public static DamageAction Damage(Trigger trigger, int amount, TargetSelection selection,
            string? selector = null)
            => new DamageAction(amount, new TargetSpec(selector ??
                (trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? "ANY_CHARACTER" : "ENEMY_HERO"), selection));

        public static HealAction Heal(Trigger trigger, int amount, TargetSelection selection,
            string? selector = null)
            => new HealAction(amount, new TargetSpec(selector ??
                (trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? "ANY_CHARACTER" : "FRIENDLY_HERO"), selection));

        public static ModifyStatsAction ModifyStats(Trigger trigger, int attack, int health,
            TargetSelection selection, string? selector = null)
            => new ModifyStatsAction(attack, health, new TargetSpec(selector ??
                (trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? "ANY_MINION" : "SELF"), selection));
    }
}
