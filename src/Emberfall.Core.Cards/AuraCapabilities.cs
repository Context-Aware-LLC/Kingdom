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
            if (!target.IsEveryCandidate ||
                target.Binding.Length != 0 || target.DistinctFrom.Length != 0) return false;
            if (!IsFoldableFilter(target.Filter)) return false;

            return IsAutomaticMinionSet(target.Selector);
        }

        /// <summary>
        /// Whether a filter can survive being re-evaluated on every recompute.
        ///
        /// <para><b>TRIBE is admitted; DAMAGED is not (Sam, 2026-08-25).</b> The rule the two sit
        /// either side of is not "which fields do we like" — it is whether the predicate can be
        /// changed by the aura's own effect. A tribe is printed on the card and nothing in the game
        /// alters it, so folding over "your Constructs" reaches the same set every pass and settles
        /// in one. <c>Damaged</c> reads current health, and a stat aura WRITES current health, so an
        /// aura selecting on it is a feedback loop with no examined fixed point — a minion could
        /// leave the damaged set because the aura healed it into range and re-enter when the aura
        /// detaches. That is the same confluence problem `docs/effect-model-scheduling.md` §7.1
        /// predicts for keyword-granting auras, arriving early through a different door.</para>
        ///
        /// <para>So this is deliberately a WHITELIST of one field rather than a veto of one: a
        /// filter axis added later is refused until somebody asks whether an aura can change it.
        /// The previous gate was `Filter != null`, which refused tribe and damaged together and was
        /// strictly stricter than the engine — <c>AuraEngine.Targets</c> resolves through
        /// <c>Targeting.ResolutionCandidates</c>, which has always applied filters.</para>
        /// </summary>
        private static bool IsFoldableFilter(TargetFilterDef? filter)
        {
            if (filter == null) return true;
            if (filter.Damaged.HasValue) return false;
            // MaxCost is refused as the whitelist promises, not because it is known to be unstable
            // -- a printed cost is as fixed as a tribe today, and nothing in the engine moves it.
            // It stays out until the question above is actually asked of it, and it has to be asked
            // again the day a cost-modifying effect exists, because that is the door the feedback
            // loop comes through. RETIREMENT TRIGGER: a card or effect that changes a card's cost.
            if (filter.MaxCost.HasValue) return false;
            return filter.Tribe.HasValue && filter.Tribe.Value != Tribe.NONE;
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
