using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The file-backed <see cref="IDeckStore"/>
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3).
    ///
    /// <para>The behaviours worth pinning are the ones §3 states as requirements rather than the
    /// CRUD: a missing file is an empty collection but a corrupt one is <b>not</b>; a deck whose
    /// cards no longer exist still round-trips intact so it can be shown as invalid; and the store
    /// is scoped by an identity key it does not mint.</para>
    ///
    /// <para><b>THE CHARACTER STORE HALF OF THIS FILE IS GONE (ADR-0009).</b> <c>JsonCharacterStore</c>
    /// was deleted with the type, and with it went the round-trip, the upsert-order and the
    /// invalid-pairing tests — <b>each of which has a deck equivalent below, because the pairing
    /// moved rather than disappearing</b>. One test had no equivalent and was not ported:
    /// <c>Delete_Is_Idempotent_And_Leaves_Bound_Decks_Alone</c>, which pinned that deleting a
    /// character left its decks on disk as L5-invalid ORPHANS rather than cascading them away. There
    /// is nothing to orphan a deck from.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class JsonCollectionStoreTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "kingdom-collection-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp dir we could not clean is not a test failure */ }
        }

        private JsonDeckStore Decks(string key = "device-token") => JsonDeckStore.InDirectory(_root, key);

        /// <summary>A deck on the Gravekeep/Verdant pairing. It was a <c>Character</c> of the same
        /// name holding those two classes until ADR-0009.</summary>
        private static DeckList Rotbloom(string id = "deck-1", string name = "Rotbloom")
            => new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT) { Id = id, Name = name };

        // ---- decks ---------------------------------------------------------------------

        [Test]
        public void First_Run_Is_An_Empty_Collection_Not_An_Error()
        {
            Assert.That(Decks().LoadAll(), Is.Empty);
        }

        [Test]
        public void A_Deck_Round_Trips_Its_Pairing_Through_Disk()
        {
            var store = Decks();
            store.Save(Rotbloom());

            // A second instance over the same path — the state must be the file, not the object.
            var loaded = Decks().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Id, Is.EqualTo("deck-1"));
            Assert.That(loaded[0].Name, Is.EqualTo("Rotbloom"));
            Assert.That(loaded[0].PrimaryClass, Is.EqualTo(CardClass.GRAVEKEEP));
            Assert.That(loaded[0].SecondaryClass, Is.EqualTo(CardClass.VERDANT));
        }

        [Test]
        public void Classes_Persist_As_Names_So_Renumbering_The_Enum_Cannot_Rewrite_A_Collection()
        {
            Decks().Save(Rotbloom());
            string json = File.ReadAllText(Decks().FilePath);

            Assert.That(json, Does.Contain("\"GRAVEKEEP\""));
            Assert.That(json, Does.Contain("\"VERDANT\""));
            Assert.That(json, Does.Not.Contain("\"primaryClass\": 3"),
                "Ordinals would silently repoint every saved deck the day a class is inserted.");
        }

        [Test]
        public void Save_Refuses_An_Invalid_Pairing()
        {
            // The one guard the store applies to a deck, moved here from JsonCharacterStore.Save
            // with the pairing itself. A Pyra+Pyra deck is a state with no legal repair, because the
            // pairing is immutable by design and the player could only delete the deck.
            var store = Decks();

            Assert.Throws<ArgumentException>(() =>
                store.Save(new DeckList(CardClass.EMBERWARD, CardClass.EMBERWARD) { Id = "d-x" }));
            Assert.Throws<ArgumentException>(() =>
                store.Save(new DeckList(CardClass.EMBERWARD, CardClass.NEUTRAL) { Id = "d-x" }));
            Assert.Throws<ArgumentException>(() => store.Save(Rotbloom(id: "")));

            Assert.That(store.LoadAll(), Is.Empty, "A rejected Save writes nothing at all.");
        }

        [Test]
        public void A_Deck_Round_Trips_With_Its_Entries_In_Order()
        {
            var store = Decks();
            var deck = Rotbloom(name: "Rotvine");
            deck.Entries.Add(new DeckEntry(FixtureCard.Id("grave_bell_novice"), 2));
            deck.Entries.Add(new DeckEntry(FixtureCard.Id("verdant_sapsurge"), 1));
            store.Save(deck);

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Name, Is.EqualTo("Rotvine"));
            Assert.That(loaded[0].Entries.Select(e => (e.CardId, e.Count)),
                Is.EqualTo(new[] { (FixtureCard.Id("grave_bell_novice"), 2), (FixtureCard.Id("verdant_sapsurge"), 1) }));
            Assert.That(loaded[0].TotalCount, Is.EqualTo(3));
        }

        [Test]
        public void A_Deck_Naming_Cards_That_No_Longer_Exist_Still_Loads()
        {
            // §3 again, from the storage side: the store must not filter unknown ids on the way out,
            // or the L4 "Unknown card" violation can never be raised.
            var store = Decks();
            var deck = Rotbloom(name: "Old");
            deck.Entries.Add(new DeckEntry(FixtureCard.Id("card_deleted_by_a_balance_patch"), 2));
            store.Save(deck);

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Entries[0].CardId, Is.EqualTo(FixtureCard.Id("card_deleted_by_a_balance_patch")));
            Assert.That(DeckValidator.Validate(loaded[0], LiveCardSet.Db).IsValid, Is.False);
        }

        [Test]
        public void Deck_Save_Upserts_And_Delete_Removes()
        {
            var store = Decks();
            store.Save(Rotbloom("deck-1", "A"));
            store.Save(Rotbloom("deck-2", "B"));
            store.Save(Rotbloom("deck-1", "A renamed"));

            Assert.That(store.LoadAll().Select(d => d.Name), Is.EqualTo(new[] { "A renamed", "B" }));

            store.Delete("deck-1");
            Assert.That(store.LoadAll().Select(d => d.Id), Is.EqualTo(new[] { "deck-2" }));

            store.Delete("deck-2");
            Assert.That(store.LoadAll(), Is.Empty);
            Assert.That(File.Exists(store.FilePath), Is.True, "An emptied collection is an empty file, not a missing one.");
        }

        [TestCase("\"gravekeep_allover\"", 1)]
        [TestCase("\"\"", 0)]
        [TestCase("null", 0)]
        [TestCase("42", 42)]
        public void Card_back_legacy_values_migrate_to_numbers(string value, int expected)
        {
            var json = CollectionJson.WriteDecks(new[] { Rotbloom("d1", "Legacy") })
                .Replace("\"cardBackId\": 0", "\"cardBackId\": " + value);
            var deck = CollectionJson.ReadDecks(json)[0];
            Assert.That(deck.CardBackId, Is.EqualTo(expected));
            Assert.That(CollectionJson.WriteDecks(new[] { deck }), Does.Contain("\"cardBackId\": " + expected));
        }

        [TestCase("-1")]
        [TestCase("1.5")]
        [TestCase("2147483648")]
        [TestCase("\"future_handle\"")]
        [TestCase("true")]
        public void Malformed_card_back_identity_is_not_silently_replaced(string value)
        {
            var json = CollectionJson.WriteDecks(new[] { Rotbloom("d1", "Bad") })
                .Replace("\"cardBackId\": 0", "\"cardBackId\": " + value);
            Assert.Throws<CollectionFormatException>(() => CollectionJson.ReadDecks(json));
        }

        [Test]
        public void A_Deck_Round_Trips_Its_Card_Back_And_Survives_One_This_Build_Does_Not_Know()
        {
            var store = Decks();
            var chosen = Rotbloom("deck-1", "Chosen");
            chosen.CardBackId = 1;
            var later = Rotbloom("deck-3", "From a later build");
            later.CardBackId = 42;
            store.Save(chosen);
            store.Save(Rotbloom("deck-2", "Unset"));
            store.Save(later);

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Select(d => d.CardBackId), Is.EqualTo(new[]
            {
                1,
                0,                                           // never chosen == the build default
                42,   // unknown is cosmetic, never a read error
            }));
        }

        [Test]
        public void A_Deck_Round_Trips_Its_Showcase_Card_Even_When_It_Is_No_Longer_In_The_Deck()
        {
            // The image a deck is recognised by. It is allowed to name a card the deck no longer
            // holds: the client falls back for the render, and keeping the id means putting the card
            // back restores the picture the player chose rather than silently losing it.
            var store = Decks();
            var deck = Rotbloom(name: "Rotvine");
            deck.ShowcaseCardId = FixtureCard.Id("grave_bell_novice");
            store.Save(deck);
            store.Save(Rotbloom("deck-2", "Unset"));

            var loaded = Decks().LoadAll();

            Assert.That(loaded[0].ShowcaseCardId, Is.EqualTo(FixtureCard.Id("grave_bell_novice")));
            Assert.That(loaded[0].Entries, Is.Empty, "…and the card it names is not in the deck at all.");
            Assert.That(loaded[1].ShowcaseCardId, Is.Zero);
        }

        [Test]
        public void A_Deck_Written_Before_Card_Backs_Existed_Still_Loads()
        {
            // The additive-field contract, from the storage side: cardBackId is absent from every
            // deck any shipped build has written so far, and its absence must not be a format error.
            var store = Decks();
            store.Save(Rotbloom("seed"));   // the scope directory exists only once something is written
            File.WriteAllText(store.FilePath, $$"""
                {
                  "schemaVersion": 1,
                  "decks": [
                    { "id": "deck-1", "primaryClass": "GRAVEKEEP", "secondaryClass": "VERDANT",
                      "name": "Rotvine",
                      "cards": [ { "id": {{FixtureCard.Id("grave_bell_novice")}}, "count": 2 } ] }
                  ]
                }
                """);

            var loaded = Decks().LoadAll();

            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That(loaded[0].Name, Is.EqualTo("Rotvine"));
            Assert.That(loaded[0].CardBackId, Is.Zero);
        }

        [Test]
        public void Deck_Save_Requires_An_Id()
        {
            Assert.Throws<ArgumentException>(() => Decks().Save(Rotbloom(id: "", name: "Nameless")));
        }

        // ---- corruption ----------------------------------------------------------------

        [Test]
        public void A_Corrupt_File_Throws_Rather_Than_Reading_As_Empty()
        {
            // The dangerous failure is not the exception, it is the alternative: returning an empty
            // roster from an unreadable file means the next Save overwrites a recoverable collection
            // with one character.
            var store = Decks();
            store.Save(Rotbloom());
            File.WriteAllText(store.FilePath, "{ this is not json");

            Assert.Throws<CollectionFormatException>(() => store.LoadAll());
        }

        [Test]
        public void An_Unknown_Class_Name_Throws_Instead_Of_Guessing()
        {
            var store = Decks();
            store.Save(Rotbloom());
            File.WriteAllText(store.FilePath,
                File.ReadAllText(store.FilePath).Replace("\"GRAVEKEEP\"", "\"NECROMANCER\""));

            var ex = Assert.Throws<CollectionFormatException>(() => store.LoadAll());
            Assert.That(ex!.Message, Does.Contain("NECROMANCER"));
        }

        [Test]
        public void A_File_From_A_Newer_Build_Is_Refused_Not_Downgraded()
        {
            var store = Decks();
            store.Save(Rotbloom());
            File.WriteAllText(store.FilePath,
                File.ReadAllText(store.FilePath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));

            Assert.Throws<CollectionFormatException>(() => store.LoadAll());
        }

        [Test]
        public void A_Failed_Write_Leaves_No_Stray_Temp_File()
        {
            var store = Decks();
            store.Save(Rotbloom());
            store.Save(new DeckList(CardClass.EMBERWARD, CardClass.TIDECALLER) { Id = "deck-2", Name = "Ashtide" });

            var stray = Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp");
            Assert.That(stray, Is.Empty, "The atomic swap must consume its temp file.");
        }

        // ---- identity scoping ----------------------------------------------------------

        [Test]
        public void Two_Device_Tokens_Get_Two_Collections()
        {
            JsonDeckStore.InDirectory(_root, "token-aaa").Save(Rotbloom("d-a"));
            JsonDeckStore.InDirectory(_root, "token-bbb").Save(Rotbloom("d-b"));

            Assert.That(JsonDeckStore.InDirectory(_root, "token-aaa").LoadAll().Single().Id, Is.EqualTo("d-a"));
            Assert.That(JsonDeckStore.InDirectory(_root, "token-bbb").LoadAll().Single().Id, Is.EqualTo("d-b"));
        }

        [Test]
        public void A_Store_Takes_Any_Path_Its_Caller_Composes()
        {
            // The constructor takes a path, not a policy — this is what keeps the assembly free of
            // UnityEngine while Unity still scopes it to Application.persistentDataPath.
            string custom = Path.Combine(_root, "somewhere", "else", "decks.json");
            var store = new JsonDeckStore(custom);
            store.Save(Rotbloom());

            Assert.That(File.Exists(custom), Is.True, "The store creates directories it was pointed at.");
            Assert.That(new JsonDeckStore(custom).LoadAll().Single().Id, Is.EqualTo("deck-1"));
        }

        // ── ReplaceAll: the cache must never be able to block its own repair ─────────────

        /// <summary>
        /// The failure this was added for, in the shape it actually arrived in.
        ///
        /// <para><b>Measured on a device, 2026-09-02.</b> An Android auto-backup restored a
        /// <c>decks.json</c> written before card ids became numbers. Every read-modify-write path —
        /// <c>Save</c>, <c>Delete</c>, and the roster sync's own "load what is there, then delete
        /// what the server no longer has" — begins by reading, so every one of them threw. The
        /// server held the authoritative answer and there was no way to write it down: the local
        /// cache was blocking the only thing that could have repaired it.</para>
        /// </summary>
        [Test]
        public void ReplaceAll_Overwrites_A_File_That_LoadAll_Refuses_To_Read()
        {
            var decks = Decks();
            string path = decks.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // A real pre-migration deck: the card id is a HANDLE, which this build's reader rejects
            // by design so a stale id can never be silently dropped.
            File.WriteAllText(path,
                "{\"schemaVersion\":1,\"decks\":[{\"id\":\"d-old\",\"characterId\":\"chr-1\"," +
                "\"name\":\"Old\",\"cards\":[{\"id\":\"clock_mainspring_halberd\",\"count\":1}]}]}");

            Assert.That(() => decks.LoadAll(), Throws.TypeOf<CollectionFormatException>(),
                "precondition: the file is exactly the kind this store refuses to read");

            var replacement = Rotbloom("d-new", "From the server");
            replacement.Entries.Add(new DeckEntry(1001, 2));

            Assert.That(() => decks.ReplaceAll(new[] { replacement }), Throws.Nothing,
                "ReplaceAll must not read the file it replaces — that is the whole reason it exists");

            var after = decks.LoadAll();
            Assert.That(after.Count, Is.EqualTo(1));
            Assert.That(after[0].Id, Is.EqualTo("d-new"));
            Assert.That(after[0].CountOf(1001), Is.EqualTo(2));
        }

        [Test]
        public void ReplaceAll_Removes_Rows_That_Are_Not_In_The_New_Set()
        {
            var decks = Decks();
            decks.Save(Rotbloom("d-1", "One"));
            decks.Save(Rotbloom("d-2", "Two"));

            decks.ReplaceAll(new[] { Rotbloom("d-2", "Two") });

            Assert.That(decks.LoadAll().Single().Id, Is.EqualTo("d-2"),
                "a deck the server no longer has must go, or it resurrects on the next read");
        }

        [Test]
        public void ReplaceAll_With_Nothing_Empties_The_Store()
        {
            var decks = Decks();
            decks.Save(Rotbloom());

            decks.ReplaceAll(Array.Empty<DeckList>());

            Assert.That(decks.LoadAll(), Is.Empty,
                "an empty roster from the server is a real answer — a player who deleted their last " +
                "deck must not have it restored by the next sign-in");
        }

        [Test]
        public void ReplaceAll_Refuses_Null_Rather_Than_Emptying_The_Store()
        {
            var decks = Decks();
            decks.Save(Rotbloom("d-1", "One"));

            Assert.That(() => decks.ReplaceAll(null!), Throws.ArgumentNullException,
                "null is a caller bug, and treating it as 'replace with nothing' would delete a " +
                "roster on a mistake that costs nothing to catch");
            Assert.That(decks.LoadAll().Count, Is.EqualTo(1));
        }
    }
}
