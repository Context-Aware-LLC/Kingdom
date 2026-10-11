using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The starter-deck choice (Sam, 2026-07-26 — hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.11): the first
    /// deck is always generated, and every deck after that is asked whether it wants one or a blank.
    ///
    /// <para><b>The subject moved from characters to decks (ADR-0009) and the ruling did not.</b> It
    /// keyed on the ROSTER being empty and now keys on the DECK LIST being empty, which is the same
    /// sentence — "a player with nothing has nothing to build from" — with the container removed.
    /// One test went entirely: <c>A_Blank_Starter_Keeps_The_Deck_First_Write_Order</c> pinned that
    /// creation wrote the deck before the character, so a crash between the two left an unreferenced
    /// deck rather than a character pointing at nothing. There is ONE write now, so there is no
    /// order to keep and no residue to classify.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class StarterDeckChoiceTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "kingdom-starter-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private JsonDeckStore Decks() => JsonDeckStore.InDirectory(_root, "device-token");

        // ---- who gets asked ------------------------------------------------------------

        [Test]
        public void The_First_Deck_Is_Not_Asked()
        {
            Assert.That(DeckCreation.OffersStarterChoice(Decks().LoadAll()), Is.False);
            Assert.That(DeckCreation.OffersStarterChoice(Array.Empty<DeckList>()), Is.False);
            Assert.That(DeckCreation.OffersStarterChoice(null!), Is.False);
        }

        [Test]
        public void Every_Deck_After_The_First_Is_Asked()
        {
            var decks = Decks();
            decks.Save(new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT) { Id = "d1" });

            Assert.That(DeckCreation.OffersStarterChoice(decks.LoadAll()), Is.True);
        }

        [Test]
        public void Deleting_Every_Deck_Puts_The_Player_Back_On_The_First()
        {
            // It keys on the list being empty, not a first-run flag: a player with nothing again has
            // nothing to build from again, and is handed a deck rather than an empty one.
            var decks = Decks();
            DeckCreation.Create(decks, LiveCardSet.Db, new DeterministicRandom(7),
                "d1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);
            Assert.That(DeckCreation.OffersStarterChoice(decks.LoadAll()), Is.True);

            decks.Delete("d1");

            Assert.That(DeckCreation.OffersStarterChoice(decks.LoadAll()), Is.False);
        }

        // ---- what each choice produces --------------------------------------------------

        [Test]
        public void Generated_Is_Still_The_Default_Behaviour()
        {
            // The parameter is optional and defaults to Generated — what the first deck always gets,
            // and what every existing caller kept getting.
            var decks = Decks();

            DeckCreation.Create(decks, LiveCardSet.Db, new DeterministicRandom(7),
                "d1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);

            Assert.That(decks.LoadAll().Single().TotalCount, Is.EqualTo(DeckValidator.DeckSize));
        }

        [Test]
        public void A_Blank_Starter_Is_A_Real_Saved_Deck_That_Is_A_Draft()
        {
            var decks = Decks();

            var created = DeckCreation.Create(decks, LiveCardSet.Db, new DeterministicRandom(7),
                "d1", "", CardClass.GRAVEKEEP, CardClass.VERDANT, StarterDeck.Blank);

            var stored = decks.LoadAll().Single();

            Assert.That(created.Id, Is.EqualTo("d1"));
            Assert.That(stored, Is.Not.Null, "Blank means an empty deck, not the absence of one.");
            Assert.That(stored.TotalCount, Is.Zero);
            Assert.That(stored.Name, Is.EqualTo("New Deck"), "§2.11's name for any newly created deck.");
            Assert.That(QueueEligibility.IsDraft(stored, LiveCardSet.Db), Is.True,
                "A draft under §1 until it reaches 30 — not a special state of its own.");
        }

        [Test]
        public void A_Blank_Starter_Appears_In_The_Grid_Like_Any_Other_Deck()
        {
            var decks = Decks();
            DeckCreation.Create(decks, LiveCardSet.Db, new DeterministicRandom(7),
                "d1", "", CardClass.GRAVEKEEP, CardClass.VERDANT, StarterDeck.Blank);

            var rows = DeckQuery.ForAccount(decks.LoadAll(), LiveCardSet.Db);

            Assert.That(rows.Count, Is.EqualTo(1));
            Assert.That(rows[0].IsDraft, Is.True);
            Assert.That(rows[0].Deck.Id, Is.EqualTo("d1"));
        }

        [Test]
        public void A_Blank_Starter_Asks_Nothing_Of_The_Card_Pool()
        {
            // The strong form of "blank consults no database": a pool that could never generate a
            // legal 30 still creates a deck you can build by hand.
            var starved = new EmptyCardDatabase();
            var decks = Decks();

            Assert.DoesNotThrow(() => DeckCreation.Create(
                decks, starved, new DeterministicRandom(7),
                "d1", "", CardClass.GRAVEKEEP, CardClass.VERDANT, StarterDeck.Blank));

            Assert.That(decks.LoadAll().Single().TotalCount, Is.Zero);
            Assert.Throws<DeckGenerationException>(() => DeckCreation.Create(
                decks, starved, new DeterministicRandom(7),
                "d2", "", CardClass.EMBERWARD, CardClass.TIDECALLER, StarterDeck.Generated),
                "The same pool still refuses to generate — the difference is the choice, not the data.");
        }

        [Test]
        public void An_Invalid_Pairing_Is_Rejected_Whichever_Starter_Was_Asked_For()
        {
            var decks = Decks();

            Assert.Throws<ArgumentException>(() => DeckCreation.Create(
                decks, LiveCardSet.Db, new DeterministicRandom(7),
                "d1", "Doubled", CardClass.EMBERWARD, CardClass.EMBERWARD, StarterDeck.Blank));

            Assert.That(decks.LoadAll(), Is.Empty, "A rejected creation leaves no blank deck behind either.");
        }

        [Test]
        public void A_Failed_Write_Leaves_Nothing_Behind()
        {
            // What replaced the deck-first write-order test. With one store there is no ordering to
            // guarantee and no survivable half to name: the write either happened or it did not.
            var flaky = new ThrowsOnSave();

            Assert.Throws<IOException>(() => DeckCreation.Create(
                flaky, LiveCardSet.Db, new DeterministicRandom(7),
                "d1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, StarterDeck.Blank));

            Assert.That(flaky.LoadAll(), Is.Empty);
        }

        private sealed class EmptyCardDatabase : ICardDatabase
        {
            public bool TryGet(int cardId, out CardDefinition def) { def = null!; return false; }
            public CardDefinition Get(int cardId) => throw new KeyNotFoundException(cardId.ToString());
            public IReadOnlyCollection<CardDefinition> AllCollectible() => Array.Empty<CardDefinition>();
        }

        private sealed class ThrowsOnSave : IDeckStore
        {
            public IReadOnlyList<DeckList> LoadAll() => Array.Empty<DeckList>();
            public void Save(DeckList deck) => throw new IOException("disk went away");
            public void Delete(string deckId) { }
            public void ReplaceAll(IReadOnlyList<DeckList> decks) { }
        }
    }
}
