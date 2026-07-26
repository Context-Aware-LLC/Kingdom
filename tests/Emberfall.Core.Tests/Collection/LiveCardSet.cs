using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The <b>live</b> M1 card set — <c>unity/Emberfall/Assets/CardData/M1.cards.json</c>, the same
    /// rows the game ships — loaded for the deck-legality tests.
    ///
    /// <para>Deliberately not the pinned golden-replay snapshot. Those two files answer different
    /// questions: the snapshot exists so a rebalance cannot break the R1 determinism gate, whereas
    /// deck legality is exactly the thing that <i>should</i> be re-checked against the real pool. A
    /// class whose collectible cards fall below a legal 30 is a content bug these tests are supposed
    /// to catch, not insulate.</para>
    ///
    /// <para>Nothing here hardcodes a card id. Ids are picked by predicate off the live set, so
    /// today's balance patch renaming a card does not turn into a red test tomorrow.</para>
    /// </summary>
    internal static class LiveCardSet
    {
        private static IReadOnlyList<CardDefinition>? _cards;
        private static JsonCardDatabase? _db;

        internal static IReadOnlyList<CardDefinition> Cards => _cards ??= Load();

        internal static ICardDatabase Db
        {
            get
            {
                if (_db == null) _db = new JsonCardDatabase(Cards);
                return _db;
            }
        }

        private static IReadOnlyList<CardDefinition> Load()
        {
            string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", "M1.cards.json");
            Assert.That(File.Exists(path), Is.True, $"Live card set not found at {path}");
            return CardJson.DeserializeArray(File.ReadAllText(path));
        }

        /// <summary>Collectible cards of a class, in file order (deck-buildable rows only — no tokens, no hero powers).</summary>
        internal static List<CardDefinition> Collectible(CardClass cardClass)
        {
            var list = new List<CardDefinition>();
            foreach (var c in Cards)
                if (c.Collectible && c.Class == cardClass) list.Add(c);
            return list;
        }

        /// <summary>The first collectible card of a class — a stable "give me a real card of X" for a fixture.</summary>
        internal static CardDefinition AnyCollectible(CardClass cardClass)
        {
            var list = Collectible(cardClass);
            Assert.That(list, Is.Not.Empty, $"Live set has no collectible {cardClass} card to build a fixture from.");
            return list[0];
        }

        /// <summary>The first collectible card of a class at a given rarity (used to reach a LEGENDARY for L3).</summary>
        internal static CardDefinition AnyCollectible(CardClass cardClass, Rarity rarity)
        {
            foreach (var c in Cards)
                if (c.Collectible && c.Class == cardClass && c.Rarity == rarity) return c;
            Assert.Fail($"Live set has no collectible {rarity} {cardClass} card.");
            return null!;
        }
    }
}
