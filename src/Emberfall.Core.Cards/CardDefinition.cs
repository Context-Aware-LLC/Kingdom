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
    /// The shared execution-capability relation between effect actions and target object kinds.
    /// Card validation rejects selector/action pairs with no overlap; runtime candidate resolution
    /// applies the same relation so malformed or hand-built definitions cannot reach a no-op handler.
    /// </summary>
    public static class ActionTargetCapabilities
    {
        public static bool Allows(Contracts.Action action, TargetCandidateKind kind)
            => (AllowedKinds(action) & Flag(kind)) != 0;

        public static bool HasCompatibleTarget(Contracts.Action action, string selector)
            => !ConsumesTargets(action) || (AllowedKinds(action) & SelectorKinds(selector)) != 0;

        public static bool HasCompatibleTarget(Contracts.Action action, string selector,
            CardType sourceType, Trigger trigger)
        {
            if (!ConsumesTargets(action)) return true;
            TargetCandidateKinds selected = selector == "SELF"
                ? SourceKinds(sourceType, trigger)
                : selector == "TRIGGER_SOURCE" ? TriggerSourceKinds(sourceType, trigger)
                : SelectorKinds(selector);
            return (AllowedKinds(action) & selected) != 0;
        }

        public static bool ConsumesTargets(Contracts.Action action)
            => AllowedKinds(action) != TargetCandidateKinds.None;

        public static TargetCandidateKinds AllowedKinds(Contracts.Action action)
        {
            switch (action)
            {
                case Contracts.Action.DEAL_DAMAGE:
                case Contracts.Action.HEAL:
                case Contracts.Action.FREEZE:
                    return TargetCandidateKinds.Hero | TargetCandidateKinds.Minion;
                case Contracts.Action.BUFF:
                case Contracts.Action.GIVE_KEYWORD:
                    return TargetCandidateKinds.Minion;
                case Contracts.Action.DESTROY:
                    return TargetCandidateKinds.Hero | TargetCandidateKinds.Minion | TargetCandidateKinds.Weapon;
                case Contracts.Action.RETURN_TO_HAND:
                    return TargetCandidateKinds.Minion | TargetCandidateKinds.Weapon |
                           TargetCandidateKinds.HandCard | TargetCandidateKinds.DeckCard |
                           TargetCandidateKinds.GraveyardCard;
                case Contracts.Action.SILENCE:
                case Contracts.Action.TRANSFORM:
                    return TargetCandidateKinds.Minion | TargetCandidateKinds.Weapon;
                default:
                    return TargetCandidateKinds.None;
            }
        }

        public static TargetCandidateKinds SelectorKinds(string selector)
        {
            switch (selector)
            {
                case "FRIENDLY_HERO":
                case "ENEMY_HERO":
                    return TargetCandidateKinds.Hero;
                case "FRIENDLY_MINIONS":
                case "ENEMY_MINIONS":
                case "ALL_MINIONS":
                case "ANY_MINION":
                case "ANY_FRIENDLY_MINION":
                case "ADJACENT":
                    return TargetCandidateKinds.Minion;
                case "ANY_CHARACTER":
                case "ANY_FRIENDLY":
                case "ANY_ENEMY":
                    return TargetCandidateKinds.Hero | TargetCandidateKinds.Minion;
                case "FRIENDLY_WEAPON":
                case "ENEMY_WEAPON":
                    return TargetCandidateKinds.Weapon;
                case "FRIENDLY_HAND":
                case "ENEMY_HAND":
                case "ANY_HAND_CARD":
                    return TargetCandidateKinds.HandCard;
                case "FRIENDLY_DECK":
                case "ENEMY_DECK":
                case "ANY_DECK_CARD":
                    return TargetCandidateKinds.DeckCard;
                case "FRIENDLY_GRAVEYARD":
                case "ENEMY_GRAVEYARD":
                case "ANY_GRAVEYARD_CARD":
                    return TargetCandidateKinds.GraveyardCard;
                case "SELF":
                case "TRIGGER_SOURCE":
                    return TargetCandidateKinds.Any;
                default:
                    return TargetCandidateKinds.None;
            }
        }

        private static TargetCandidateKinds Flag(TargetCandidateKind kind)
            => (TargetCandidateKinds)(1 << (int)kind);

        private static TargetCandidateKinds SourceKinds(CardType sourceType, Trigger trigger)
        {
            if (trigger == Trigger.ON_DEATH) return TargetCandidateKinds.GraveyardCard;
            switch (sourceType)
            {
                case CardType.MINION: return TargetCandidateKinds.Minion;
                case CardType.WEAPON: return TargetCandidateKinds.Weapon;
                case CardType.SPELL: return TargetCandidateKinds.GraveyardCard;
                default: return TargetCandidateKinds.None; // ephemeral hero-power sources are not target candidates
            }
        }

        private static TargetCandidateKinds TriggerSourceKinds(CardType sourceType, Trigger trigger)
        {
            switch (trigger)
            {
                case Trigger.ON_FRIENDLY_SUMMON:
                case Trigger.ON_DAMAGED:
                case Trigger.ON_HEAL:
                    return TargetCandidateKinds.Minion;
                case Trigger.ON_SPELL_CAST:
                case Trigger.ON_DEATH:
                    return TargetCandidateKinds.GraveyardCard;
                case Trigger.ON_ATTACK:
                    return TargetCandidateKinds.Weapon;
                case Trigger.ON_PLAY:
                case Trigger.ON_CAST:
                    return SourceKinds(sourceType, trigger);
                default:
                    // Turn-boundary events carry no targetable event source. New trigger kinds must
                    // be classified here before TRIGGER_SOURCE can validate against them.
                    return TargetCandidateKinds.None;
            }
        }
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
        public IReadOnlyList<EffectDef> Effects { get; }
        public string CardText { get; }
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
            IReadOnlyList<Keyword> keywords, IReadOnlyList<EffectDef> effects,
            string cardText, string flavorText, string artId, SetId setId,
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
            Effects = effects ?? System.Array.Empty<EffectDef>();
            CardText = cardText ?? "";
            FlavorText = flavorText ?? "";
            ArtId = artId ?? "";
            SetId = setId;
            Collectible = collectible;
            Tribe = tribe;
            FactionId = factionId ?? "";
            RequiresEngineeringPrimitive = requiresEngineeringPrimitive;
        }
    }

    /// <summary>
    /// One optional gate on an <see cref="EffectDef"/>. The shape is intentionally typed and extensible:
    /// each <see cref="ConditionType"/> owns the value fields it understands, while validation rejects
    /// incomplete or contextless combinations before a card can load into a match.
    /// </summary>
    public sealed class EffectConditionDef
    {
        public ConditionType Type { get; }
        public TurnOwner? TurnOwner { get; }

        public EffectConditionDef(ConditionType type, TurnOwner? turnOwner)
        {
            Type = type;
            TurnOwner = turnOwner;
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
        public static IReadOnlyCollection<string> Selectors => KnownSelectors;
    }

    /// <summary>
    /// One entry in <c>effects[]</c> (dev-docs/specs/card-schema.md §5). The interpreter (Phase B) tree-walks these;
    /// for the determinism foundation they are carried as immutable data only.
    /// </summary>
    public sealed class EffectDef
    {
        public Trigger Trigger { get; }
        public Action Action { get; }
        public int? Amount { get; }
        public int? Amount2 { get; }
        public string? TokenId { get; }       // payload.tokenId for SUMMON / ADD_TO_HAND / TRANSFORM
        public Keyword? GrantedKeyword { get; } // payload.keyword for GIVE_KEYWORD
        public VfxEvent? VfxOverride { get; }
        public EffectConditionDef? Condition { get; }
        /// <summary>
        /// <c>payload.permanent</c> for GAIN_MANA (dev-docs/specs/card-schema.md §5.2): true = gain permanent
        /// crystals (raises <c>Mana.Max</c>, capped at 10 — the Quicken / Seedling Rite ramp
        /// primitive); false/absent = this-turn temporary crystals (the Spark/Coin pattern).
        /// </summary>
        public bool Permanent { get; }
        /// <summary>
        /// <c>payload.empty</c> for GAIN_MANA with <c>permanent:true</c>: true = the new crystal
        /// arrives unfilled this turn ("Gain an empty Mana Crystal" — Wild-Growth-style ramp);
        /// false/absent = the crystal is also spendable immediately. Meaningless without Permanent.
        /// </summary>
        public bool Empty { get; }
        public TargetSpec? Target { get; }
        /// <summary>
        /// Compatibility view of <c>target.selector</c>. New consumers use <see cref="Target"/> so selection
        /// policy, count, filters and bindings cannot be discarded while reading only the selector string.
        /// </summary>
        public string? Selector => Target?.Selector;

        public EffectDef(Trigger trigger, Action action, int? amount, int? amount2,
                         string? tokenId, Keyword? grantedKeyword, VfxEvent? vfxOverride,
                         string? selector = null, bool permanent = false, bool empty = false,
                         EffectConditionDef? condition = null, TargetSpec? target = null)
        {
            Trigger = trigger;
            Action = action;
            Amount = amount;
            Amount2 = amount2;
            TokenId = tokenId;
            GrantedKeyword = grantedKeyword;
            VfxOverride = vfxOverride;
            Condition = condition;
            Target = target ?? (selector == null ? null : new TargetSpec(selector));
            Permanent = permanent;
            Empty = empty;
        }
    }
}
