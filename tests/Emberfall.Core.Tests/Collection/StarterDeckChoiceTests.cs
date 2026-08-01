using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The starter-deck choice (Sam, 2026-07-26 — hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.11): the first
    /// character always gets a generated deck, and every character after that is asked whether it
    /// wants one or a blank.
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class StarterDeckChoiceTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-starter-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private JsonCharacterStore Characters() => JsonCharacterStore.InDirectory(_root, "device-token");
        private JsonDeckStore Decks() => JsonDeckStore.InDirectory(_root, "device-token");

        // ---- who gets asked ------------------------------------------------------------

        [Test]
        public void The_First_Character_Is_Not_Asked()
        {
            Assert.That(CharacterCreation.OffersStarterChoice(Characters().LoadAll()), Is.False);
            Assert.That(CharacterCreation.OffersStarterChoice(Array.Empty<Character>()), Is.False);
            Assert.That(CharacterCreation.OffersStarterChoice(null!), Is.False);
        }

        [Test]
        public void Every_Character_After_The_First_Is_Asked()
        {
            var characters = Characters();
            characters.Save(new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT));

            Assert.That(CharacterCreation.OffersStarterChoice(characters.LoadAll()), Is.True);
        }

        [Test]
        public void Deleting_Every_Character_Puts_The_Player_Back_On_The_First()
        {
            // It keys on the roster being empty, not a first-run flag: a player with nothing again has
            // nothing to build from again, and is handed a deck rather than an empty one.
            var characters = Characters();
            var decks = Decks();
            CharacterCreation.Create(characters, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, "d1");
            Assert.That(CharacterCreation.OffersStarterChoice(characters.LoadAll()), Is.True);

            CharacterDeletion.Delete(characters, decks, "chr-1");

            Assert.That(CharacterCreation.OffersStarterChoice(characters.LoadAll()), Is.False);
        }

        // ---- what each choice produces --------------------------------------------------

        [Test]
        public void Generated_Is_Still_The_Default_Behaviour()
        {
            // The parameter is optional and defaults to Generated — what the first character always
            // gets, and what every existing caller kept getting.
            var characters = Characters();
            var decks = Decks();

            CharacterCreation.Create(characters, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, "d1");

            Assert.That(decks.LoadAll().Single().TotalCount, Is.EqualTo(DeckValidator.DeckSize));
        }

        [Test]
        public void A_Blank_Starter_Is_A_Real_Saved_Deck_That_Is_The_Default_And_A_Draft()
        {
            var characters = Characters();
            var decks = Decks();

            var created = CharacterCreation.Create(characters, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, "d1", StarterDeck.Blank);

            var stored = characters.LoadAll().Single();
            var deck = CharacterQuery.DefaultDeckOf(stored, decks.LoadAll());

            Assert.That(created.DefaultDeckId, Is.EqualTo("d1"));
            Assert.That(deck, Is.Not.Null, "Blank means an empty deck, not the absence of one.");
            Assert.That(deck!.TotalCount, Is.Zero);
            Assert.That(deck.Name, Is.EqualTo("New Deck"), "§2.11's name for any newly created deck.");
            Assert.That(QueueEligibility.IsDraft(deck, stored, LiveCardSet.Db), Is.True,
                "A draft under §1 until it reaches 30 — not a special state of its own.");
        }

        [Test]
        public void The_Sidebar_Opens_On_A_Blank_Starter_Like_Any_Other_Default()
        {
            var characters = Characters();
            var decks = Decks();
            CharacterCreation.Create(characters, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, "d1", StarterDeck.Blank);

            var result = DeckQuery.ForCharacter(characters.LoadAll().Single(), decks.LoadAll(), LiveCardSet.Db);

            Assert.That(result.DefaultResolved, Is.True);
            Assert.That(result.Opening!.Value.IsDefault, Is.True);
            Assert.That(result.Opening!.Value.IsDraft, Is.True);
        }

        [Test]
        public void A_Blank_Starter_Asks_Nothing_Of_The_Card_Pool()
        {
            // The strong form of "blank consults no database": a pool that could never generate a
            // legal 30 still creates a character you can build for by hand.
            var starved = new EmptyCardDatabase();
            var characters = Characters();
            var decks = Decks();

            Assert.DoesNotThrow(() => CharacterCreation.Create(
                characters, decks, starved, new DeterministicRandom(7),
                "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, "d1", StarterDeck.Blank));

            Assert.That(decks.LoadAll().Single().TotalCount, Is.Zero);
            Assert.Throws<DeckGenerationException>(() => CharacterCreation.Create(
                characters, decks, starved, new DeterministicRandom(7),
                "chr-2", "Emberling", CardClass.EMBERWARD, CardClass.TIDECALLER, "d2", StarterDeck.Generated),
                "The same pool still refuses to generate — the difference is the choice, not the data.");
        }

        [Test]
        public void A_Blank_Starter_Keeps_The_Deck_First_Write_Order()
        {
            // Same crash reasoning as the generated path: an unreferenced deck survives better than a
            // character pointing at one that does not exist.
            var decks = Decks();
            var flaky = new ThrowsOnSave();

            Assert.Throws<IOException>(() => CharacterCreation.Create(
                flaky, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, "d1", StarterDeck.Blank));

            Assert.That(decks.LoadAll().Single().Id, Is.EqualTo("d1"), "The deck was written first.");
            Assert.That(DeckQuery.Orphans(decks.LoadAll(), Array.Empty<Character>()).Count, Is.EqualTo(1),
                "And the residue is the survivable half.");
        }

        [Test]
        public void An_Invalid_Pairing_Is_Rejected_Whichever_Starter_Was_Asked_For()
        {
            var characters = Characters();
            var decks = Decks();

            Assert.Throws<ArgumentException>(() => CharacterCreation.Create(
                characters, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Doubled", CardClass.EMBERWARD, CardClass.EMBERWARD, "d1", StarterDeck.Blank));

            Assert.That(decks.LoadAll(), Is.Empty, "A rejected creation leaves no blank deck behind either.");
        }

        private sealed class EmptyCardDatabase : ICardDatabase
        {
            public bool TryGet(string cardId, out CardDefinition def) { def = null!; return false; }
            public CardDefinition Get(string cardId) => throw new KeyNotFoundException(cardId);
            public IReadOnlyCollection<CardDefinition> AllCollectible() => Array.Empty<CardDefinition>();
        }

        private sealed class ThrowsOnSave : ICharacterStore
        {
            public IReadOnlyList<Character> LoadAll() => Array.Empty<Character>();
            public void Save(Character character) => throw new IOException("disk went away");
            public void Delete(string characterId) { }
        }
    }
}
