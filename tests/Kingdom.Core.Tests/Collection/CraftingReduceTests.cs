using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The Reduce half of <see cref="Crafting"/> once it can take more than one copy: a counted
    /// Reduce of one card, and <see cref="Crafting.Spares"/> — every copy past a deck cap. Against the
    /// live card set, like the deck-rule fixtures beside it.
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CraftingReduceTests
    {
        private static CardDefinition First(Rarity rarity) =>
            LiveCardSet.Cards.First(d => d.PoolEligible && d.Rarity == rarity);

        [Test]
        public void A_Counted_Reduce_Pays_Per_Copy_And_Leaves_The_Rest()
        {
            CardDefinition rare = First(Rarity.RARE);
            ReduceOffer offer = Crafting.OfferReduce(rare, copiesOwned: 5, count: 3);

            Assert.Multiple(() =>
            {
                Assert.That(offer.Offerable, Is.True);
                Assert.That(offer.Count, Is.EqualTo(3));
                Assert.That(offer.Payout, Is.EqualTo(3 * ShardValues.ForRarity(Rarity.RARE)));
                Assert.That(offer.CopiesAfter, Is.EqualTo(2));
            });
        }

        [Test]
        public void More_Copies_Than_Owned_Is_Refused_Whole_Not_Clamped()
        {
            ReduceOffer offer = Crafting.OfferReduce(First(Rarity.COMMON), copiesOwned: 2, count: 3);

            Assert.Multiple(() =>
            {
                Assert.That(offer.Refusal, Is.EqualTo(ReduceRefusal.NotEnoughCopies));
                Assert.That(offer.CopiesAfter, Is.EqualTo(2), "A refused Reduce leaves every copy.");
            });
        }

        [Test]
        public void Owning_None_Outranks_Asking_For_Too_Many()
        {
            Assert.That(Crafting.OfferReduce(First(Rarity.COMMON), copiesOwned: 0, count: 3).Refusal,
                Is.EqualTo(ReduceRefusal.OwnsNone));
        }

        [Test]
        public void Spares_Are_The_Copies_Past_The_Deck_Cap_And_Nothing_Else()
        {
            CardDefinition common = First(Rarity.COMMON);
            CardDefinition atCap = LiveCardSet.Cards.First(d => d.PoolEligible && d.Rarity == Rarity.COMMON && d.Id != common.Id);
            CardDefinition epic = First(Rarity.EPIC);
            var owned = new OwnedCardCounts();
            owned.Set(common.Id, 5);
            owned.Set(atCap.Id, DeckValidator.MaxCopies(Rarity.COMMON));
            owned.Set(epic.Id, 3);

            IReadOnlyList<SpareCopies> spares = Crafting.Spares(LiveCardSet.Cards, owned, includeLegendary: false);

            Assert.Multiple(() =>
            {
                Assert.That(spares.Select(s => s.Card.Id), Is.EquivalentTo(new[] { common.Id, epic.Id }));
                Assert.That(spares.Single(s => s.Card.Id == common.Id).Copies, Is.EqualTo(3));
                Assert.That(spares.Single(s => s.Card.Id == common.Id).Payout,
                    Is.EqualTo(3 * ShardValues.ForRarity(Rarity.COMMON)));
                Assert.That(spares.Single(s => s.Card.Id == epic.Id).Copies, Is.EqualTo(1));
            });
        }

        [Test]
        public void A_Spare_Legendary_Is_Kept_Unless_Asked_For()
        {
            CardDefinition legendary = First(Rarity.LEGENDARY);
            var owned = new OwnedCardCounts();
            owned.Set(legendary.Id, 3);

            IReadOnlyList<SpareCopies> without = Crafting.Spares(LiveCardSet.Cards, owned, includeLegendary: false);
            IReadOnlyList<SpareCopies> with = Crafting.Spares(LiveCardSet.Cards, owned, includeLegendary: true);

            Assert.Multiple(() =>
            {
                Assert.That(without, Is.Empty);
                Assert.That(with.Single().Copies, Is.EqualTo(2), "One Legendary is the deck cap; the other two are spare.");
                Assert.That(with.Single().Payout, Is.EqualTo(2 * ShardValues.ForRarity(Rarity.LEGENDARY)));
            });
        }

        [Test]
        public void A_Spare_Count_Is_The_Copies_Past_The_Cap_Whatever_The_Rarity()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Crafting.SpareCount(First(Rarity.COMMON), 5), Is.EqualTo(3));
                Assert.That(Crafting.SpareCount(First(Rarity.COMMON), 2), Is.Zero);
                Assert.That(Crafting.SpareCount(First(Rarity.LEGENDARY), 3), Is.EqualTo(2),
                    "Leaving Legendaries out is the offer's default, not a limit on what is spare.");
                Assert.That(Crafting.SpareCount(null, 9), Is.Zero);
            });
        }

        [Test]
        public void Unknown_Ownership_Has_No_Spares()
        {
            Assert.That(Crafting.Spares(LiveCardSet.Cards, null, includeLegendary: true), Is.Empty,
                "A null record means ownership is unknown, never that every card is held in excess.");
        }
    }
}
