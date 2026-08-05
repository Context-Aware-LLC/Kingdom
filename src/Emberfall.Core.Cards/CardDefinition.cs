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
        public bool RequiresEngineeringPrimitive { get; }

        /// <summary>
        /// Whether this row may enter a live card pool — deck generation, deck-building, booster
        /// packs, the soak's generated decks. <b>This is the whole of the</b>
        /// <c>requiresEngineeringPrimitive</c> <b>gate</b>: a row that declares grammar the engine
        /// has not grown yet is authored, parsed, validated and shipped in the set file, but is held
        /// out of every pool until the primitive lands, so it can never be drawn and never fire.
        /// <para>Until EF-032a this flag gated NOTHING at runtime. Both <c>EffectInterpreter</c> and
        /// <c>CardValidator</c> C14 told authors to "gate it behind requiresEngineeringPrimitive",
        /// and every consumer filtered on <see cref="Collectible"/> alone — so following that advice
        /// bought a green validator and an identical crash. Every <c>ICardDatabase.AllCollectible</c>
        /// implementation now reads THIS property rather than re-deriving the predicate, so the gate
        /// cannot come half-loose again.</para>
        /// <para><b>RETIREMENT TRIGGER (Sam, 2026-08-03):</b> this flag is a transitional mechanism
        /// for clearing the backlog of cards authored before the engine caught up, not a standing
        /// pattern. Going forward the rule is backend-first: don't author a card against a primitive
        /// that doesn't exist yet. When no live row sets this flag anymore, retire it — delete this
        /// property, <see cref="RequiresEngineeringPrimitive"/>, every <c>AllCollectible</c> call site
        /// that reads it, C14b in <c>CardValidator</c>, and the held-row branch in
        /// <c>scan_unresolvable_trigger_selectors.py</c> — and fold <see cref="PoolEligible"/> back
        /// down to a plain read of <see cref="Collectible"/>.</para>
        /// </summary>
        public bool PoolEligible => Collectible && !RequiresEngineeringPrimitive;

        public CardDefinition(
            string id, string name, CardType type, int cost,
            int? attack, int? health, int? durability,
            CardClass @class, Rarity rarity,
            IReadOnlyList<Keyword> keywords, IReadOnlyList<RuleDefinition> rules,
            string flavorText, string artId, SetId setId,
            bool collectible, Tribe? tribe, string factionId, bool requiresEngineeringPrimitive)
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
            RequiresEngineeringPrimitive = requiresEngineeringPrimitive;
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
    /// </summary>
    public sealed class TargetSpec
    {
        private static readonly HashSet<string> KnownSelectors = new HashSet<string>
        {
            "SELF", "FRIENDLY_MINIONS", "ENEMY_MINIONS", "ALL_MINIONS", "FRIENDLY_HERO",
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

        public TargetSpec(string selector, TargetChosenBy? chosenBy = null, int? count = null,
            TargetFilterDef? filter = null, string? binding = null, string? distinctFrom = null)
        {
            Selector = selector;
            ChosenBy = chosenBy;
            Count = count;
            Filter = filter;
            Binding = binding ?? "";
            DistinctFrom = distinctFrom ?? "";
        }

        public static bool IsKnownSelector(string selector) => KnownSelectors.Contains(selector);
        public static bool DefaultsToPlayerChoice(string selector) => DefaultPlayerSelectors.Contains(selector);
        public static bool IsPlayerChosen(TargetSpec? target)
            => target != null && (target.ChosenBy == TargetChosenBy.PLAYER ||
                (!target.ChosenBy.HasValue && DefaultsToPlayerChoice(target.Selector)));
        public static IReadOnlyCollection<string> Selectors => KnownSelectors;
    }

}
