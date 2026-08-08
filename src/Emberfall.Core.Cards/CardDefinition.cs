using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>The runtime object categories selectors can produce.</summary>
    public enum TargetCandidateKind
    {
        Hero,
        Minion,
        Weapon,
        HandCard,
        DeckCard,
        GraveyardCard,
    }

    [System.Flags]
    public enum TargetCandidateKinds
    {
        None = 0,
        Hero = 1 << 0,
        Minion = 1 << 1,
        Weapon = 1 << 2,
        HandCard = 1 << 3,
        DeckCard = 1 << 4,
        GraveyardCard = 1 << 5,
        Any = Hero | Minion | Weapon | HandCard | DeckCard | GraveyardCard,
    }

    /// <summary>
    /// 1:1 mapping of the D1 §2 card row (dev-docs/specs/card-schema.md:5). <b>Immutable after load</b> — runtime
    /// mutable state lives on the <see cref="CardInstance"/>, never on the definition.
    /// </summary>
    public sealed class CardDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public CardType Type { get; }
        public int Cost { get; }
        public int? Attack { get; }
        public int? Health { get; }
        public int? Durability { get; }
        public CardClass Class { get; }
        public Rarity Rarity { get; }
        public IReadOnlyList<Keyword> Keywords { get; }
        public IReadOnlyList<RuleDefinition> Rules { get; }
        public string FlavorText { get; }
        public string ArtId { get; }
        public SetId SetId { get; }
        public bool Collectible { get; }
        public Tribe? Tribe { get; }
        public string FactionId { get; }

        /// <summary>
        /// Whether this row may enter a live card pool — deck generation, deck-building, booster
        /// packs, the soak's generated decks. Today this is exactly <see cref="Collectible"/>.
        /// <para><b>It is deliberately still its own property, and every</b>
        /// <c>ICardDatabase.AllCollectible</c> <b>implementation must filter on it rather than
        /// re-derive the predicate.</b> That is the whole lesson of EF-032a: the pool predicate
        /// used to be spelled out at each of the ~10 call sites, a gate was added to one meaning of
        /// it, and the gate came half-loose because the other call sites never heard about it. One
        /// named seam means the next pool restriction lands in one place instead of ten. Collapsing
        /// this to a plain <c>Collectible</c> read at each call site would re-scatter it.</para>
        /// <para>The gate it used to carry — <c>requiresEngineeringPrimitive</c>, a per-row
        /// quarantine that held a card authored against a not-yet-existing engine primitive out of
        /// every pool — was retired in NOR-224 once no row set it. The standing rule that replaced
        /// it is backend-first: don't author a card against a primitive that doesn't exist yet.
        /// The validator now rejects such a row outright (C14b) instead of offering to park it.</para>
        /// </summary>
        public bool PoolEligible => Collectible;

        public CardDefinition(
            string id, string name, CardType type, int cost,
            int? attack, int? health, int? durability,
            CardClass @class, Rarity rarity,
            IReadOnlyList<Keyword> keywords, IReadOnlyList<RuleDefinition> rules,
            string flavorText, string artId, SetId setId,
            bool collectible, Tribe? tribe, string factionId)
        {
            Id = id;
            Name = name;
            Type = type;
            Cost = cost;
            Attack = attack;
            Health = health;
            Durability = durability;
            Class = @class;
            Rarity = rarity;
            Keywords = keywords ?? System.Array.Empty<Keyword>();
            Rules = rules ?? System.Array.Empty<RuleDefinition>();
            FlavorText = flavorText ?? "";
            ArtId = artId ?? "";
            SetId = setId;
            Collectible = collectible;
            Tribe = tribe;
            FactionId = factionId ?? "";
        }
    }

    /// <summary>Who supplies the targets selected from a <see cref="TargetSpec"/> candidate set.</summary>
    public enum TargetChosenBy
    {
        PLAYER,
        RANDOM,
    }

    /// <summary>Optional predicates applied after selector candidate enumeration.</summary>
    public sealed class TargetFilterDef
    {
        public bool? Damaged { get; }
        public Tribe? Tribe { get; }

        public TargetFilterDef(bool? damaged = null, Tribe? tribe = null)
        {
            Damaged = damaged;
            Tribe = tribe;
        }
    }

    /// <summary>
    /// The complete authored <c>target</c> object. A null <see cref="ChosenBy"/> preserves the
    /// established selector defaults: <c>ANY_*</c> is chosen by the player and set/fixed selectors
    /// resolve automatically. <c>Count == -1</c> means every candidate.
    ///
    /// <para><b><see cref="Required"/> marks a target that is a COST, not an effect</b> — "sacrifice
    /// a friendly minion to play this". A card whose cost cannot be paid is unplayable; every other
    /// player-chosen target on a MINION or WEAPON fizzles when nothing legal exists, leaving the body
    /// behind (Sam, 2026-08-07). A SPELL is unplayable either way today, because a spell whose only
    /// effect fizzles is pure card-burn — so on a spell this flag records intent without changing
    /// behavior yet.</para>
    /// </summary>
    public sealed class TargetSpec
    {
        private static readonly HashSet<string> KnownSelectors = new HashSet<string>
        {
            "SELF", "FRIENDLY_MINIONS", "OTHER_FRIENDLY_MINIONS", "ENEMY_MINIONS", "ALL_MINIONS", "FRIENDLY_HERO",
            "ENEMY_HERO", "ANY_CHARACTER", "ANY_FRIENDLY", "ANY_ENEMY", "ANY_MINION",
            "ANY_FRIENDLY_MINION", "ADJACENT", "TRIGGER_SOURCE",
            "FRIENDLY_WEAPON", "ENEMY_WEAPON",
            "FRIENDLY_HAND", "ENEMY_HAND", "ANY_HAND_CARD",
            "FRIENDLY_DECK", "ENEMY_DECK", "ANY_DECK_CARD",
            "FRIENDLY_GRAVEYARD", "ENEMY_GRAVEYARD", "ANY_GRAVEYARD_CARD",
        };

        private static readonly HashSet<string> DefaultPlayerSelectors = new HashSet<string>
        {
            "ANY_CHARACTER", "ANY_FRIENDLY", "ANY_ENEMY", "ANY_MINION", "ANY_FRIENDLY_MINION",
            "ANY_HAND_CARD", "ANY_DECK_CARD", "ANY_GRAVEYARD_CARD",
        };

        public string Selector { get; }
        public TargetChosenBy? ChosenBy { get; }
        public int? Count { get; }
        public TargetFilterDef? Filter { get; }
        public string Binding { get; }
        public string DistinctFrom { get; }
        public bool Required { get; }

        public TargetSpec(string selector, TargetChosenBy? chosenBy = null, int? count = null,
            TargetFilterDef? filter = null, string? binding = null, string? distinctFrom = null,
            bool required = false)
        {
            Selector = selector;
            ChosenBy = chosenBy;
            Count = count;
            Filter = filter;
            Binding = binding ?? "";
            DistinctFrom = distinctFrom ?? "";
            Required = required;
        }

        public static bool IsKnownSelector(string selector) => KnownSelectors.Contains(selector);
        public static bool DefaultsToPlayerChoice(string selector) => DefaultPlayerSelectors.Contains(selector);
        public static bool IsPlayerChosen(TargetSpec? target)
            => target != null && (target.ChosenBy == TargetChosenBy.PLAYER ||
                (!target.ChosenBy.HasValue && DefaultsToPlayerChoice(target.Selector)));
        public static IReadOnlyCollection<string> Selectors => KnownSelectors;
    }

}
