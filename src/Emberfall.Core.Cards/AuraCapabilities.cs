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
            // An aura is a continuous fold over a whole set, so the plain sweep is the only
            // selection it can hold: a player pick has no moment to happen in and a random pick
            // would re-roll on every recompute.
            if (!target.IsEveryCandidate || target.Filter != null ||
                target.Binding.Length != 0 || target.DistinctFrom.Length != 0) return false;

            return IsAutomaticMinionSet(target.Selector);
        }

        /// <summary>
        /// Whether a target names a set of board minions an aura can fold over continuously.
        ///
        /// <para><b>A SHAPE, not a list of names.</b> Until 2026-08-20 this was five selector names
        /// here and the SAME five in <c>AuraEngine.Targets</c>, with nothing checking that the two
        /// agreed — the duplication <c>docs/effect-model-fork.md</c> names as
        /// "<c>AuraEngine.Targets</c> beside <c>Targeting</c>". The failure mode was silent in the
        /// worst direction: a name admitted here but missing there would validate and then apply to
        /// NOBODY, because the runtime switch fell through and yielded an empty sequence. No
        /// exception, no warning, an aura that simply does not exist.</para>
        ///
        /// <para>Both are gone. The runtime now resolves through <c>Targeting</c> like everything
        /// else, and this asks the query what it is instead of whether anybody remembered to list
        /// it. The admitted set is unchanged — the same five names, and only those five, satisfy
        /// this — but it is now closed under the criteria rather than under an author's memory, so a
        /// future name with the identical shape is admitted here AND resolvable there, together or
        /// not at all.</para>
        ///
        /// <para>What stays rejected and why: <c>NotSelf</c> ("your OTHER minions") is a coherent
        /// aura the engine could now serve, and it is deliberately still out — widening a
        /// fail-closed capability is a ruling, not a refactor, and this commit is a refactor. A
        /// hero-inclusive domain stays out because a stat aura on a hero means nothing;
        /// <c>TriggerSource</c> stays out because a continuous fold has no trigger to read.</para>
        /// </summary>
        private static bool IsAutomaticMinionSet(string selector)
        {
            if (!TargetQueries.TryGet(selector, out TargetQuery query)) return false;
            // The source itself, wherever it is. SELF states no criteria — the relation names the
            // entity — so there is nothing else on the query to check.
            if (query.Relation == SourceRelation.Self) return true;
            if (query.Relation != SourceRelation.Any && query.Relation != SourceRelation.Beside)
                return false;
            return query.Zones == TargetZones.Board && query.Types == TargetTypes.Minion;
        }
    }
}
