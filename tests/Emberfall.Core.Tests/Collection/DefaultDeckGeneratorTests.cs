using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="DefaultDeckGenerator"/> — the placeholder starter deck a new Character opens on
    /// (Sam, 2026-07-26), against the live M1 pool.
    ///
    /// <para>Three properties are the whole contract and each is asserted rather than assumed: the
    /// output passes L1-L5, the curve is actually biased cheap, and the same seed produces the same
    /// deck. The fourth — a thin pool fails loudly — is the one that cannot be provoked from the live
    /// set today, so it gets a deliberately starved database.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DefaultDeckGeneratorTests
    {
        private static Character Rotbloom() => new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);
        private static DeterministicRandom Rng(ulong seed = 1234) => new DeterministicRandom(seed);

        /// <summary>Every pairing the live set can express, so no class combination is silently untested.</summary>
        private static IEnumerable<Character> AllPairings()
        {
            var classes = Enum.GetValues(typeof(CardClass)).Cast<CardClass>().Where(c => c != CardClass.NEUTRAL);
            foreach (var p in classes)
                foreach (var s in classes)
                    if (p != s) yield return new Character($"chr-{p}-{s}", $"{p}/{s}", p, s);
        }

        [Test]
        public void Every_Pairing_Generates_A_Deck_That_Passes_L1_Through_L5()
        {
            foreach (var character in AllPairings())
            {
                var deck = DefaultDeckGenerator.Generate(character, LiveCardSet.Db, Rng(), "deck-1");
                var result = DeckValidator.Validate(deck, character, LiveCardSet.Db);

                Assert.That(deck.TotalCount, Is.EqualTo(DeckValidator.DeckSize), character.Name);
                Assert.That(result.IsValid, Is.True,
                    $"{character.Name}: " + string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Message}")));
                Assert.That(deck.CharacterId, Is.EqualTo(character.Id), "L5 binding is set by the generator.");
            }
        }

        [Test]
        public void Generated_Cards_Are_Collectible_And_Inside_The_Pairing()
        {
            // L4 passing is not quite enough: tokens and hero-power rows are NEUTRAL, so a generator
            // that ignored Collectible would satisfy L4 while dealing the player a Spark Coin.
            var character = Rotbloom();
            var deck = DefaultDeckGenerator.Generate(character, LiveCardSet.Db, Rng(), "deck-1");

            foreach (var entry in deck.Entries)
            {
                var def = LiveCardSet.Db.Get(entry.CardId);
                Assert.That(def.Collectible, Is.True, $"{def.Id} is not a collectible card.");
                Assert.That(character.AllowsClass(def.Class), Is.True, $"{def.Id} is {def.Class}.");
                Assert.That(entry.Count, Is.InRange(1, DeckValidator.MaxCopies(def.Rarity)), def.Id.ToString());
            }
        }

        [Test]
        public void The_Same_Seed_Produces_The_Same_Deck()
        {
            var character = Rotbloom();

            var a = DefaultDeckGenerator.Generate(character, LiveCardSet.Db, Rng(99), "deck-1");
            var b = DefaultDeckGenerator.Generate(character, LiveCardSet.Db, Rng(99), "deck-1");

            Assert.That(b.Entries.Select(e => (e.CardId, e.Count)),
                Is.EqualTo(a.Entries.Select(e => (e.CardId, e.Count))),
                "Entry order is candidate order, not draw order, so the same seed writes an identical file.");
        }

        [Test]
        public void Different_Seeds_Produce_Different_Decks()
        {
            // Guards the boring failure where the weighting collapses and every deck is the same 30.
            var character = Rotbloom();

            var a = DefaultDeckGenerator.Generate(character, LiveCardSet.Db, Rng(1), "deck-1");
            var b = DefaultDeckGenerator.Generate(character, LiveCardSet.Db, Rng(2), "deck-1");

            Assert.That(b.Entries.Select(e => (e.CardId, e.Count)),
                Is.Not.EqualTo(a.Entries.Select(e => (e.CardId, e.Count))));
        }

        [Test]
        public void Entries_Are_Ordered_And_Never_Duplicated()
        {
            var deck = DefaultDeckGenerator.Generate(Rotbloom(), LiveCardSet.Db, Rng(), "deck-1");
            var ids = deck.Entries.Select(e => e.CardId).ToList();

            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count),
                "Copies live in DeckEntry.Count — a second row for the same id would dodge the L2/L3 cap.");
            Assert.That(ids, Is.Ordered.Using((System.Collections.IComparer)StringComparer.Ordinal));
        }

        // ---- the curve -----------------------------------------------------------------

        [Test]
        public void The_Draw_Is_Weighted_Toward_Cheap_Cards()
        {
            // The property that makes the deck playable. Measured against the pool it was drawn from,
            // because "average cost under N" would just be a fact about M1's costs, not about the
            // weighting. Averages are scaled by 100 to stay integer — this assembly is float-free.
            var character = Rotbloom();
            var pool = LiveCardSet.Cards.Where(c => c.Collectible && character.AllowsClass(c.Class)).ToList();
            int poolAvg100 = pool.Sum(c => c.Cost) * 100 / pool.Count;

            var costs = new List<int>();
            for (ulong seed = 0; seed < 25; seed++)
            {
                var deck = DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(seed), "d");
                foreach (var e in deck.Entries)
                    for (int i = 0; i < e.Count; i++) costs.Add(LiveCardSet.Db.Get(e.CardId).Cost);
            }
            int deckAvg100 = costs.Sum() * 100 / costs.Count;

            Assert.That(deckAvg100, Is.LessThan(poolAvg100),
                $"Deck average {deckAvg100 / 100.0} is not cheaper than the pool's {poolAvg100 / 100.0} — " +
                "a uniform draw would produce a curve of sevens.");
            Assert.That(costs.Count(c => c <= 3) * 100 / costs.Count, Is.GreaterThan(40),
                "A playable opening needs early plays, not just a lower mean.");
        }

        [Test]
        public void Weight_Falls_With_Cost_And_Never_Reaches_Zero()
        {
            // A zero weight would make a card unreachable, and a pool that needs its expensive cards
            // to reach 30 would then loop forever rather than fail.
            for (int cost = 0; cost < 12; cost++)
            {
                Assert.That(DefaultDeckGenerator.Weight(cost), Is.GreaterThan(0), $"cost {cost}");
                if (cost > 0 && cost < 8)
                    Assert.That(DefaultDeckGenerator.Weight(cost), Is.LessThan(DefaultDeckGenerator.Weight(cost - 1)));
            }
        }

        // ---- the thin pool -------------------------------------------------------------

        [Test]
        public void A_Pool_That_Cannot_Reach_Thirty_Fails_Loudly()
        {
            // Sam's constraint: never hand back a 27-card deck that dies on save. Not provokable from
            // the live set — the thinnest real pairing is EMBERWARD/TIDECALLER, and it still clears
            // 30 because every character also gets NEUTRAL — so the pool is starved deliberately.
            var character = Rotbloom();
            var starved = new FixedCardDatabase(
                LiveCardSet.Collectible(CardClass.GRAVEKEEP).Take(5));   // 5 rows, at most 10 copies

            Assert.That(DefaultDeckGenerator.CanGenerate(character, starved), Is.False);

            var ex = Assert.Throws<DeckGenerationException>(
                () => DefaultDeckGenerator.Generate(character, starved, Rng(), "deck-1"));
            Assert.That(ex!.Message, Does.Contain("30"));
        }

        [Test]
        public void CanGenerate_Is_True_For_Every_Real_Pairing_Today()
        {
            // Doubles as a content tripwire: if a future set edit drops a class below a buildable
            // pool, this fails here rather than at a player's Create button.
            foreach (var character in AllPairings())
                Assert.That(DefaultDeckGenerator.CanGenerate(character, LiveCardSet.Db), Is.True, character.Name);
        }

        [Test]
        public void Generate_Refuses_Arguments_It_Cannot_Honour()
        {
            var c = Rotbloom();
            Assert.Throws<ArgumentNullException>(() => DefaultDeckGenerator.Generate(null!, LiveCardSet.Db, Rng(), "d"));
            Assert.Throws<ArgumentNullException>(() => DefaultDeckGenerator.Generate(c, null!, Rng(), "d"));
            Assert.Throws<ArgumentNullException>(() => DefaultDeckGenerator.Generate(c, LiveCardSet.Db, null!, "d"));
            Assert.Throws<ArgumentException>(() => DefaultDeckGenerator.Generate(c, LiveCardSet.Db, Rng(), ""));
        }

        /// <summary>An <see cref="ICardDatabase"/> over an explicit row list, for starving the pool on purpose.</summary>
        private sealed class FixedCardDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _byId = new();
            public FixedCardDatabase(IEnumerable<CardDefinition> defs) { foreach (var d in defs) _byId[d.Id] = d; }
            public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(int cardId) => _byId[cardId];
            public IReadOnlyCollection<CardDefinition> AllCollectible()
                => _byId.Values.Where(d => d.PoolEligible).ToList();
        }
    }
}
