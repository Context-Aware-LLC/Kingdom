using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <c>ICardDatabase.AllCollectible</c> hands back exactly the <c>PoolEligible</c> rows, and a
    /// row it omits stays resolvable by id.
    ///
    /// <para><b>Renamed from <c>EngineeringPrimitiveGateTests</c> in NOR-224</b>, which retired the
    /// <c>requiresEngineeringPrimitive</c> flag these tests used as their excluded row. The flag is
    /// gone; the contract it exposed is not, and is what actually needed defending. <c>collectible:
    /// false</c> now plays the excluded row.</para>
    ///
    /// <para>The defect this fixture came from (EF-032a): the pool predicate was spelled out
    /// independently at every <c>AllCollectible</c> implementation, a restriction was added to one
    /// meaning of it, and the other implementations never heard about it — so a row that was
    /// supposed to be held back still entered generated decks and still threw mid-match. That is
    /// why <see cref="CardDefinition.PoolEligible"/> stays a single named property even now that it
    /// reads <c>Collectible</c> and nothing else, and why these tests exercise the contract through
    /// a database that implements it rather than a double that agrees by construction.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class PoolEligibilityContractTests
    {
        [Test]
        public void Pool_Eligibility_Is_Collectible_Today()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Card(FixtureCard.Id("plain"), collectible: true).PoolEligible, Is.True,
                    "an ordinary collectible row is pool-eligible.");
                Assert.That(Card(FixtureCard.Id("token"), collectible: false).PoolEligible, Is.False,
                    "collectible:false excludes.");
            });
        }

        [Test]
        public void Excluded_Row_Is_Absent_From_The_Pool_But_Still_Resolvable_By_Id()
        {
            var db = new PoolDatabase(new[]
            {
                Card(FixtureCard.Id("keep"), collectible: true),
                Card(FixtureCard.Id("held"), collectible: false),
            });

            Assert.That(db.AllCollectible().Select(d => d.Id), Is.EquivalentTo(new[] { FixtureCard.Id("keep") }),
                "a non-pool-eligible row must not reach a pool.");

            Assert.That(db.TryGet(FixtureCard.Id("held"), out var held), Is.True,
                "but it must stay resolvable — tokens, replays and already-saved decks reference it by id.");
            Assert.That(held.Id, Is.EqualTo(FixtureCard.Id("held")));
        }

        [Test]
        public void Excluded_Row_Never_Enters_A_Generated_Deck()
        {
            // A pool wide enough to build 30 cards, in which exactly one row is excluded.
            var rows = new List<CardDefinition>();
            for (int i = 0; i < 40; i++)
                rows.Add(Card(FixtureCard.Id($"ok_{i:D2}"), collectible: true));
            rows.Add(Card(FixtureCard.Id("excluded_row"), collectible: false));

            var db = new PoolDatabase(rows);

            // A real pairing, because a generated deck must have one (ADR-0009 L5) — the rows above
            // are all NEUTRAL, so which two classes these are does not change what is drawn.
            var deck = DefaultDeckGenerator.Generate(
                CardClass.GRAVEKEEP, CardClass.VERDANT, db, new DeterministicRandom(1234), "d");

            Assert.That(deck.TotalCount, Is.EqualTo(30), "sanity: the pool was wide enough to build.");
            Assert.That(deck.CountOf(FixtureCard.Id("excluded_row")), Is.Zero,
                "the deck generator draws from AllCollectible, so the predicate must reach it.");
        }

        [Test]
        public void Live_Set_Pool_Is_Exactly_Its_Collectible_Rows()
        {
            // The witness that no implementation has quietly re-derived the predicate: what the
            // live database calls a pool matches what PoolEligible says it is, row for row.
            var pool = LiveCardSet.Db.AllCollectible().ToList();
            Assert.That(pool.Where(d => !d.PoolEligible), Is.Empty,
                "AllCollectible must never hand back a row that is not pool-eligible.");
            Assert.That(pool, Is.Not.Empty, "sanity: the live set has a pool at all.");
        }

        private static CardDefinition Card(int id, bool collectible)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, cost: 1, attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
                flavorText: "", artId: "", setId: SetId.CORE,
                collectible: collectible, tribe: null, factionId: "");

        /// <summary>
        /// A database that honours the <see cref="ICardDatabase.AllCollectible"/> contract, so these
        /// tests exercise the contract rather than a double that agrees with them by construction.
        /// </summary>
        private sealed class PoolDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _byId = new();
            public PoolDatabase(IEnumerable<CardDefinition> defs) { foreach (var d in defs) _byId[d.Id] = d; }
            public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(int cardId) => _byId[cardId];
            public IReadOnlyCollection<CardDefinition> AllCollectible()
                => _byId.Values.Where(d => d.PoolEligible).ToList();
        }
    }
}
