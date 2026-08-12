using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The pure crafting predicates (GH#157 Render, GH#158 Reduce). These pin the <i>decision</i> — the
    /// cap, the price read, the deck impact — with no ledger anywhere near them;
    /// <c>CraftingServiceTests</c> covers the half that moves a balance.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CraftingTests
    {
        private static CardDefinition Card(Rarity rarity, bool collectible = true) => new CardDefinition(
            id: "c_" + rarity.ToString().ToLowerInvariant() + (collectible ? "" : "_token"),
            name: "Test " + rarity, type: CardType.MINION, cost: 1,
            attack: 1, health: 1, durability: null,
            @class: CardClass.NEUTRAL, rarity: rarity,
            keywords: System.Array.Empty<Keyword>(), rules: System.Array.Empty<RuleDefinition>(),
            flavorText: "", artId: "test", setId: SetId.EMBERFALL,
            collectible: collectible, tribe: null, factionId: "");

        private static DeckList Deck(string id, params (string CardId, int Count)[] entries)
        {
            var deck = new DeckList { Id = id, CharacterId = "char-1", Name = id };
            foreach ((string cardId, int count) in entries) deck.Entries.Add(new DeckEntry(cardId, count));
            return deck;
        }

        // ── Render ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void Render_Prices_Every_Rarity_From_The_ShardCosts_Table()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Crafting.OfferRender(Card(Rarity.COMMON), 0, 10_000).Cost, Is.EqualTo(ShardCosts.Common));
                Assert.That(Crafting.OfferRender(Card(Rarity.RARE), 0, 10_000).Cost, Is.EqualTo(ShardCosts.Rare));
                Assert.That(Crafting.OfferRender(Card(Rarity.EPIC), 0, 10_000).Cost, Is.EqualTo(ShardCosts.Epic));
                Assert.That(Crafting.OfferRender(Card(Rarity.LEGENDARY), 0, 10_000).Cost, Is.EqualTo(ShardCosts.Legendary));
            });
        }

        [Test]
        public void Render_Is_Offerable_With_Exactly_The_Cost_In_Shards()
        {
            RenderOffer offer = Crafting.OfferRender(Card(Rarity.EPIC), 0, ShardCosts.Epic);

            Assert.Multiple(() =>
            {
                Assert.That(offer.Offerable, Is.True);
                Assert.That(offer.Refusal, Is.EqualTo(RenderRefusal.None));
            });
        }

        [Test]
        public void Render_Refuses_One_Shard_Short()
        {
            RenderOffer offer = Crafting.OfferRender(Card(Rarity.EPIC), 0, ShardCosts.Epic - 1);

            Assert.That(offer.Refusal, Is.EqualTo(RenderRefusal.InsufficientShards));
        }

        [Test]
        public void Render_Refuses_A_Third_Common_And_A_Second_Legendary()
        {
            RenderOffer thirdCommon = Crafting.OfferRender(Card(Rarity.COMMON), 2, 10_000);
            RenderOffer secondLegendary = Crafting.OfferRender(Card(Rarity.LEGENDARY), 1, 10_000);

            Assert.Multiple(() =>
            {
                Assert.That(thirdCommon.Refusal, Is.EqualTo(RenderRefusal.AtCopyCap));
                Assert.That(thirdCommon.CopyCap, Is.EqualTo(2));
                Assert.That(secondLegendary.Refusal, Is.EqualTo(RenderRefusal.AtCopyCap));
                Assert.That(secondLegendary.CopyCap, Is.EqualTo(1));
            });
        }

        [Test]
        public void Render_Offers_A_Second_Common_And_A_First_Legendary()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Crafting.OfferRender(Card(Rarity.COMMON), 1, 10_000).Offerable, Is.True);
                Assert.That(Crafting.OfferRender(Card(Rarity.LEGENDARY), 0, 10_000).Offerable, Is.True);
            });
        }

        /// <summary>
        /// Owning past the deck cap is a legal state — packs may stack copies without limit (Sam,
        /// 2026-08-11) — and crafting past it is refused (Sam, 2026-08-12). Both rulings are live at
        /// once, which is the pair a reader is most likely to mistake for a contradiction.
        /// </summary>
        [Test]
        public void Owning_Past_The_Cap_Is_Legal_And_Only_Crafting_Is_Refused()
        {
            CardDefinition common = Card(Rarity.COMMON);

            RenderOffer render = Crafting.OfferRender(common, copiesOwned: 5, shardBalance: 10_000);
            ReduceOffer reduce = Crafting.OfferReduce(common, copiesOwned: 5);

            Assert.Multiple(() =>
            {
                Assert.That(render.Refusal, Is.EqualTo(RenderRefusal.AtCopyCap));
                Assert.That(reduce.Offerable, Is.True);
                Assert.That(reduce.CopiesAfter, Is.EqualTo(4));
                // The deck still plays 2 of them, which is why holding 5 costs nothing to allow.
                Assert.That(DeckValidator.CopyLimit(Rarity.COMMON, OwnedAt(common.Id, 5), common.Id),
                            Is.EqualTo(2));
            });
        }

        private static OwnedCardCounts OwnedAt(string cardId, int copies)
        {
            var owned = new OwnedCardCounts();
            owned.Set(cardId, copies);
            return owned;
        }

        [Test]
        public void Render_Reports_The_Cap_Ahead_Of_A_Price_It_Also_Cannot_Pay()
        {
            RenderOffer offer = Crafting.OfferRender(Card(Rarity.LEGENDARY), 1, 0);

            Assert.That(offer.Refusal, Is.EqualTo(RenderRefusal.AtCopyCap));
        }

        [Test]
        public void Render_Refuses_A_Non_Collectible_Row_And_An_Unknown_Id()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Crafting.OfferRender(Card(Rarity.COMMON, collectible: false), 0, 10_000).Refusal,
                            Is.EqualTo(RenderRefusal.NotCollectible));
                Assert.That(Crafting.OfferRender((CardDefinition?)null, 0, 10_000).Refusal,
                            Is.EqualTo(RenderRefusal.UnknownCard));
            });
        }

        // ── Reduce ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void Reduce_Pays_The_Locked_ShardValues_Table_Untouched()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Crafting.OfferReduce(Card(Rarity.COMMON), 1).Payout, Is.EqualTo(ShardValues.Common));
                Assert.That(Crafting.OfferReduce(Card(Rarity.RARE), 1).Payout, Is.EqualTo(ShardValues.Rare));
                Assert.That(Crafting.OfferReduce(Card(Rarity.EPIC), 1).Payout, Is.EqualTo(ShardValues.Epic));
                Assert.That(Crafting.OfferReduce(Card(Rarity.LEGENDARY), 1).Payout, Is.EqualTo(ShardValues.Legendary));
            });
        }

        [Test]
        public void Reduce_Of_One_Of_Two_Leaves_One()
        {
            ReduceOffer offer = Crafting.OfferReduce(Card(Rarity.COMMON), 2);

            Assert.Multiple(() =>
            {
                Assert.That(offer.Offerable, Is.True);
                Assert.That(offer.CopiesAfter, Is.EqualTo(1));
            });
        }

        [Test]
        public void Reduce_Refuses_When_The_Player_Owns_None()
        {
            ReduceOffer offer = Crafting.OfferReduce(Card(Rarity.RARE), 0);

            Assert.Multiple(() =>
            {
                Assert.That(offer.Refusal, Is.EqualTo(ReduceRefusal.OwnsNone));
                Assert.That(offer.CopiesAfter, Is.EqualTo(0));
            });
        }

        [Test]
        public void Reduce_Is_Not_Capped_By_Anything_The_Render_Cap_Applies()
        {
            // Owning the format cap is exactly when Render refuses and Reduce is most wanted.
            Assert.That(Crafting.OfferReduce(Card(Rarity.LEGENDARY), 1).Offerable, Is.True);
        }

        // ── Deck impact (the material a Reduce confirmation is built from) ────────────────────

        [Test]
        public void DeckImpact_Names_Every_Deck_Holding_The_Card()
        {
            var decks = new List<DeckList>
            {
                Deck("deck-a", ("c_common", 2)),
                Deck("deck-b", ("c_rare", 1)),
                Deck("deck-c", ("c_common", 1)),
            };

            IReadOnlyList<DeckCardUsage> impact = Crafting.DeckImpact("c_common", copiesAfter: 1, decks);

            Assert.Multiple(() =>
            {
                Assert.That(impact.Count, Is.EqualTo(2));
                Assert.That(impact[0].Deck.Id, Is.EqualTo("deck-a"));
                Assert.That(impact[0].Copies, Is.EqualTo(2));
                Assert.That(impact[0].BecomesUnbacked, Is.True);
                Assert.That(impact[1].Deck.Id, Is.EqualTo("deck-c"));
                Assert.That(impact[1].BecomesUnbacked, Is.False);
            });
        }

        [Test]
        public void DeckImpact_Reaches_Decks_Bound_To_Another_Character()
        {
            var other = Deck("deck-other", ("c_epic", 1));
            other.CharacterId = "char-2";

            IReadOnlyList<DeckCardUsage> impact = Crafting.DeckImpact("c_epic", copiesAfter: 0, new[] { other });

            Assert.Multiple(() =>
            {
                Assert.That(impact.Count, Is.EqualTo(1));
                Assert.That(impact[0].BecomesUnbacked, Is.True);
            });
        }

        [Test]
        public void DeckImpact_Is_Empty_When_No_Saved_Deck_Runs_The_Card()
        {
            var decks = new List<DeckList> { Deck("deck-a", ("c_rare", 2)) };

            Assert.That(Crafting.DeckImpact("c_common", copiesAfter: 0, decks).Count, Is.EqualTo(0));
        }

        [Test]
        public void DeckImpact_Of_A_Spare_Copy_Leaves_Every_Deck_Backed()
        {
            var decks = new List<DeckList> { Deck("deck-a", ("c_common", 1)) };

            IReadOnlyList<DeckCardUsage> impact = Crafting.DeckImpact("c_common", copiesAfter: 1, decks);

            Assert.That(impact[0].BecomesUnbacked, Is.False);
        }

        [Test]
        public void An_Unbacked_Deck_Is_Exactly_What_L6_Then_Reports()
        {
            // The confirm's claim and the validator's verdict have to be the same sentence.
            var db = BoosterPackFixture.Db(new[] { Card(Rarity.COMMON) });
            var deck = Deck("deck-a", ("c_common", 2));
            var owned = new OwnedCardCounts();
            owned.Set("c_common", 1);

            IReadOnlyList<DeckCardUsage> impact = Crafting.DeckImpact("c_common", copiesAfter: 1, new[] { deck });
            var character = new Character("char-1", "Tester", CardClass.NEUTRAL, CardClass.NEUTRAL);
            DeckValidation validation = DeckValidator.Validate(deck, character, db, owned);

            Assert.Multiple(() =>
            {
                Assert.That(impact[0].BecomesUnbacked, Is.True);
                Assert.That(validation.Violations, Has.Some.Matches<DeckViolation>(v => v.Rule == DeckRule.L6_Ownership));
            });
        }
    }
}
