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
    /// EF-032a — <c>requiresEngineeringPrimitive</c> actually holds a row out of every live pool.
    ///
    /// <para>Both <c>EffectInterpreter.SelectorTargets</c> and <c>CardValidator</c> C14 instruct an
    /// author to "gate it behind requiresEngineeringPrimitive" when a row declares grammar the engine
    /// cannot resolve. Before this fixture that advice was inert: the flag was parsed, validated and
    /// reported, but no pool consumed it, so a gated row still entered generated decks and still threw
    /// mid-match. Following the error message produced a green validator and an identical crash.</para>
    ///
    /// <para>These tests are the consumer. They fail if the flag is ever quietly dropped back to a
    /// report-only field.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class EngineeringPrimitiveGateTests
    {
        [Test]
        public void Flag_Vetoes_Collectible_On_The_Shared_Predicate()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Card("plain", collectible: true, gated: false).IsPoolEligible, Is.True,
                    "an ordinary collectible row is pool-eligible.");
                Assert.That(Card("gated", collectible: true, gated: true).IsPoolEligible, Is.False,
                    "the engine's veto beats the author's collectible:true — this is the whole point of the flag.");
                Assert.That(Card("token", collectible: false, gated: false).IsPoolEligible, Is.False,
                    "collectible:false still excludes, unchanged.");
                Assert.That(Card("both", collectible: false, gated: true).IsPoolEligible, Is.False);
            });
        }

        [Test]
        public void Gated_Row_Is_Absent_From_The_Pool_But_Still_Resolvable_By_Id()
        {
            var db = new PoolDatabase(new[]
            {
                Card("keep", collectible: true, gated: false),
                Card("held", collectible: true, gated: true),
            });

            Assert.That(db.AllCollectible().Select(d => d.Id), Is.EquivalentTo(new[] { "keep" }),
                "a gated row must not reach a pool.");

            Assert.That(db.TryGet("held", out var held), Is.True,
                "but it must stay resolvable — tokens, replays and already-saved decks reference it by id.");
            Assert.That(held.Id, Is.EqualTo("held"));
        }

        [Test]
        public void Gated_Row_Never_Enters_A_Generated_Deck()
        {
            // A pool wide enough to build 30 cards, in which exactly one row is gated.
            var rows = new List<CardDefinition>();
            for (int i = 0; i < 40; i++)
                rows.Add(Card($"ok_{i:D2}", collectible: true, gated: false));
            rows.Add(Card("gated_row", collectible: true, gated: true));

            var db = new PoolDatabase(rows);
            var character = new Character("chr-1", "Test", CardClass.NEUTRAL, CardClass.NEUTRAL);

            var deck = DefaultDeckGenerator.Generate(character, db, new DeterministicRandom(1234), "d");

            Assert.That(deck.TotalCount, Is.EqualTo(30), "sanity: the pool was wide enough to build.");
            Assert.That(deck.CountOf("gated_row"), Is.Zero,
                "the deck generator draws from AllCollectible, so the gate must reach it.");
        }

        [Test]
        public void Live_Card_Set_Sets_The_Flag_On_Nothing_Yet()
        {
            // Behaviour-neutrality witness: this gate changed no shipped content when it landed.
            // When a row is first held behind the flag, update this to name it — a silent content
            // removal is exactly what this assertion exists to surface.
            var gated = LiveCardSet.Db.AllCollectible().Where(d => d.RequiresEngineeringPrimitive).ToList();
            Assert.That(gated, Is.Empty,
                "AllCollectible must never hand back a gated row, whatever the live set contains.");
        }

        private static CardDefinition Card(string id, bool collectible, bool gated)
            => new CardDefinition(id, id, CardType.MINION, cost: 1, attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: Array.Empty<EffectDef>(),
                cardText: "", flavorText: "", artId: "", setId: SetId.CORE,
                collectible: collectible, tribe: null, factionId: "", requiresEngineeringPrimitive: gated);

        /// <summary>
        /// A database that honours the <see cref="ICardDatabase.AllCollectible"/> contract, so these
        /// tests exercise the contract rather than a double that agrees with them by construction.
        /// </summary>
        private sealed class PoolDatabase : ICardDatabase
        {
            private readonly Dictionary<string, CardDefinition> _byId = new(StringComparer.Ordinal);
            public PoolDatabase(IEnumerable<CardDefinition> defs) { foreach (var d in defs) _byId[d.Id] = d; }
            public bool TryGet(string cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(string cardId) => _byId[cardId];
            public IReadOnlyCollection<CardDefinition> AllCollectible()
                => _byId.Values.Where(d => d.IsPoolEligible).ToList();
        }
    }
}
