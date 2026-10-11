using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>Closed capability relation shared by AURA validation and its continuous runtime fold.</summary>
    public static class AuraCapabilities
    {
        public static bool Supports(CardType sourceType, RuleDefinition rule)
        {
            if (rule.Action is PreventLethalDamageAction prevention)
                return sourceType == CardType.MINION && rule.Activation is AuraActivation &&
                    rule.Condition is AlwaysCondition && rule.Lifetime is SourceBoundLifetime &&
                    prevention.Target.Selector == "OTHER_FRIENDLY_MINIONS" &&
                    prevention.Target.IsEveryCandidate && prevention.Target.Filter == null &&
                    prevention.Target.Binding.Length == 0 && prevention.Target.DistinctFrom.Length == 0 &&
                    !prevention.Target.IsCost;

            if (!(rule.Activation is AuraActivation) || !(rule.Condition is AlwaysCondition) ||
                !(rule.Action is ModifyStatsAction)) return false;
            if (!LifetimeFits(sourceType, rule.Lifetime)) return false;

            var target = ((ModifyStatsAction)rule.Action).Target;
            // An aura is a continuous fold over a whole set, so the plain sweep is the only
            // selection it can hold: a player pick has no moment to happen in and a random pick
            // would re-roll on every recompute.
            if (!target.IsEveryCandidate ||
                target.Binding.Length != 0 || target.DistinctFrom.Length != 0) return false;
            if (!IsFoldableFilter(target.Filter)) return false;

            return IsAutomaticMinionSet(target.Selector, sourceType);
        }

        /// <summary>
        /// How long an aura lasts, as a function of WHAT IS CASTING IT.
        ///
        /// <para>This is the whole of the spell-aura widening: a minion's aura lives exactly as long
        /// as the minion is on the board, and a spell — which resolves and is gone — must instead
        /// declare its own end. Rather than mint a lifetime node for a duration the model already
        /// expresses, the existing carrierless-observer lifetimes carry it, and the same three that
        /// <see cref="RuleLifetimeCapabilities.Allows"/> admits for every other ongoing spell rule
        /// are admitted here. There is deliberately no fourth case: a WEAPON or HERO_POWER aura has
        /// no runtime that folds it, so it fails closed like any unlisted pair.</para>
        ///
        /// <para>The three read, on an aura: ONE_SHOT is "this turn", FINITE n is "n of your turns",
        /// PERMANENT is the rest of the game. They mean turns rather than firings because an aura
        /// never resolves — see the tick site in <c>CommandProcessor.ApplyEndTurn</c>, which is the
        /// only thing that ages one.</para>
        /// </summary>
        private static bool LifetimeFits(CardType sourceType, RuleLifetime lifetime)
        {
            if (lifetime == null) return false;
            switch (sourceType)
            {
                case CardType.MINION:
                    return lifetime is SourceBoundLifetime;
                case CardType.SPELL:
                    return lifetime is OneShotLifetime || lifetime is PermanentLifetime ||
                           lifetime is FiniteLifetime finite && finite.Uses > 0;
                default:
                    return false;
            }
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
            // Cost is refused as the whitelist promises, not because it is known to be unstable --
            // a printed cost is as fixed as a tribe today, and nothing in the engine moves it. It
            // stays out until the question above is actually asked of it, and it has to be asked
            // again the day a cost-modifying effect exists, because that is the door the feedback
            // loop comes through. RETIREMENT TRIGGER: a card or effect that changes a card's cost.
            if (filter.MaxCost.HasValue) return false;
            // Keyword is the axis the whitelist was actually written for. An aura that grants a
            // keyword and selects on one is the confluence problem `docs/effect-model-scheduling.md`
            // §7.1 predicts, arriving through the front door rather than the side one Damaged came
            // through -- the aura's own grant changes the set it folds over.
            if (filter.Keyword.HasValue) return false;
            // NegateTribe is refused the same way: it is a NEW facet of the one foldable axis,
            // added 2026-09-05, and nobody has asked whether "everything except a tribe" recomputes
            // safely every time the board changes. Fail closed rather than admit it because the
            // whitelist happened to key on Tribe alone.
            if (filter.NegateTribe) return false;
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
        /// <para><c>NotSelf</c> ("your OTHER minions") was held out of this relation until Sam ruled
        /// it in (2026-09-30, Throneless King's +1/+1 to other friendly Undead). It is positional in
        /// the same way SELF and BESIDE are — "other than the source" needs a source standing on the
        /// board — so it is minion-only. What stays rejected and why: a
        /// hero-inclusive domain stays out because a stat aura on a hero means nothing;
        /// <c>TriggerSource</c> stays out because a continuous fold has no trigger to read.</para>
        /// </summary>
        private static bool IsAutomaticMinionSet(string selector, CardType sourceType)
        {
            if (!TargetQueries.TryGet(selector, out TargetQuery query)) return false;

            // SELF, BESIDE and NOT_SELF are POSITIONAL — they answer "the source" and "next to the source",
            // which only mean something for a thing standing in a board slot. A spell's aura is
            // carried by an OngoingEffectInstance, which has an id and a controller and no position
            // at all, so `Targeting` resolves either of them to the EMPTY SET and reports no error.
            // That is precisely the silent shape the 2026-08-20 collapse onto one relation existed
            // to delete — an aura that validates and affects nobody — so refuse them here, where the
            // refusal is visible, rather than letting the resolver return nothing.
            if (query.Relation == SourceRelation.Self || query.Relation == SourceRelation.Beside ||
                query.Relation == SourceRelation.NotSelf)
                if (sourceType != CardType.MINION) return false;

            // The source itself, wherever it is. SELF states no criteria — the relation names the
            // entity — so there is nothing else on the query to check.
            if (query.Relation == SourceRelation.Self) return true;
            if (query.Relation != SourceRelation.Any && query.Relation != SourceRelation.Beside &&
                query.Relation != SourceRelation.NotSelf)
                return false;
            return query.Zones == TargetZones.Board && query.Types == TargetTypes.Minion;
        }
    }
}
