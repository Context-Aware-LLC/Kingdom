using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The card lifecycle field (#398, folding in #86's retired state). The properties that matter:
    /// a row without the field is PUBLISHED (no migration), a DRAFT or RETIRED row still resolves but
    /// never enters the live pool, and the deck validator refuses it even for an account that owns
    /// every card.
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CardLifecycleTests
    {
        private const string Row =
            "{\"id\":%ID%,\"handle\":\"lifecycle_probe_%ID%\",\"name\":\"Probe\",\"type\":\"MINION\",\"cost\":1," +
            "\"attack\":1,\"health\":1,\"class\":\"NEUTRAL\",\"rarity\":\"COMMON\"%LIFECYCLE%}";

        private static CardDefinition Probe(int id, string? lifecycle) =>
            CardJson.Deserialize(Row.Replace("%ID%", id.ToString()).Replace("%LIFECYCLE%",
                lifecycle == null ? "" : ",\"lifecycle\":\"" + lifecycle + "\""));

        [Test]
        public void A_Row_Without_The_Field_Is_Published_And_Pool_Eligible()
        {
            CardDefinition card = Probe(990001, null);
            Assert.That(card.Lifecycle, Is.EqualTo(CardLifecycle.PUBLISHED));
            Assert.That(card.PoolEligible, Is.True);
        }

        [TestCase("DRAFT", CardLifecycle.DRAFT)]
        [TestCase("RETIRED", CardLifecycle.RETIRED)]
        public void Draft_And_Retired_Rows_Resolve_But_Leave_The_Live_Pool(string json, CardLifecycle expected)
        {
            CardDefinition card = Probe(990002, json);
            var db = new CardNameIndex(new[] { card });

            Assert.That(card.Lifecycle, Is.EqualTo(expected));
            Assert.That(card.Collectible, Is.True, "collectible is the card's nature; lifecycle is separate");
            Assert.That(card.PoolEligible, Is.False);
            Assert.That(db.TryGet(card.Id, out _), Is.True, "a non-live row must stay resolvable (#86)");
            Assert.That(db.AllCollectible(), Is.Empty);
        }

        [Test]
        public void An_Unknown_Lifecycle_Value_Is_A_Loud_Error()
        {
            Assert.Throws<System.ArgumentException>(() => Probe(990003, "SOMEDAY"));
        }

        [TestCase("DRAFT")]
        [TestCase("RETIRED")]
        public void The_Deck_Validator_Refuses_A_Non_Published_Card_Even_When_Everything_Is_Owned(string json)
        {
            CardDefinition probe = Probe(990004, json);
            var db = new CardNameIndex(LiveCardSet.Cards.Concat(new[] { probe }));
            var deck = new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT) { Id = "d", Name = "d" };
            deck.Entries.Add(new DeckEntry(probe.Id, 1));

            DeckValidation result = DeckValidator.Validate(deck, db, AllCardsOwnedCounts.Instance);

            Assert.That(result.Violations.Select(v => v.Rule), Does.Contain(DeckRule.L7_NotPublished));
        }
    }
}
