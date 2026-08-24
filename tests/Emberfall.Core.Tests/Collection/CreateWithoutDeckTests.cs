using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The deckless creation path (NOR-278): the portrait-grid flow picks only the class pairing
    /// and a name, and the character lands with NO deck — the same lone-"+" row a character whose
    /// decks were all deleted already shows. The first deck is a later, separate choice.
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CreateWithoutDeckTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-nodeck-tests", Guid.NewGuid().ToString("N"));
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

        [Test]
        public void It_Saves_The_Character_And_No_Deck()
        {
            var characters = Characters();
            var decks = Decks();

            var created = CharacterCreation.CreateWithoutDeck(
                characters, "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);

            Assert.That(created.Id, Is.EqualTo("chr-1"));
            Assert.That(created.Name, Is.EqualTo("Rotbloom"));
            Assert.That(created.DefaultDeckId, Is.Empty, "A deckless character points at no default.");
            Assert.That(characters.LoadAll().Single().Id, Is.EqualTo("chr-1"));
            Assert.That(decks.LoadAll(), Is.Empty, "No deck is written at all.");
        }

        [Test]
        public void It_Lands_As_The_Same_Empty_State_As_A_Character_With_No_Decks()
        {
            // The whole point of the deckless path: the roster's existing lone-"+" state renders it
            // with no new building. DeckQuery must report a character with no decks and no resolvable
            // default — exactly what a character whose decks were all deleted reports.
            var characters = Characters();
            var decks = Decks();

            CharacterCreation.CreateWithoutDeck(
                characters, "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);

            var result = DeckQuery.ForCharacter(characters.LoadAll().Single(), decks.LoadAll(), LiveCardSet.Db);

            Assert.That(result.Count, Is.Zero, "No decks.");
            Assert.That(result.DefaultResolved, Is.False, "Nothing resolves as the default.");
            Assert.That(result.Opening, Is.Null, "And nothing to open on.");
        }

        [Test]
        public void It_Asks_Nothing_Of_The_Card_Pool()
        {
            // No ICardDatabase argument at all — a deckless character is creatable for any valid
            // pairing however thin its pool. Whether that pool can fill a 30 is the "+" plate's
            // concern, not creation's.
            var characters = Characters();

            Assert.DoesNotThrow(() => CharacterCreation.CreateWithoutDeck(
                characters, "chr-1", "Emberling", CardClass.EMBERWARD, CardClass.TIDECALLER));

            Assert.That(characters.LoadAll().Single().Id, Is.EqualTo("chr-1"));
        }

        [Test]
        public void An_Invalid_Pairing_Is_Rejected_And_Writes_Nothing()
        {
            var characters = Characters();

            Assert.Throws<ArgumentException>(() => CharacterCreation.CreateWithoutDeck(
                characters, "chr-1", "Doubled", CardClass.EMBERWARD, CardClass.EMBERWARD));

            Assert.That(characters.LoadAll(), Is.Empty, "A rejected pairing leaves no roster row.");
        }
    }
}
