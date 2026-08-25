using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// Public, Unity-free target candidate. Instance identity is the authoritative reference; kind,
    /// controller and zone let a client present candidates without learning engine-private objects.
    /// </summary>
    public readonly struct TargetCandidate : IEquatable<TargetCandidate>
    {
        public InstanceId Instance { get; }
        public InstanceId Id => Instance;
        public PlayerId Controller { get; }
        /// <summary>WHERE this candidate is. The other half of the old fused
        /// <c>TargetCandidateKind</c> -- WHAT it is -- is not stored here at all: it is a property
        /// of the definition, reachable through <see cref="DefinitionId"/>, and storing it would be
        /// the same fact in a second place. The hero is the one candidate with no definition.</summary>
        public Zone Zone { get; }

        internal CharacterRef Character { get; }
        internal int DefinitionId { get; }
        internal CardInstance? Card { get; }
        internal int Seat { get; }
        /// <summary>A character is the Board zone -- that is the whole definition, and the reason
        /// the hero is handed <see cref="Zone.Board"/>.</summary>
        internal bool IsCharacter => Zone == Zone.Board;
        internal bool IsHero => Character.IsHero;
        internal bool IsMinion => Character.IsMinion;
        internal HeroInstance? Hero => Character.Hero;
        internal CardInstance? Minion => Character.Minion;
        /// <summary>See <see cref="CharacterRef.Host"/> — the single accessor that collapses an
        /// IsMinion/IsHero branch pair over `.Effects` into one resolve-then-apply.</summary>
        internal IEffectHost? Host => Character.Host;

        internal TargetCandidate(InstanceId instance, PlayerId controller,
            Zone zone, CharacterRef character = default, int definitionId = 0,
            CardInstance? card = null, int seat = -1)
        {
            Instance = instance;
            Controller = controller;
            Zone = zone;
            Character = character;
            DefinitionId = definitionId;
            Card = card;
            Seat = seat;
        }

        public bool Equals(TargetCandidate other) => Instance.Equals(other.Instance);
        public override bool Equals(object? obj) => obj is TargetCandidate other && Equals(other);
        public override int GetHashCode() => Instance.GetHashCode();
        public override string ToString() => $"{Zone}:{Instance}";
    }

    /// <summary>One ordered player-choice step exposed to headless and Unity presentation code.</summary>
    public sealed class TargetChoiceRequirement
    {
        public string Binding { get; }
        public int RequiredCount { get; }

        /// <summary>
        /// The FEWEST targets the player may supply. Equal to <see cref="RequiredCount"/> for every
        /// ordinary choice — a prompt you cannot walk away from — and zero for an OPTIONAL cost,
        /// which is the only shape that lets a player decline a pick the board could satisfy.
        ///
        /// <para><b>A client that ignores this keeps working.</b> It is additive precisely so the
        /// Unity presentation layer, which this solution does not compile, is not silently broken by
        /// a field it has never heard of; such a client simply never offers the decline.</para>
        ///
        /// <para>RETIREMENT TRIGGER: the client that first READS this is the one wiring the decline
        /// control, and it discharges item 1.1 of <c>docs/effect-model-migration.md</c>, "A control for
        /// declining an optional cost". Delete that entry in the same change. Until
        /// then an optional cost silently behaves as a required one whenever the board can pay it —
        /// the card face says "You may" and the game never lets you not, with nothing failing.</para>
        /// </summary>
        public int MinimumCount { get; }

        public IReadOnlyList<string> DistinctFrom { get; }
        public IReadOnlyList<TargetCandidate> Candidates { get; }

        internal TargetChoiceRequirement(string binding, int requiredCount, int minimumCount,
            IReadOnlyList<string> distinctFrom, IReadOnlyList<TargetCandidate> candidates)
        {
            Binding = binding;
            RequiredCount = requiredCount;
            MinimumCount = minimumCount;
            DistinctFrom = distinctFrom;
            Candidates = candidates;
        }
    }

    /// <summary>
    /// The one target-policy seam used by intent validation, effect resolution and client targeting.
    /// Candidate order is canonical: controller side before opponent side; heroes before board
    /// minions; board and card zones in list order.
    ///
    /// <para>Stated as a sort key, which is the form that survives the selector names collapsing
    /// into a criteria object: <b>side (controller first) → zone → the hero before board minions →
    /// position within the owning zone</b>, where zone is <see cref="Zone"/>'s own declaration
    /// order. Every arm of the switch below must emit a subsequence of that order, and the filter
    /// pass after it must not reorder. <c>CandidateOrderTests</c> enforces this over all 26
    /// selectors from BOTH seats — the second seat is what distinguishes controller-relative from
    /// seat-absolute.</para>
    ///
    /// <para><b>The middle key was re-anchored when kind×zone was unfused (2026-08-20), and the
    /// cross-zone half of the order deliberately changed.</b> It used to be
    /// <c>TargetCandidateKind</c>'s declaration order — Hero, Minion, Weapon, HandCard, DeckCard,
    /// GraveyardCard — and that enum no longer exists to anchor it. Nothing observable moved,
    /// because every selector produces candidates from exactly ONE zone, so the cross-zone ranking
    /// has never had a list to sort; the hero-before-minions half, which IS exercised, is preserved
    /// verbatim and stated separately because the hero is the one non-card. Recorded rather than
    /// left silent precisely because a query that spans zones — the thing unfusing makes possible —
    /// is what makes this key observable for the first time.</para>
    ///
    /// <para><b>This is not cosmetic.</b> <see cref="ResolveTargets"/> draws a RANDOM target with
    /// <c>state.Rng.NextInt(candidates.Count)</c>, so the order is the mapping from RNG index to
    /// entity. Reordering an arm diverges every RANDOM resolution in a live match while moving no
    /// state hash — which is why it is pinned by a rule here rather than left to a golden fixture.</para>
    /// </summary>
    public static class Targeting
    {
        public static IReadOnlyCollection<string> Selectors => TargetSpec.Selectors;

        public static bool IsPlayerChosen(TargetSpec? spec)
            => TargetSpec.IsPlayerChosen(spec);

        // RequiresPlayerChoice(def, trigger) lived here and was deleted with its last caller.
        // "Does this card ask for a target" is now the WRONG question and the name did not say so:
        // a herald whose candidate set is empty asks for nothing, because the play fizzles instead of
        // being refused. Ask PlayerChoicePlan and read RequiredCount, which consults the board.

        /// <summary>
        /// Build the ordered, binding-aware choice plan a client must satisfy. Effects sharing a
        /// binding share one choice; their candidate sets are intersected in canonical order.
        /// </summary>
        public static IReadOnlyList<TargetChoiceRequirement> PlayerChoicePlan(GameState state,
            ICardDatabase db, PlayerId controller, CardDefinition def, Trigger trigger,
            InstanceId? source = null)
        {
            var groups = new List<ChoiceGroup>();
            var byBinding = new Dictionary<string, ChoiceGroup>();
            foreach (RuleDefinition rule in def.Rules)
            {
                TargetSpec? candidateTarget = RuleSemantics.TargetOf(rule.Action);
                if (!RuleSemantics.ActivatesOn(rule, trigger) || !IsPlayerChosen(candidateTarget)) continue;
                TargetSpec spec = candidateTarget!;
                IReadOnlyList<TargetCandidate> candidates = Candidates(state, db, controller,
                    spec, rule.Action, source);
                int required = RequiredPlayerCount(spec, def.Type, candidates.Count);
                int minimum = MinimumPlayerCount(spec, required);
                if (!byBinding.TryGetValue(spec.Binding, out ChoiceGroup? group))
                {
                    group = new ChoiceGroup(spec.Binding, required, minimum, candidates);
                    byBinding.Add(spec.Binding, group);
                    groups.Add(group);
                }
                else
                {
                    group.Intersect(candidates);
                    if (group.RequiredCount != required) group.RequiredCount = int.MaxValue;
                    // Two specs sharing one choice must BOTH be satisfiable by what the player
                    // sends, so the binding is only declinable if every spec on it is.
                    if (minimum > group.MinimumCount) group.MinimumCount = minimum;
                }
                group.AddDistinctFrom(spec.DistinctFrom);
            }

            var result = new List<TargetChoiceRequirement>(groups.Count);
            foreach (ChoiceGroup group in groups)
                result.Add(new TargetChoiceRequirement(group.Binding, group.RequiredCount,
                    group.MinimumCount, group.DistinctFrom, group.Candidates));
            return result;
        }

        /// <summary>
        /// Candidate intersection for one named choice. An effect with no <c>binding</c> is the
        /// ordinary authored shape, not a deprecated one (149 of 171 authored targets) — all such
        /// effects share a single choice, so a candidate must satisfy every unbound player-chosen
        /// effect on the card. A named binding isolates one effect into its own choice.
        /// </summary>
        public static IReadOnlyList<TargetCandidate> PlayerChoiceCandidates(GameState state,
            ICardDatabase db, PlayerId controller, CardDefinition def, Trigger trigger,
            string? binding = null, InstanceId? source = null)
        {
            string key = binding ?? "";
            foreach (TargetChoiceRequirement requirement in PlayerChoicePlan(
                         state, db, controller, def, trigger, source))
                if (requirement.Binding == key) return requirement.Candidates;
            return Array.Empty<TargetCandidate>();
        }

        private sealed class ChoiceGroup
        {
            public readonly string Binding;
            public int RequiredCount;
            public int MinimumCount;
            public readonly List<string> DistinctFrom = new List<string>();
            public readonly List<TargetCandidate> Candidates;

            public ChoiceGroup(string binding, int requiredCount, int minimumCount,
                IReadOnlyList<TargetCandidate> candidates)
            {
                Binding = binding;
                RequiredCount = requiredCount;
                MinimumCount = minimumCount;
                Candidates = new List<TargetCandidate>(candidates);
            }

            public void AddDistinctFrom(string binding)
            {
                if (binding.Length > 0 && !DistinctFrom.Contains(binding)) DistinctFrom.Add(binding);
            }

            public void Intersect(IReadOnlyList<TargetCandidate> candidates)
            {
                var allowed = new HashSet<InstanceId>();
                foreach (TargetCandidate candidate in candidates) allowed.Add(candidate.Instance);
                for (int i = Candidates.Count - 1; i >= 0; i--)
                    if (!allowed.Contains(Candidates[i].Instance)) Candidates.RemoveAt(i);
            }
        }

        /// <summary>
        /// Enumerate one target specification for a player. This is the presentation seam: Unity
        /// receives exactly the candidates the server will accept, including damaged/tribe filters
        /// and enemy Shroud exclusion. Hidden opponent zones and ordered decks fail closed.
        /// </summary>
        public static IReadOnlyList<TargetCandidate> Candidates(GameState state, ICardDatabase db,
            PlayerId controller, TargetSpec spec, RuleAction? action = null,
            InstanceId? source = null,
            InstanceId? triggerSource = null)
            => EnumerateCandidates(state, db, controller, spec, action, source, triggerSource,
                forPlayerChoice: true, allowHiddenZones: false);

        internal static IReadOnlyList<TargetCandidate> ResolutionCandidates(GameState state,
            ICardDatabase db, PlayerId controller, TargetSpec spec, RuleAction? action = null,
            InstanceId? source = null,
            InstanceId? triggerSource = null)
            => EnumerateCandidates(state, db, controller, spec, action, source, triggerSource,
                forPlayerChoice: false, allowHiddenZones: true);

        private static IReadOnlyList<TargetCandidate> EnumerateCandidates(GameState state, ICardDatabase db,
            PlayerId controller, TargetSpec spec, RuleAction? action, InstanceId? source,
            InstanceId? triggerSource, bool forPlayerChoice, bool allowHiddenZones)
        {
            int friendly = state.SeatOf(controller);
            var result = new List<TargetCandidate>();
            Enumerate(state, db, TargetQueries.Get(spec.Selector), friendly,
                source, triggerSource, allowHiddenZones, result);

            for (int i = result.Count - 1; i >= 0; i--)
            {
                TargetCandidate candidate = result[i];
                if (source.HasValue && candidate.Zone == Zone.Hand &&
                    candidate.Instance == source.Value)
                {
                    result.RemoveAt(i);
                    continue;
                }
                if (forPlayerChoice && IsHiddenEnemyMinion(state, friendly, candidate))
                {
                    result.RemoveAt(i);
                    continue;
                }
                if (action != null && !ActionAllows(action, candidate, db))
                {
                    result.RemoveAt(i);
                    continue;
                }
                if (!PassesFilter(candidate, spec.Filter, db))
                    result.RemoveAt(i);
            }
            return result;
        }

        /// <summary>Validate every player-authored target before any state mutation or event emission.</summary>
        public static bool ValidatePlayerChoices(GameState state, ICardDatabase db, PlayerId controller,
            CardDefinition def, Trigger trigger, IReadOnlyList<TargetRef> choices, InstanceId? source = null)
        {
            var playerRules = new List<RuleDefinition>();
            foreach (RuleDefinition rule in def.Rules)
            {
                if (!RuleSemantics.ActivatesOn(rule, trigger)) continue;
                if (IsPlayerChosen(RuleSemantics.TargetOf(rule.Action))) playerRules.Add(rule);
            }

            // No activating rule asks the player to choose, so every target the client sent is
            // unsolicited and the command is refused. There is no selector-less effect to fall back
            // FOR: a selector is required by construction, on two levels that both predate this
            // check — CardJson.TargetFromElement throws FormatException on an action whose `target`
            // omits `selector`, and TargetedRuleAction's constructor throws on a null TargetSpec. An
            // action that can take a target therefore always HAS one, and `playerRules.Count == 0`
            // can only mean the card wanted no player target at all.
            //
            // This used to accept unsolicited targets on an untargeted card (live-character +
            // distinct, then true), labelled as compatibility for "pre-TargetSpec" content. No such
            // content exists or can exist; the branch only widened what client-authored input this
            // VULN-001 surface would take. Removed 2026-08-07 under the greenfield ruling: conform
            // the content to the present model rather than carrying a shim for a past that has none.
            if (playerRules.Count == 0) return choices.Count == 0;

            var consumed = new bool[choices.Count];
            foreach (RuleDefinition rule in playerRules)
            {
                TargetSpec spec = ((TargetedRuleAction)rule.Action).Target;
                IReadOnlyList<TargetCandidate> candidates = Candidates(state, db, controller, spec,
                    rule.Action, source);
                var candidateIds = new HashSet<InstanceId>();
                foreach (TargetCandidate candidate in candidates) candidateIds.Add(candidate.Instance);

                var selected = new List<TargetRef>();
                for (int i = 0; i < choices.Count; i++)
                {
                    bool belongs = spec.Binding.Length == 0
                        ? choices[i].Binding.Length == 0
                        : choices[i].Binding == spec.Binding;
                    if (!belongs) continue;
                    consumed[i] = true;
                    selected.Add(choices[i]);
                }

                int required = RequiredPlayerCount(spec, def.Type, candidates.Count);
                int minimum = MinimumPlayerCount(spec, required);
                if (selected.Count > required || selected.Count < minimum || !Distinct(selected))
                    return false;
                foreach (TargetRef choice in selected)
                    if (!candidateIds.Contains(choice.Instance)) return false;
            }

            for (int i = 0; i < consumed.Length; i++)
                if (!consumed[i]) return false;

            foreach (RuleDefinition rule in playerRules)
            {
                TargetSpec spec = ((TargetedRuleAction)rule.Action).Target;
                if (spec.DistinctFrom.Length == 0) continue;
                foreach (TargetRef a in choices)
                    if (a.Binding == spec.Binding)
                        foreach (TargetRef b in choices)
                            if (b.Binding == spec.DistinctFrom && a.Instance == b.Instance)
                                return false;
            }
            return true;
        }

        internal static IReadOnlyList<TargetCandidate> ResolveTargets(GameState state, ICardDatabase db,
            EffectSourceContext source, IReadOnlyList<TargetRef> choices,
            TargetBindingContext bindings, InstanceId? triggerSource)
        {
            RuleDefinition rule = source.Rule;
            if (!RuleActionCapabilities.ConsumesTargets(rule.Action))
                return Array.Empty<TargetCandidate>();

            TargetSpec spec = ((TargetedRuleAction)rule.Action).Target;

            IReadOnlyList<TargetCandidate> selected;
            if (IsPlayerChosen(spec))
            {
                selected = ChoiceCandidates(state, db, choices, spec.Binding, rule.Action);
            }
            else
            {
                var candidates = new List<TargetCandidate>(ResolutionCandidates(state, db,
                    source.Controller, spec, rule.Action, source.Identity, triggerSource));
                bindings.Exclude(spec.DistinctFrom, candidates);
                if (spec.Selection.IsRandom)
                {
                    int count = RequiredAutomaticCount(spec, candidates.Count);
                    var random = new List<TargetCandidate>(count);
                    while (random.Count < count && candidates.Count > 0)
                    {
                        int index = state.Rng.NextInt(candidates.Count);
                        random.Add(candidates[index]);
                        candidates.RemoveAt(index);
                    }
                    selected = random;
                }
                else
                {
                    // Nobody is choosing and the engine is not rolling, so the count has reached
                    // the selector's maximum and the candidate list IS the selection. The
                    // take-the-first-N branch that used to sit here is gone with the shape that
                    // authored it: an automatic finite count took candidates in board order, which
                    // meant "the two leftmost, every time" and was never what any card wanted.
                    // C27 is what keeps that shape unauthorable now the count no longer names its
                    // chooser.
                    selected = candidates;
                }
            }

            bindings.Record(spec.Binding, selected);
            // A cost that reached nothing was not paid. Recorded here rather than in the action
            // handler because a pick can come back empty without any handler running at all — a
            // declined optional cost and a fizzled one both arrive as an empty selection.
            if (spec.IsCost) bindings.RecordCostPaid(selected.Count > 0);
            return selected;
        }

        /// <summary>
        /// How many targets the player OWES for one spec. The whole play is rejected when he supplies
        /// a different number, so this is also the answer to "is this card playable right now".
        ///
        /// <para><b>A shortfall fizzles on a MINION or WEAPON and refuses on a SPELL</b> (Sam,
        /// 2026-08-07). The body is the difference: a minion whose herald finds nothing is still a
        /// minion, so demanding a target it cannot have made 15 rows unplayable — a Reinforce herald
        /// could never be your first minion. A spell that fizzles is pure card-burn with nothing left
        /// over, so it stays refused and the card stays in hand.</para>
        ///
        /// <para><b>A REQUIRED cost opts back out of the fizzle</b> — "sacrifice a friendly minion".
        /// An unpayable cost is unplayable on every card type, body or not. An OPTIONAL cost does
        /// not: it fizzles like any other pick, the body lands, and <see cref="CostPaidCondition"/>
        /// is what stops the run it would have bought. On a SPELL both spellings refuse anyway,
        /// which is why C28 rejects the optional one there rather than letting it read as intent the
        /// runtime ignores (Sam, 2026-08-22).</para>
        ///
        /// <para>Clamping here mirrors <see cref="RequiredAutomaticCount"/>, which has always taken
        /// as many candidates as exist. The two paths disagreeing is what produced the bug — and the
        /// selection term is what stopped them being able to: both now read one number off
        /// <see cref="TargetSpec.Selection"/> instead of each deciding what an absent count meant.</para>
        /// </summary>
        private static int RequiredPlayerCount(TargetSpec spec, CardType sourceType, int candidateCount)
        {
            int authored = spec.Selection.Count;
            return CanFizzle(spec, sourceType) ? Math.Min(authored, candidateCount) : authored;
        }

        private static bool CanFizzle(TargetSpec spec, CardType sourceType)
            => spec.Cost != CostKind.Required &&
               (sourceType == CardType.MINION || sourceType == CardType.WEAPON);

        /// <summary>
        /// The FEWEST targets a player may supply for one spec. Everything is take-it-or-leave-the-
        /// card except an OPTIONAL cost, which is the only shape in the model where declining a
        /// payable pick is a legal play rather than a malformed command.
        ///
        /// <para>Clamped by <paramref name="required"/> so a cost with nothing to pay it stays at
        /// zero on both ends: on a MINION the fizzle already took the count to zero, and demanding
        /// more than the plan offers would refuse a card the fizzle rule says is playable.</para>
        /// </summary>
        private static int MinimumPlayerCount(TargetSpec spec, int required)
            => spec.Cost == CostKind.Optional ? 0 : required;

        /// <summary>
        /// How many candidates an automatic spec takes.
        ///
        /// <para>This used to branch on the selection MODE: every candidate under <c>all</c>,
        /// otherwise the authored count clamped. The branch is gone rather than rewritten — "every
        /// candidate" is now a count that has reached the selector's maximum, and clamping a count
        /// that is already at or above the maximum down to the candidates that exist yields exactly
        /// the candidate list. One expression covers both because they were never two things.</para>
        /// </summary>
        private static int RequiredAutomaticCount(TargetSpec spec, int candidateCount)
            => Math.Min(spec.Selection.Count, candidateCount);

        private static IReadOnlyList<TargetCandidate> ChoiceCandidates(GameState state, ICardDatabase db,
            IReadOnlyList<TargetRef> choices, string binding, RuleAction action)
        {
            var result = new List<TargetCandidate>();
            foreach (TargetRef choice in choices)
                if (choice.Binding == binding && TryCandidate(state, choice.Instance, out TargetCandidate candidate) &&
                    ActionAllows(action, candidate, db))
                    result.Add(candidate);
            return result;
        }

        /// <summary>
        /// The action capability check over the two unfused axes. Zone comes off the candidate;
        /// TYPE is resolved from the definition, which is the only place it is stored.
        ///
        /// <para>Fails closed on an unresolvable definition. The hero is the one candidate that
        /// legitimately has none -- it is the only non-card (Sam, 2026-08-20) -- and is passed as a
        /// null type rather than being given a synthetic definition to look up.</para>
        /// </summary>
        private static bool ActionAllows(RuleAction action, TargetCandidate candidate, ICardDatabase db)
        {
            if (candidate.DefinitionId <= 0)
                return RuleActionCapabilities.Allows(action, candidate.Zone, null);
            return db.TryGet(candidate.DefinitionId, out CardDefinition def) &&
                   RuleActionCapabilities.Allows(action, candidate.Zone, def.Type);
        }

        private static bool Distinct(IReadOnlyList<TargetRef> choices)
        {
            var seen = new HashSet<InstanceId>();
            foreach (TargetRef choice in choices)
                if (!seen.Add(choice.Instance)) return false;
            return true;
        }

        private static bool PassesFilter(TargetCandidate candidate, TargetFilterDef? filter, ICardDatabase db)
        {
            if (filter == null) return true;
            if (filter.Damaged.HasValue)
            {
                if (!candidate.IsCharacter) return false;
                bool damaged = candidate.Character.IsHero
                    ? candidate.Character.Hero!.CurrentHealth < candidate.Character.Hero.MaxHealth
                    : candidate.Character.Minion!.CurrentHealth < candidate.Character.Minion.MaxHealth;
                if (damaged != filter.Damaged.Value) return false;
            }
            if (filter.Tribe.HasValue)
            {
                if (candidate.DefinitionId <= 0 ||
                    !db.TryGet(candidate.DefinitionId, out CardDefinition def) ||
                    def.Tribe != filter.Tribe.Value) return false;
            }
            // The PRINTED cost, which is the only one there is -- no effect modifies a card's cost,
            // so there is no live value to prefer and a candidate with no definition behind it (a
            // hero) simply fails rather than passing by default.
            if (filter.MaxCost.HasValue)
            {
                if (candidate.DefinitionId <= 0 ||
                    !db.TryGet(candidate.DefinitionId, out CardDefinition costDef) ||
                    costDef.Cost > filter.MaxCost.Value) return false;
            }
            return true;
        }

        private static bool IsHiddenEnemyMinion(GameState state, int friendlySeat, TargetCandidate candidate)
            => candidate.IsMinion &&
               candidate.Character.Seat != friendlySeat &&
               candidate.Character.Minion!.Keywords.Contains(Keyword.SHROUD);

        /// <summary>
        /// The whole enumeration, as one pass over the four axes. This replaced a 26-arm switch --
        /// one arm per selector NAME -- in which the arms for <c>ALL_MINIONS</c> and
        /// <c>ANY_MINION</c> were the same two statements written twice.
        ///
        /// <para><b>The loop order IS the canonical candidate order</b>: side (controller first),
        /// then zone in <see cref="Zone"/>'s declaration order, then the hero before the board
        /// minions, then position within the owning list. It is not imposed on the result
        /// afterwards -- there is no sort here, and adding one would put the rule in two places.
        /// <c>CandidateOrderTests</c> is what holds the loop to it.</para>
        /// </summary>
        private static void Enumerate(GameState state, ICardDatabase db, TargetQuery query,
            int friendlySeat, InstanceId? source, InstanceId? triggerSource, bool allowHiddenZones,
            List<TargetCandidate> result)
        {
            int enemySeat = friendlySeat == 0 ? 1 : 0;
            for (int side = 0; side < 2; side++)
            {
                bool friendly = side == 0;
                if (friendly ? query.Side == TargetSide.Enemy : query.Side == TargetSide.Friendly) continue;

                int seat = friendly ? friendlySeat : enemySeat;
                foreach (Zone zone in Zones)
                {
                    if ((query.Zones & TargetAxes.OfZone(zone)) == 0) continue;
                    if (!IsVisible(zone, friendly, allowHiddenZones) && !NamesOneEntity(query)) continue;
                    AddZone(state, db, query, seat, zone, source, triggerSource, result);
                }
            }
        }

        /// <summary>Zone order is <see cref="Zone"/>'s own declaration order, taken from the enum
        /// rather than restated as a list here -- a second list is a second thing to keep true.</summary>
        private static readonly Zone[] Zones = (Zone[])Enum.GetValues(typeof(Zone));

        /// <summary>
        /// Whether a zone's contents may be enumerated for the side that owns them. Stated once as a
        /// rule over (zone, side); it used to be an <c>if (allowHiddenZones)</c> repeated inside six
        /// selector arms, which is the same fact written six times.
        ///
        /// <para>Both DECKS are hidden, not just the opponent's -- a player does not know their own
        /// draw order either. Hands are hidden only on the far side. Board, weapon and graveyard are
        /// public.</para>
        /// </summary>
        private static bool IsVisible(Zone zone, bool friendly, bool allowHiddenZones)
        {
            if (allowHiddenZones) return true;
            if (zone == Zone.Deck) return false;
            return zone != Zone.Hand || friendly;
        }

        /// <summary>
        /// Whether a query names one entity outright instead of describing criteria. Those are
        /// environment reads -- the source, or whatever raised the trigger -- and they resolve
        /// wherever that entity happens to be, including a zone the asking player cannot see.
        /// Nothing is revealed: the effect already holds the identity.
        /// </summary>
        private static bool NamesOneEntity(TargetQuery query)
            => query.Relation == SourceRelation.Self || query.Relation == SourceRelation.TriggerSource;

        private static void AddZone(GameState state, ICardDatabase db, TargetQuery query, int seat,
            Zone zone, InstanceId? source, InstanceId? triggerSource, List<TargetCandidate> result)
        {
            PlayerState player = state.Players[seat];
            if (zone == Zone.Board)
            {
                // The hero first, then the minions left to right. The hero is the one non-card, so
                // "hero before minions" is a TYPE fact and cannot come from the zone axis.
                Consider(HeroCandidate(state, seat), state, db, query, source, triggerSource, result);
                foreach (CardInstance minion in player.Board)
                    Consider(MinionCandidate(seat, minion), state, db, query, source, triggerSource, result);
                return;
            }
            if (zone == Zone.Weapon)
            {
                if (player.Weapon != null)
                    Consider(new TargetCandidate(player.Weapon.Id, player.Id, Zone.Weapon,
                            definitionId: player.Weapon.DefinitionId, card: player.Weapon, seat: seat),
                        state, db, query, source, triggerSource, result);
                return;
            }
            IReadOnlyList<CardInstance> cards = zone == Zone.Hand ? player.Hand
                : zone == Zone.Deck ? (IReadOnlyList<CardInstance>)player.Deck : player.Graveyard;
            foreach (CardInstance card in cards)
                Consider(new TargetCandidate(card.Id, player.Id, zone,
                        definitionId: card.DefinitionId, card: card, seat: seat),
                    state, db, query, source, triggerSource, result);
        }

        /// <summary>Admits one candidate if it satisfies the two axes the enumeration loop does not
        /// already decide -- type, and relation to the source.</summary>
        private static void Consider(TargetCandidate candidate, GameState state, ICardDatabase db,
            TargetQuery query, InstanceId? source, InstanceId? triggerSource, List<TargetCandidate> result)
        {
            if (!TypeMatches(query, candidate, db)) return;
            if (!RelationMatches(query, candidate, state, source, triggerSource)) return;
            result.Add(candidate);
        }

        /// <summary>
        /// The TYPE axis. Resolved through the definition, which is the only place a card's type is
        /// stored -- see <see cref="TargetCandidate.Zone"/> for why it is not copied onto the
        /// candidate.
        ///
        /// <para>The database is not consulted when every card type is admitted, because the answer
        /// cannot depend on it. That is an optimisation on the shipping path and a safety rail in
        /// tests, where <c>FakeCardDatabase</c> fabricates a MINION row for any id it was not
        /// handed.</para>
        /// </summary>
        private static bool TypeMatches(TargetQuery query, TargetCandidate candidate, ICardDatabase db)
        {
            bool isCard = candidate.DefinitionId > 0;
            if (!isCard) return (query.Types & TargetTypes.Hero) != 0;
            if ((query.Types & TargetTypes.AnyCard) == TargetTypes.AnyCard) return true;
            return db.TryGet(candidate.DefinitionId, out CardDefinition def) &&
                   (query.Types & TargetAxes.OfCard(def.Type)) != 0;
        }

        /// <summary>The SOURCE-RELATION axis. <c>Beside</c> is computed, never read off the minion --
        /// adjacency is a relation between two of them and has no value until both ends are named.</summary>
        private static bool RelationMatches(TargetQuery query, TargetCandidate candidate,
            GameState state, InstanceId? source, InstanceId? triggerSource)
        {
            switch (query.Relation)
            {
                case SourceRelation.Any:
                    return true;
                case SourceRelation.Self:
                    return source.HasValue && candidate.Instance == source.Value;
                case SourceRelation.TriggerSource:
                    return triggerSource.HasValue && candidate.Instance == triggerSource.Value;
                case SourceRelation.NotSelf:
                    return !source.HasValue || candidate.Instance != source.Value;
                case SourceRelation.Beside:
                    return IsBesideSource(state, source, candidate);
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether a candidate sits immediately left or right of the source, in the source's own row.
        ///
        /// <para><b>RETIREMENT TRIGGER — a SPARSE BOARD breaks this line and nothing will say so.</b>
        /// The comparison below is on LIST INDEX, which equals <see cref="CardInstance.Position"/>
        /// only because <c>RulesEngine.Reindex</c> re-stamps every minion after each board edit. Make
        /// <c>Position</c> authoritative and delete <c>Reindex</c> — the conversion Sam holds out of
        /// this fork's scope (2026-08-22) — and the two quantities part company: minions in slots 0
        /// and 3 with gaps between them are list-index 0 and 1, so this returns TRUE for a pair the
        /// board says is not adjacent. Compare <c>Position</c>, not index, in that change.</para>
        ///
        /// <para><b>Nothing else in the effect model presumes a dense board</b>, which was the
        /// standing constraint on the grammar work: <see cref="SourceRelation.Beside"/> is a relation
        /// computed from both ends rather than an attribute stored on a minion,
        /// <see cref="TargetQuery.MaxCandidates"/> answers 2 for it on any board shape, and the
        /// candidate order is stated as a sort over position rather than a walk of the list. This
        /// method is the one place the presumption actually landed.</para>
        /// </summary>
        private static bool IsBesideSource(GameState state, InstanceId? source, TargetCandidate candidate)
        {
            if (!source.HasValue || !Characters.TryFind(state, source.Value, out CharacterRef character) ||
                !character.IsMinion) return false;
            // Adjacency exists only within one row, so a candidate on the other side is not beside
            // anything -- "an adjacent enemy minion" has no geometry in this game.
            if (candidate.Seat != character.Seat) return false;
            List<CardInstance> board = state.Players[character.Seat].Board;
            int sourceIndex = board.IndexOf(character.Minion!);
            if (sourceIndex < 0) return false;
            int index = board.FindIndex(m => m.Id == candidate.Instance);
            return index >= 0 && Math.Abs(index - sourceIndex) == 1;
        }

        private static void AddById(GameState state, InstanceId? id, List<TargetCandidate> result)
        {
            if (id.HasValue && TryCandidate(state, id.Value, out TargetCandidate candidate)) result.Add(candidate);
        }

        private static bool TryCandidate(GameState state, InstanceId id, out TargetCandidate candidate)
        {
            if (Characters.TryFind(state, id, out CharacterRef character))
            {
                PlayerId controller = state.Players[character.Seat].Id;
                candidate = new TargetCandidate(id, controller,
                    Zone.Board, character, character.IsMinion ? character.Minion!.DefinitionId : 0,
                    card: character.Minion, seat: character.Seat);
                return true;
            }
            for (int seat = 0; seat < 2; seat++)
            {
                PlayerState player = state.Players[seat];
                if (player.Weapon != null && player.Weapon.Id == id)
                {
                    candidate = new TargetCandidate(id, player.Id,
                        Zone.Weapon, definitionId: player.Weapon.DefinitionId,
                        card: player.Weapon, seat: seat);
                    return true;
                }
                if (TryCard(player.Hand, id, player.Id, Zone.Hand, seat, out candidate) ||
                    TryCard(player.Deck, id, player.Id, Zone.Deck, seat, out candidate) ||
                    TryCard(player.Graveyard, id, player.Id, Zone.Graveyard, seat, out candidate))
                    return true;
            }
            candidate = default;
            return false;
        }

        internal static bool TryControllerSeat(GameState state, InstanceId id, out int seat)
        {
            if (TryCandidate(state, id, out TargetCandidate candidate))
            {
                seat = candidate.Seat;
                return true;
            }
            seat = -1;
            return false;
        }

        private static bool TryCard(IReadOnlyList<CardInstance> cards, InstanceId id, PlayerId controller,
            Zone zone, int seat, out TargetCandidate candidate)
        {
            foreach (CardInstance card in cards)
                if (card.Id == id)
                {
                    candidate = new TargetCandidate(id, controller, zone,
                        definitionId: card.DefinitionId, card: card, seat: seat);
                    return true;
                }
            candidate = default;
            return false;
        }

        private static TargetCandidate HeroCandidate(GameState state, int seat)
        {
            PlayerState player = state.Players[seat];
            return new TargetCandidate(player.Hero.Id, player.Id,
                Zone.Board, CharacterRef.OfHero(seat, player.Hero), seat: seat);
        }

        private static TargetCandidate MinionCandidate(int seat, CardInstance minion)
            => new TargetCandidate(minion.Id, minion.Controller,
                Zone.Board, CharacterRef.OfMinion(seat, minion), minion.DefinitionId,
                card: minion, seat: seat);
    }

    internal sealed class TargetBindingContext
    {
        private readonly Dictionary<string, HashSet<InstanceId>> _bindings = new Dictionary<string, HashSet<InstanceId>>();

        /// <summary>
        /// Whether this play's COST was paid. One flag, not a dictionary, because a cost is tied to
        /// the run adjacent to it and <c>CardValidator</c> C28 allows one per trigger — see
        /// <see cref="CostPaidCondition"/>, which carries the retirement trigger for the day that
        /// stops being true.
        ///
        /// <para>False until a cost rule resolves, which is why a card with no cost never satisfies
        /// a <c>COST_PAID</c> gate: fail-closed is the right default for a rule that says "only if
        /// the price was paid" on a card that names no price.</para>
        /// </summary>
        public bool CostPaid { get; private set; }

        public void RecordCostPaid(bool paid) => CostPaid = paid;

        public void Record(string binding, IReadOnlyList<TargetCandidate> targets)
        {
            if (binding.Length == 0) return;
            var ids = new HashSet<InstanceId>();
            foreach (TargetCandidate target in targets) ids.Add(target.Instance);
            _bindings[binding] = ids;
        }

        public void Exclude(string binding, List<TargetCandidate> candidates)
        {
            if (binding.Length == 0 || !_bindings.TryGetValue(binding, out HashSet<InstanceId>? ids)) return;
            for (int i = candidates.Count - 1; i >= 0; i--)
                if (ids.Contains(candidates[i].Instance)) candidates.RemoveAt(i);
        }
    }
}
