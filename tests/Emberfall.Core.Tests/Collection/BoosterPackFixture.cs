using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// Synthetic card pools for the booster-pack fixtures (NOR-109 §7).
    ///
    /// <para><b>Synthetic on purpose, unlike <see cref="LiveCardSet"/>.</b> These fixtures pin the
    /// <i>algorithm</i> — odds, pity boundaries, dupe protection — and the algorithm must not change
    /// when a designer adds a card. A pool built here has a stated shape, so "every Common is maxed" is
    /// a two-line setup rather than a fact about today's Emberfall set that a content patch can quietly
    /// falsify. <see cref="LiveCardSetPackTests"/> covers the other half: that the real pools can
    /// actually honour what the algorithm promises.</para>
    /// </summary>
    internal static class BoosterPackFixture
    {
        /// <summary>The mnemonic for slot <paramref name="i"/> of a rarity — stable, sortable, greppable.</summary>
        internal static string Handle(SetId set, Rarity rarity, int i)
            => $"{set.ToString().ToLowerInvariant()}_{rarity.ToString().ToLowerInvariant()}_{i:000}";

        /// <summary>The numeric id that mnemonic mints to.</summary>
        internal static int CardId(SetId set, Rarity rarity, int i) => FixtureCard.Id(Handle(set, rarity, i));

        internal static CardDefinition Card(SetId set, Rarity rarity, int i) => new CardDefinition(
            id: CardId(set, rarity, i), handle: Handle(set, rarity, i),
            name: Handle(set, rarity, i), type: CardType.MINION, cost: 1,
            attack: 1, health: 1, durability: null,
            @class: CardClass.NEUTRAL, rarity: rarity,
            keywords: System.Array.Empty<Keyword>(), rules: System.Array.Empty<RuleDefinition>(),
            flavorText: "", artId: "art/" + Handle(set, rarity, i), setId: set,
            collectible: true, tribe: null, factionId: "");

        /// <summary>
        /// A pool with <paramref name="perRarity"/> cards of every rarity, split across CORE and the
        /// expansion so the pairing itself is exercised rather than assumed.
        /// </summary>
        internal static List<CardDefinition> Pool(SetId expansion, int perRarity)
        {
            var cards = new List<CardDefinition>();
            foreach (Rarity rarity in new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY })
                for (int i = 0; i < perRarity; i++)
                    cards.Add(Card(i % 4 == 0 ? SetId.CORE : expansion, rarity, i));
            return cards;
        }

        /// <summary>A pool holding only the given cards — for the degenerate-shape fixtures.</summary>
        internal static ICardDatabase Db(IEnumerable<CardDefinition> cards) => new JsonCardDatabase(cards);

        /// <summary>A big, deterministic pool as an <see cref="ICardDatabase"/>.</summary>
        internal static ICardDatabase Db(SetId expansion, int perRarity) => Db(Pool(expansion, perRarity));

        /// <summary>Everything in <paramref name="pool"/> owned at its copy limit — the all-dupes state.</summary>
        internal static OwnedCardCounts FullyOwned(IEnumerable<CardDefinition> pool)
        {
            var owned = new OwnedCardCounts();
            foreach (var card in pool) owned.Set(card.Id, DeckValidator.MaxCopies(card.Rarity));
            return owned;
        }
    }
}
