using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>Closed capability relation shared by AURA validation and its continuous runtime fold.</summary>
    public static class AuraCapabilities
    {
        public static bool Supports(CardType sourceType, RuleDefinition rule)
        {
            if (sourceType != CardType.MINION || !(rule.Activation is AuraActivation) ||
                !(rule.Condition is AlwaysCondition) || !(rule.Lifetime is SourceBoundLifetime) ||
                !(rule.Action is ModifyStatsAction)) return false;

            var target = ((ModifyStatsAction)rule.Action).Target;
            if (target.ChosenBy.HasValue || target.Count.HasValue || target.Filter != null ||
                target.Binding.Length != 0 || target.DistinctFrom.Length != 0) return false;

            switch (target.Selector)
            {
                case "SELF":
                case "FRIENDLY_MINIONS":
                case "ENEMY_MINIONS":
                case "ALL_MINIONS":
                case "ADJACENT":
                    return true;
                default:
                    return false;
            }
        }
    }
}
