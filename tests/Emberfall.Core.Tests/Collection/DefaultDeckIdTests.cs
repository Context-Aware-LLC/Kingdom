using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="Character.DefaultDeckId"/> — the deck a character opens on (Sam, 2026-07-26) — its
    /// persistence, and the character-creation flow that sets it.
    ///
    /// <para>The rule under test is the same one the rest of this store obeys: a reference to
    /// something that no longer exists is <b>surfaced</b>, never repaired.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DefaultDeckIdTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-defaultdeck-tests", Guid.NewGuid().ToString("N"));
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

        // ---- persistence ---------------------------------------------------------------

        [Test]
        public void DefaultDeckId_Round_Trips()
        {
            var c = new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT) { DefaultDeckId = "deck-7" };
            Characters().Save(c);

            Assert.That(Characters().LoadAll().Single().DefaultDeckId, Is.EqualTo("deck-7"));
        }

        [Test]
        public void It_Is_The_One_Mutable_Property_On_Character()
        {
            // The pairing is a permanent identity decision; the default deck is a preference. A
            // player changing their default must not be a delete-and-recreate.
            var store = Characters();
            var c = new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT) { DefaultDeckId = "deck-a" };
            store.Save(c);

            c.DefaultDeckId = "deck-b";
            store.Save(c);

            var loaded = store.LoadAll();
            Assert.That(loaded.Count, Is.EqualTo(1), "Changing the default is an upsert, not a new character.");
            Assert.That(loaded[0].DefaultDeckId, Is.EqualTo("deck-b"));
        }

        [Test]
        public void A_Roster_Written_Before_Default_Decks_Existed_Still_Loads()
        {
            // Additive field, not a schema bump: an older file simply has no default, which is the
            // same state as "the deck it pointed at was deleted" and is handled identically.
            var store = Characters();
            store.Save(new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT));
            File.WriteAllText(store.FilePath, """
                {
                  "schemaVersion": 1,
                  "characters": [
                    {
                      "id": "chr-1",
                      "name": "Rotbloom",
                      "primaryClass": "GRAVEKEEP",
                      "secondaryClass": "VERDANT"
                    }
                  ]
                }
                """);

            var loaded = store.LoadAll();

            Assert.That(loaded.Single().DefaultDeckId, Is.Empty);
            Assert.That(CharacterQuery.DefaultDeckOf(loaded[0], Decks().LoadAll()), Is.Null);
        }

        // ---- the dangling reference ----------------------------------------------------

        [Test]
        public void Deleting_The_Default_Deck_Leaves_The_Reference_Dangling_And_Visible()
        {
            var characters = Characters();
            var decks = Decks();
            var deck = new DeckList { Id = "deck-1", CharacterId = "chr-1", Name = "Starter" };
            decks.Save(deck);
            characters.Save(new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT)
            { DefaultDeckId = "deck-1" });

            decks.Delete("deck-1");

            var reloaded = characters.LoadAll().Single();
            Assert.That(reloaded.DefaultDeckId, Is.EqualTo("deck-1"),
                "The store does not silently repair the reference — consistent with orphaning decks on character delete.");
            Assert.That(CharacterQuery.DefaultDeckOf(reloaded, decks.LoadAll()), Is.Null,
                "The query is what surfaces it; the panel shows 'no default deck' and lets the player choose.");
        }

        [Test]
        public void DefaultDeckOf_Resolves_When_The_Deck_Is_There()
        {
            var decks = new[]
            {
                new DeckList { Id = "deck-1", Name = "A" },
                new DeckList { Id = "deck-2", Name = "B" },
            };
            var c = new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT) { DefaultDeckId = "deck-2" };

            Assert.That(CharacterQuery.DefaultDeckOf(c, decks)!.Name, Is.EqualTo("B"));
            Assert.That(CharacterQuery.DefaultDeckOf(c, Array.Empty<DeckList>()), Is.Null);
            Assert.That(CharacterQuery.DefaultDeckOf(null!, decks), Is.Null);
            Assert.That(CharacterQuery.DefaultDeckOf(c, null!), Is.Null);
        }

        // ---- creation flow -------------------------------------------------------------

        [Test]
        public void Creating_A_Character_Persists_Both_Halves_And_Links_Them()
        {
            var characters = Characters();
            var decks = Decks();

            var created = CharacterCreation.Create(characters, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, "deck-1");

            Assert.That(created.DefaultDeckId, Is.EqualTo("deck-1"));

            var storedCharacter = characters.LoadAll().Single();
            var storedDeck = CharacterQuery.DefaultDeckOf(storedCharacter, decks.LoadAll());

            Assert.That(storedDeck, Is.Not.Null, "A new character opens on a deck that actually exists.");
            Assert.That(storedDeck!.TotalCount, Is.EqualTo(DeckValidator.DeckSize));
            Assert.That(DeckValidator.Validate(storedDeck, storedCharacter, LiveCardSet.Db).IsValid, Is.True);
        }

        [Test]
        public void Creation_Rejects_An_Invalid_Pairing_Before_Writing_Anything()
        {
            var characters = Characters();
            var decks = Decks();

            Assert.Throws<ArgumentException>(() => CharacterCreation.Create(
                characters, decks, LiveCardSet.Db, new DeterministicRandom(7),
                "chr-1", "Doubled", CardClass.EMBERWARD, CardClass.EMBERWARD, "deck-1"));

            Assert.That(characters.LoadAll(), Is.Empty);
            Assert.That(decks.LoadAll(), Is.Empty, "A rejected creation leaves no orphan deck behind.");
        }

        // ---- the rule that must NOT be reintroduced -------------------------------------

        [Test]
        public void An_Illegal_Draft_Deck_Saves_Without_Complaint()
        {
            // Save is NOT legality-gated (§1: what legality gates is queue-eligibility). A half-built
            // deck must survive the session; legality is computed on load and never stored.
            var decks = Decks();
            var draft = new DeckList { Id = "deck-1", CharacterId = "chr-1", Name = "Half-built" };
            draft.Entries.Add(new DeckEntry(LiveCardSet.AnyCollectible(CardClass.GRAVEKEEP).Id, 1));

            Assert.DoesNotThrow(() => decks.Save(draft));

            var loaded = decks.LoadAll().Single();
            Assert.That(loaded.TotalCount, Is.EqualTo(1), "The draft is kept intact, not padded or rejected.");

            var character = new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);
            Assert.That(DeckValidator.Validate(loaded, character, LiveCardSet.Db).IsValid, Is.False,
                "It is a draft: illegal on load, and that is computed, never a stored flag.");
        }

        [Test]
        public void Nothing_In_The_Stored_File_Records_Legality()
        {
            // A stored flag would go stale the moment a balance patch moved the line — §1 requires a
            // deck to stop being a draft on its own when a patch re-validates it.
            var decks = Decks();
            decks.Save(new DeckList { Id = "deck-1", CharacterId = "chr-1", Name = "Half-built" });

            string json = File.ReadAllText(decks.FilePath);
            foreach (var forbidden in new[] { "valid", "legal", "draft" })
                Assert.That(json.ToLowerInvariant(), Does.Not.Contain(forbidden), $"'{forbidden}' must not be persisted.");
        }
    }
}
