using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>
    /// Closed lifetime relation shared by card validation and the carrierless ongoing-effect runtime.
    /// A lifetime is legal only for the source/trigger shape that can actually retain it; unlisted
    /// lifetime nodes and malformed use counts fail closed.
    /// </summary>
    public static class RuleLifetimeCapabilities
    {
        /// <summary>Returns whether an authored rule lifetime is legal for its card source and trigger.</summary>
        public static bool Allows(CardType sourceType, Trigger trigger, RuleLifetime lifetime)
        {
            if (lifetime == null) return false;

            if (IsOngoingSpellRule(sourceType, trigger))
                return lifetime is OneShotLifetime || lifetime is PermanentLifetime ||
                       lifetime is FiniteLifetime finite && finite.Uses > 0;

            return RequiresOneShotLifetime(trigger)
                ? lifetime is OneShotLifetime
                : lifetime is SourceBoundLifetime;
        }

        /// <summary>
        /// Maps a live future-spell rule to the canonical persisted lifetime of its carrierless
        /// observer. The source/trigger relation is checked here rather than duplicated by the
        /// interpreter.
        /// </summary>
        public static bool TryCreateOngoingEffect(CardType sourceType, Trigger trigger,
            RuleLifetime lifetime, out OngoingEffectLifetime ongoingLifetime,
            out int? remainingUses)
        {
            ongoingLifetime = default;
            remainingUses = null;
            if (!IsOngoingSpellRule(sourceType, trigger) || !Allows(sourceType, trigger, lifetime))
                return false;

            switch (lifetime)
            {
                case OneShotLifetime:
                    ongoingLifetime = OngoingEffectLifetime.ONE_SHOT;
                    remainingUses = 1;
                    return true;
                case FiniteLifetime finite:
                    ongoingLifetime = OngoingEffectLifetime.FINITE;
                    remainingUses = finite.Uses;
                    return true;
                case PermanentLifetime:
                    ongoingLifetime = OngoingEffectLifetime.PERMANENT;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Advances one qualified ongoing-effect resolution. Unknown or malformed persisted shapes
        /// expire immediately, preventing an unbounded observer from surviving a bad snapshot.
        /// </summary>
        public static bool ConsumeOngoingEffect(OngoingEffectLifetime lifetime, int? remainingUses,
            out int? nextRemainingUses)
        {
            switch (lifetime)
            {
                case OngoingEffectLifetime.PERMANENT:
                    nextRemainingUses = null;
                    return false;
                case OngoingEffectLifetime.ONE_SHOT:
                case OngoingEffectLifetime.FINITE:
                    if (!remainingUses.HasValue || remainingUses.Value <= 0)
                    {
                        nextRemainingUses = 0;
                        return true;
                    }
                    nextRemainingUses = remainingUses.Value - 1;
                    return nextRemainingUses.Value <= 0;
                default:
                    nextRemainingUses = null;
                    return true;
            }
        }

        public static bool IsOngoingSpellRule(CardType sourceType, Trigger trigger)
            => sourceType == CardType.SPELL &&
               EffectSourceCapabilities.Supports(EffectSourceKind.OngoingEffect, trigger);

        public static bool IsOngoingOnly(RuleLifetime lifetime)
            => lifetime is FiniteLifetime || lifetime is PermanentLifetime;

        public static bool HasValidFiniteUses(RuleLifetime lifetime)
            => !(lifetime is FiniteLifetime finite) || finite.Uses > 0;

        public static string RequiredLifetimeDescription(CardType sourceType, Trigger trigger)
        {
            if (IsOngoingSpellRule(sourceType, trigger))
                return "ONE_SHOT, FINITE, or PERMANENT";
            return RequiresOneShotLifetime(trigger) ? "ONE_SHOT" : "SOURCE_BOUND";
        }

        public static bool RequiresOneShotLifetime(Trigger trigger)
            => trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST;
    }
}
