using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The file-backed <see cref="ICharacterStore"/> / <see cref="IDeckStore"/>
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3).
    ///
    /// <para>The behaviours worth pinning are the ones §3 states as requirements rather than the
    /// CRUD: a missing file is an empty collection but a corrupt one is <b>not</b>; a deck whose
    /// character or cards no longer exist still round-trips intact so it can be shown as invalid;
    /// and the store is scoped by an identity key it does not mint.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class JsonCollectionStoreTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-collection-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp dir we could not clean is not a test failure */ }
        }

        private JsonCharacterStore Characters(string key = "device-token") => JsonCharacterStore.InDirectory(_root, key);
        private JsonDeckStore Decks(string key = "device-token") => JsonDeckStore.InDirectory(_root, key);

        private static Character Rotbloom(string id = "chr-1")
            => new Character(id, "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT);

        // ---- characters ----------------------------------------------------------------

        [Test]
        public void First_Run_Is_An_Empty_Roster_Not_An_Error()
        {
            Assert.That(Characters().LoadAll(), Is.Empty);
            Assert.That(Decks().LoadAll(), Is.Empty);
        }

        [Test]
        public void A_Character_Round_Trips_Through_Disk()
        {
            var store = Characters();
            store.Save(Rotbloom());

            // A second instance over the same path — the state must be the file, not the object.
            var loaded = Characters().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Id, Is.EqualTo("chr-1"));
            Assert.That(loaded[0].Name, Is.EqualTo("Rotbloom"));
            Assert.That(loaded[0].PrimaryClass, Is.EqualTo(CardClass.GRAVEKEEP));
            Assert.That(loaded[0].SecondaryClass, Is.EqualTo(CardClass.VERDANT));
        }

        [Test]
        public void Classes_Persist_As_Names_So_Renumbering_The_Enum_Cannot_Rewrite_A_Roster()
        {
            Characters().Save(Rotbloom());
            string json = File.ReadAllText(Characters().FilePath);

            Assert.That(json, Does.Contain("\"GRAVEKEEP\""));
            Assert.That(json, Does.Contain("\"VERDANT\""));
            Assert.That(json, Does.Not.Contain("\"primaryClass\": 3"),
                "Ordinals would silently repoint every saved character the day a class is inserted.");
        }

        [Test]
        public void Save_Upserts_By_Id_And_Keeps_Roster_Order()
        {
            var store = Characters();
            store.Save(Rotbloom("chr-1"));
            store.Save(new Character("chr-2", "Ashtide", CardClass.EMBERWARD, CardClass.TIDECALLER));
            store.Save(new Character("chr-1", "Rotbloom the Renamed", CardClass.GRAVEKEEP, CardClass.VERDANT));

            var loaded = store.LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(2), "Re-saving an id updates in place, it does not append.");
            Assert.That(loaded[0].Id, Is.EqualTo("chr-1"), "Order is stable — the S6a panel must not reshuffle.");
            Assert.That(loaded[0].Name, Is.EqualTo("Rotbloom the Renamed"));
            Assert.That(loaded[1].Id, Is.EqualTo("chr-2"));
        }

        [Test]
        public void Save_Refuses_An_Invalid_Pairing()
        {
            var store = Characters();

            Assert.Throws<ArgumentException>(() =>
                store.Save(new Character("chr-x", "Doubled", CardClass.EMBERWARD, CardClass.EMBERWARD)));
            Assert.Throws<ArgumentException>(() =>
                store.Save(new Character("chr-x", "Half", CardClass.EMBERWARD, CardClass.NEUTRAL)));
            Assert.Throws<ArgumentException>(() => store.Save(Rotbloom(id: "")));

            Assert.That(store.LoadAll(), Is.Empty, "A rejected Save writes nothing at all.");
        }

        [Test]
        public void Delete_Is_Idempotent_And_Leaves_Bound_Decks_Alone()
        {
            var characters = Characters();
            var decks = Decks();
            characters.Save(Rotbloom());
            decks.Save(new DeckList { Id = "deck-1", CharacterId = "chr-1", Name = "Rot" });

            characters.Delete("chr-1");
            characters.Delete("chr-1");   // second delete must not throw
            characters.Delete("never-existed");

            Assert.That(characters.LoadAll(), Is.Empty);
            var survivors = decks.LoadAll();
            Assert.That(survivors.Count, Is.EqualTo(1),
                "§3: the orphaned deck surfaces as L5-invalid on open — it is not cascaded away.");
            Assert.That(survivors[0].CharacterId, Is.EqualTo("chr-1"));
            Assert.That(CharacterQuery.ById(characters.LoadAll(), survivors[0].CharacterId), Is.Null);
        }

        // ---- decks ---------------------------------------------------------------------

        [Test]
        public void A_Deck_Round_Trips_With_Its_Entries_In_Order()
        {
            var store = Decks();
            var deck = new DeckList { Id = "deck-1", CharacterId = "chr-1", Name = "Rotvine" };
            deck.Entries.Add(new DeckEntry("grave_bell_novice", 2));
            deck.Entries.Add(new DeckEntry("verdant_sapsurge", 1));
            store.Save(deck);

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Name, Is.EqualTo("Rotvine"));
            Assert.That(loaded[0].CharacterId, Is.EqualTo("chr-1"));
            Assert.That(loaded[0].Entries.Select(e => (e.CardId, e.Count)),
                Is.EqualTo(new[] { ("grave_bell_novice", 2), ("verdant_sapsurge", 1) }));
            Assert.That(loaded[0].TotalCount, Is.EqualTo(3));
        }

        [Test]
        public void A_Deck_Naming_Cards_That_No_Longer_Exist_Still_Loads()
        {
            // §3 again, from the storage side: the store must not filter unknown ids on the way out,
            // or the L4 "Unknown card" violation can never be raised.
            var store = Decks();
            var deck = new DeckList { Id = "deck-1", CharacterId = "chr-gone", Name = "Old" };
            deck.Entries.Add(new DeckEntry("card_deleted_by_a_balance_patch", 2));
            store.Save(deck);

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Entries[0].CardId, Is.EqualTo("card_deleted_by_a_balance_patch"));
            Assert.That(DeckValidator.Validate(loaded[0], Rotbloom(), LiveCardSet.Db).IsValid, Is.False);
        }

        [Test]
        public void Deck_Save_Upserts_And_Delete_Removes()
        {
            var store = Decks();
            store.Save(new DeckList { Id = "deck-1", Name = "A" });
            store.Save(new DeckList { Id = "deck-2", Name = "B" });
            store.Save(new DeckList { Id = "deck-1", Name = "A renamed" });

            Assert.That(store.LoadAll().Select(d => d.Name), Is.EqualTo(new[] { "A renamed", "B" }));

            store.Delete("deck-1");
            Assert.That(store.LoadAll().Select(d => d.Id), Is.EqualTo(new[] { "deck-2" }));

            store.Delete("deck-2");
            Assert.That(store.LoadAll(), Is.Empty);
            Assert.That(File.Exists(store.FilePath), Is.True, "An emptied collection is an empty file, not a missing one.");
        }

        [Test]
        public void A_Deck_Round_Trips_Its_Card_Back_And_Survives_One_This_Build_Does_Not_Know()
        {
            var store = Decks();
            store.Save(new DeckList { Id = "deck-1", CharacterId = "chr-1", Name = "Chosen", CardBackId = "gravekeep_allover" });
            store.Save(new DeckList { Id = "deck-2", CharacterId = "chr-1", Name = "Unset" });
            store.Save(new DeckList { Id = "deck-3", CharacterId = "chr-1", Name = "From a later build", CardBackId = "a_back_from_a_set_that_does_not_ship_yet" });

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Select(d => d.CardBackId), Is.EqualTo(new[]
            {
                "gravekeep_allover",
                "",                                          // never chosen == the build default
                "a_back_from_a_set_that_does_not_ship_yet",   // unknown is cosmetic, never a read error
            }));
        }

        [Test]
        public void A_Deck_Round_Trips_Its_Showcase_Card_Even_When_It_Is_No_Longer_In_The_Deck()
        {
            // The image a deck is recognised by. It is allowed to name a card the deck no longer
            // holds: the client falls back for the render, and keeping the id means putting the card
            // back restores the picture the player chose rather than silently losing it.
            var store = Decks();
            var deck = new DeckList
            {
                Id = "deck-1",
                CharacterId = "chr-1",
                Name = "Rotvine",
                ShowcaseCardId = "grave_bell_novice",
            };
            store.Save(deck);
            store.Save(new DeckList { Id = "deck-2", CharacterId = "chr-1", Name = "Unset" });

            var loaded = Decks().LoadAll();

            Assert.That(loaded[0].ShowcaseCardId, Is.EqualTo("grave_bell_novice"));
            Assert.That(loaded[0].Entries, Is.Empty, "…and the card it names is not in the deck at all.");
            Assert.That(loaded[1].ShowcaseCardId, Is.Empty);
        }

        [Test]
        public void A_Deck_Written_Before_Card_Backs_Existed_Still_Loads()
        {
            // The additive-field contract, from the storage side: cardBackId is absent from every
            // deck any shipped build has written so far, and its absence must not be a format error.
            var store = Decks();
            store.Save(new DeckList { Id = "seed" });   // the scope directory exists only once something is written
            File.WriteAllText(store.FilePath, """
                {
                  "schemaVersion": 1,
                  "decks": [
                    { "id": "deck-1", "characterId": "chr-1", "name": "Rotvine",
                      "cards": [ { "id": "grave_bell_novice", "count": 2 } ] }
                  ]
                }
                """);

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Name, Is.EqualTo("Rotvine"));
            Assert.That(loaded[0].CardBackId, Is.Empty);
        }

        [Test]
        public void Deck_Save_Requires_An_Id()
        {
            Assert.Throws<ArgumentException>(() => Decks().Save(new DeckList { Id = "", Name = "Nameless" }));
        }

        // ---- corruption ----------------------------------------------------------------

        [Test]
        public void A_Corrupt_File_Throws_Rather_Than_Reading_As_Empty()
        {
            // The dangerous failure is not the exception, it is the alternative: returning an empty
            // roster from an unreadable file means the next Save overwrites a recoverable collection
            // with one character.
            var store = Characters();
            store.Save(Rotbloom());
            File.WriteAllText(store.FilePath, "{ this is not json");

            Assert.Throws<CollectionFormatException>(() => store.LoadAll());
        }

        [Test]
        public void An_Unknown_Class_Name_Throws_Instead_Of_Guessing()
        {
            var store = Characters();
            store.Save(Rotbloom());
            File.WriteAllText(store.FilePath,
                File.ReadAllText(store.FilePath).Replace("\"GRAVEKEEP\"", "\"NECROMANCER\""));

            var ex = Assert.Throws<CollectionFormatException>(() => store.LoadAll());
            Assert.That(ex!.Message, Does.Contain("NECROMANCER"));
        }

        [Test]
        public void A_File_From_A_Newer_Build_Is_Refused_Not_Downgraded()
        {
            var store = Characters();
            store.Save(Rotbloom());
            File.WriteAllText(store.FilePath,
                File.ReadAllText(store.FilePath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));

            Assert.Throws<CollectionFormatException>(() => store.LoadAll());
        }

        [Test]
        public void A_Failed_Write_Leaves_No_Stray_Temp_File()
        {
            var store = Characters();
            store.Save(Rotbloom());
            store.Save(new Character("chr-2", "Ashtide", CardClass.EMBERWARD, CardClass.TIDECALLER));

            var stray = Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp");
            Assert.That(stray, Is.Empty, "The atomic swap must consume its temp file.");
        }

        // ---- identity scoping ----------------------------------------------------------

        [Test]
        public void Two_Device_Tokens_Get_Two_Collections()
        {
            JsonCharacterStore.InDirectory(_root, "token-aaa").Save(Rotbloom("chr-a"));
            JsonCharacterStore.InDirectory(_root, "token-bbb").Save(Rotbloom("chr-b"));

            Assert.That(JsonCharacterStore.InDirectory(_root, "token-aaa").LoadAll().Single().Id, Is.EqualTo("chr-a"));
            Assert.That(JsonCharacterStore.InDirectory(_root, "token-bbb").LoadAll().Single().Id, Is.EqualTo("chr-b"));
        }

        [Test]
        public void A_Store_Takes_Any_Path_Its_Caller_Composes()
        {
            // The constructor takes a path, not a policy — this is what keeps the assembly free of
            // UnityEngine while Unity still scopes it to Application.persistentDataPath.
            string custom = Path.Combine(_root, "somewhere", "else", "roster.json");
            var store = new JsonCharacterStore(custom);
            store.Save(Rotbloom());

            Assert.That(File.Exists(custom), Is.True, "The store creates directories it was pointed at.");
            Assert.That(new JsonCharacterStore(custom).LoadAll().Single().Id, Is.EqualTo("chr-1"));
        }
    }
}
