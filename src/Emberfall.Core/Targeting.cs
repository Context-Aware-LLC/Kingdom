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
        public TargetCandidateKind Kind { get; }
        public Zone Zone { get; }

        internal CharacterRef Character { get; }
        internal string DefinitionId { get; }
        internal CardInstance? Card { get; }
        internal int Seat { get; }
        internal bool IsCharacter => Kind == TargetCandidateKind.Hero || Kind == TargetCandidateKind.Minion;
        internal bool IsHero => Kind == TargetCandidateKind.Hero;
        internal bool IsMinion => Kind == TargetCandidateKind.Minion;
        internal HeroInstance? Hero => Character.Hero;
        internal CardInstance? Minion => Character.Minion;

        internal TargetCandidate(InstanceId instance, PlayerId controller, TargetCandidateKind kind,
            Zone zone, CharacterRef character = default, string? definitionId = null,
            CardInstance? card = null, int seat = -1)
        {
            Instance = instance;
            Controller = controller;
            Kind = kind;
            Zone = zone;
            Character = character;
            DefinitionId = definitionId ?? "";
            Card = card;
            Seat = seat;
        }

        public bool Equals(TargetCandidate other) => Instance.Equals(other.Instance);
        public override bool Equals(object? obj) => obj is TargetCandidate other && Equals(other);
        public override int GetHashCode() => Instance.GetHashCode();
        public override string ToString() => $"{Kind}:{Instance}";
    }

    /// <summary>One ordered player-choice step exposed to headless and Unity presentation code.</summary>
    public sealed class TargetChoiceRequirement
    {
        public string Binding { get; }
        public int RequiredCount { get; }
        public IReadOnlyList<string> DistinctFrom { get; }
        public IReadOnlyList<TargetCandidate> Candidates { get; }

        internal TargetChoiceRequirement(string binding, int requiredCount,
            IReadOnlyList<string> distinctFrom, IReadOnlyList<TargetCandidate> candidates)
        {
            Binding = binding;
            RequiredCount = requiredCount;
            DistinctFrom = distinctFrom;
            Candidates = candidates;
        }
    }

    /// <summary>
    /// The one target-policy seam used by intent validation, effect resolution and client targeting.
    /// Candidate order is canonical: controller side before opponent side; heroes before board
    /// minions; board and card zones in list order.
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
                if (!byBinding.TryGetValue(spec.Binding, out ChoiceGroup? group))
                {
                    group = new ChoiceGroup(spec.Binding, required, candidates);
                    byBinding.Add(spec.Binding, group);
                    groups.Add(group);
                }
                else
                {
                    group.Intersect(candidates);
                    if (group.RequiredCount != required) group.RequiredCount = int.MaxValue;
                }
                group.AddDistinctFrom(spec.DistinctFrom);
            }

            var result = new List<TargetChoiceRequirement>(groups.Count);
            foreach (ChoiceGroup group in groups)
                result.Add(new TargetChoiceRequirement(group.Binding, group.RequiredCount,
                    group.DistinctFrom, group.Candidates));
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
            public readonly List<string> DistinctFrom = new List<string>();
            public readonly List<TargetCandidate> Candidates;

            public ChoiceGroup(string binding, int requiredCount,
                IReadOnlyList<TargetCandidate> candidates)
            {
                Binding = binding;
                RequiredCount = requiredCount;
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
            int enemy = friendly == 0 ? 1 : 0;
            var result = new List<TargetCandidate>();

            switch (spec.Selector)
            {
                case "SELF": AddById(state, source, result); break;
                case "TRIGGER_SOURCE": AddById(state, triggerSource, result); break;
                case "ADJACENT": AddAdjacent(state, source, result); break;
                case "FRIENDLY_HERO": AddHero(state, friendly, result); break;
                case "ENEMY_HERO": AddHero(state, enemy, result); break;
                case "FRIENDLY_MINIONS": AddMinions(state, friendly, result); break;
                case "OTHER_FRIENDLY_MINIONS":
                    AddMinions(state, friendly, result);
                    if (source.HasValue) result.RemoveAll(c => c.Instance == source.Value);
                    break;
                case "ENEMY_MINIONS": AddMinions(state, enemy, result); break;
                case "ALL_MINIONS": AddMinions(state, friendly, result); AddMinions(state, enemy, result); break;
                case "ANY_CHARACTER": AddCharacters(state, friendly, result); AddCharacters(state, enemy, result); break;
                case "ANY_FRIENDLY": AddCharacters(state, friendly, result); break;
                case "ANY_ENEMY": AddCharacters(state, enemy, result); break;
                case "ANY_MINION": AddMinions(state, friendly, result); AddMinions(state, enemy, result); break;
                case "ANY_FRIENDLY_MINION": AddMinions(state, friendly, result); break;
                case "FRIENDLY_WEAPON": AddWeapon(state, friendly, result); break;
                case "ENEMY_WEAPON": AddWeapon(state, enemy, result); break;
                case "FRIENDLY_HAND": AddCards(state, friendly, Zone.Hand, result); break;
                case "ENEMY_HAND": if (allowHiddenZones) AddCards(state, enemy, Zone.Hand, result); break;
                case "ANY_HAND_CARD":
                    AddCards(state, friendly, Zone.Hand, result);
                    if (allowHiddenZones) AddCards(state, enemy, Zone.Hand, result);
                    break;
                case "FRIENDLY_DECK": if (allowHiddenZones) AddCards(state, friendly, Zone.Deck, result); break;
                case "ENEMY_DECK": if (allowHiddenZones) AddCards(state, enemy, Zone.Deck, result); break;
                case "ANY_DECK_CARD":
                    if (allowHiddenZones)
                    {
                        AddCards(state, friendly, Zone.Deck, result);
                        AddCards(state, enemy, Zone.Deck, result);
                    }
                    break;
                case "FRIENDLY_GRAVEYARD": AddCards(state, friendly, Zone.Graveyard, result); break;
                case "ENEMY_GRAVEYARD": AddCards(state, enemy, Zone.Graveyard, result); break;
                case "ANY_GRAVEYARD_CARD": AddCards(state, friendly, Zone.Graveyard, result); AddCards(state, enemy, Zone.Graveyard, result); break;
                default: throw new InvalidOperationException($"Unknown target selector '{spec.Selector}'.");
            }

            for (int i = result.Count - 1; i >= 0; i--)
            {
                TargetCandidate candidate = result[i];
                if (source.HasValue && candidate.Kind == TargetCandidateKind.HandCard &&
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
                if (action != null && !RuleActionCapabilities.Allows(action, candidate.Kind))
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
                if (selected.Count != required || !Distinct(selected)) return false;
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
                selected = ChoiceCandidates(state, choices, spec.Binding, rule.Action);
            }
            else
            {
                var candidates = new List<TargetCandidate>(ResolutionCandidates(state, db,
                    source.Controller, spec, rule.Action, source.Identity, triggerSource));
                bindings.Exclude(spec.DistinctFrom, candidates);
                int count = RequiredAutomaticCount(spec, candidates.Count);
                if (spec.ChosenBy == TargetChosenBy.RANDOM)
                {
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
                    selected = count == candidates.Count ? candidates : candidates.GetRange(0, count);
                }
            }

            bindings.Record(spec.Binding, selected);
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
        /// <para><b><see cref="TargetSpec.Required"/> opts back out of the fizzle</b>, for a target
        /// that is a COST rather than an effect — "sacrifice a friendly minion". An unpayable cost is
        /// unplayable on every card type, body or not.</para>
        ///
        /// <para>Clamping here mirrors <see cref="RequiredAutomaticCount"/>, which has always taken
        /// as many candidates as exist. The two paths disagreeing is what produced the bug.</para>
        /// </summary>
        private static int RequiredPlayerCount(TargetSpec spec, CardType sourceType, int candidateCount)
        {
            if (spec.Count == -1) return candidateCount;
            int authored = spec.Count ?? 1;
            return CanFizzle(spec, sourceType) ? Math.Min(authored, candidateCount) : authored;
        }

        private static bool CanFizzle(TargetSpec spec, CardType sourceType)
            => !spec.Required && (sourceType == CardType.MINION || sourceType == CardType.WEAPON);

        private static int RequiredAutomaticCount(TargetSpec spec, int candidateCount)
        {
            if (spec.Count == -1) return candidateCount;
            if (spec.Count.HasValue) return Math.Min(spec.Count.Value, candidateCount);
            return spec.ChosenBy == TargetChosenBy.RANDOM ? Math.Min(1, candidateCount) : candidateCount;
        }

        private static IReadOnlyList<TargetCandidate> ChoiceCandidates(GameState state,
            IReadOnlyList<TargetRef> choices, string binding, RuleAction action)
        {
            var result = new List<TargetCandidate>();
            foreach (TargetRef choice in choices)
                if (choice.Binding == binding && TryCandidate(state, choice.Instance, out TargetCandidate candidate) &&
                    RuleActionCapabilities.Allows(action, candidate.Kind))
                    result.Add(candidate);
            return result;
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
                if (candidate.DefinitionId.Length == 0 ||
                    !db.TryGet(candidate.DefinitionId, out CardDefinition def) ||
                    def.Tribe != filter.Tribe.Value) return false;
            }
            return true;
        }

        private static bool IsHiddenEnemyMinion(GameState state, int friendlySeat, TargetCandidate candidate)
            => candidate.Kind == TargetCandidateKind.Minion &&
               candidate.Character.Seat != friendlySeat &&
               candidate.Character.Minion!.Keywords.Contains(Keyword.SHROUD);

        private static void AddCharacters(GameState state, int seat, List<TargetCandidate> result)
        {
            AddHero(state, seat, result);
            AddMinions(state, seat, result);
        }

        private static void AddHero(GameState state, int seat, List<TargetCandidate> result)
        {
            PlayerState player = state.Players[seat];
            result.Add(HeroCandidate(state, seat));
        }

        private static void AddMinions(GameState state, int seat, List<TargetCandidate> result)
        {
            PlayerState player = state.Players[seat];
            foreach (CardInstance minion in player.Board)
                result.Add(MinionCandidate(seat, minion));
        }

        private static void AddWeapon(GameState state, int seat, List<TargetCandidate> result)
        {
            PlayerState player = state.Players[seat];
            if (player.Weapon != null)
                result.Add(new TargetCandidate(player.Weapon.Id, player.Id, TargetCandidateKind.Weapon,
                    Zone.Weapon, definitionId: player.Weapon.DefinitionId, card: player.Weapon, seat: seat));
        }

        private static void AddCards(GameState state, int seat, Zone zone, List<TargetCandidate> result)
        {
            PlayerState player = state.Players[seat];
            IReadOnlyList<CardInstance> cards = zone == Zone.Hand ? player.Hand
                : zone == Zone.Deck ? player.Deck : player.Graveyard;
            TargetCandidateKind kind = zone == Zone.Hand ? TargetCandidateKind.HandCard
                : zone == Zone.Deck ? TargetCandidateKind.DeckCard : TargetCandidateKind.GraveyardCard;
            foreach (CardInstance card in cards)
                result.Add(new TargetCandidate(card.Id, player.Id, kind, zone,
                    definitionId: card.DefinitionId, card: card, seat: seat));
        }

        private static void AddAdjacent(GameState state, InstanceId? source, List<TargetCandidate> result)
        {
            if (!source.HasValue || !Characters.TryFind(state, source.Value, out CharacterRef character) ||
                !character.IsMinion) return;
            List<CardInstance> board = state.Players[character.Seat].Board;
            int index = board.IndexOf(character.Minion!);
            if (index > 0)
            {
                CardInstance left = board[index - 1];
                result.Add(MinionCandidate(character.Seat, left));
            }
            if (index >= 0 && index + 1 < board.Count)
            {
                CardInstance right = board[index + 1];
                result.Add(MinionCandidate(character.Seat, right));
            }
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
                    character.IsHero ? TargetCandidateKind.Hero : TargetCandidateKind.Minion,
                    Zone.Board, character, character.IsMinion ? character.Minion!.DefinitionId : null,
                    card: character.Minion, seat: character.Seat);
                return true;
            }
            for (int seat = 0; seat < 2; seat++)
            {
                PlayerState player = state.Players[seat];
                if (player.Weapon != null && player.Weapon.Id == id)
                {
                    candidate = new TargetCandidate(id, player.Id, TargetCandidateKind.Weapon,
                        Zone.Weapon, definitionId: player.Weapon.DefinitionId,
                        card: player.Weapon, seat: seat);
                    return true;
                }
                if (TryCard(player.Hand, id, player.Id, TargetCandidateKind.HandCard, Zone.Hand, seat, out candidate) ||
                    TryCard(player.Deck, id, player.Id, TargetCandidateKind.DeckCard, Zone.Deck, seat, out candidate) ||
                    TryCard(player.Graveyard, id, player.Id, TargetCandidateKind.GraveyardCard, Zone.Graveyard, seat, out candidate))
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
            TargetCandidateKind kind, Zone zone, int seat, out TargetCandidate candidate)
        {
            foreach (CardInstance card in cards)
                if (card.Id == id)
                {
                    candidate = new TargetCandidate(id, controller, kind, zone,
                        definitionId: card.DefinitionId, card: card, seat: seat);
                    return true;
                }
            candidate = default;
            return false;
        }

        private static TargetCandidate HeroCandidate(GameState state, int seat)
        {
            PlayerState player = state.Players[seat];
            return new TargetCandidate(player.Hero.Id, player.Id, TargetCandidateKind.Hero,
                Zone.Board, CharacterRef.OfHero(seat, player.Hero), seat: seat);
        }

        private static TargetCandidate MinionCandidate(int seat, CardInstance minion)
            => new TargetCandidate(minion.Id, minion.Controller, TargetCandidateKind.Minion,
                Zone.Board, CharacterRef.OfMinion(seat, minion), minion.DefinitionId,
                card: minion, seat: seat);
    }

    internal sealed class TargetBindingContext
    {
        private readonly Dictionary<string, HashSet<InstanceId>> _bindings = new Dictionary<string, HashSet<InstanceId>>();

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
