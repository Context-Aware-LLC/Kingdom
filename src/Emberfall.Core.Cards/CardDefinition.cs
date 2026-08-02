using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
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
        /// True when this row may enter a live card pool — a generated deck, a deck the player
        /// builds, a collection view, a pack. <see cref="Collectible"/> is the author's intent;
        /// <see cref="RequiresEngineeringPrimitive"/> is the engine's veto over it, and the veto wins.
        ///
        /// Both <c>EffectInterpreter.SelectorTargets</c> and <c>CardValidator</c> C14 tell an author to
        /// "gate it behind requiresEngineeringPrimitive" when a row outruns the engine. Until EF-032a
        /// that advice was inert — the flag was parsed, validated and reported with no consumer, so the
        /// only pool filter was <see cref="Collectible"/> and a gated row still entered generated decks
        /// and still threw at runtime. This property is what makes those two messages true. Every
        /// <c>ICardDatabase.AllCollectible</c> implementation must filter on it.
        /// </summary>
        public bool IsPoolEligible => Collectible && !RequiresEngineeringPrimitive;

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
        /// <summary>
        /// Optional target selector from <c>target.selector</c> in the card JSON (dev-docs/specs/card-schema.md §5.3).
        /// When set and the player provides no explicit targets, the interpreter auto-resolves the target
        /// list via <c>SelectorTargets</c> (e.g. "ENEMY_MINIONS" for AoE sweeps, "ENEMY_HERO" for
        /// auto-targeted battlecries). Player-chosen targets always take precedence.
        /// </summary>
        public string? Selector { get; }

        public EffectDef(Trigger trigger, Action action, int? amount, int? amount2,
                         string? tokenId, Keyword? grantedKeyword, VfxEvent? vfxOverride,
                         string? selector = null, bool permanent = false, bool empty = false)
        {
            Trigger = trigger;
            Action = action;
            Amount = amount;
            Amount2 = amount2;
            TokenId = tokenId;
            GrantedKeyword = grantedKeyword;
            VfxOverride = vfxOverride;
            Selector = selector;
            Permanent = permanent;
            Empty = empty;
        }
    }
}
