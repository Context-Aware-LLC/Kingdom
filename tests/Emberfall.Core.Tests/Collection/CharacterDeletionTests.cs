using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="CharacterDeletion"/> — deleting a character deletes its decks (Sam, 2026-07-26).
    ///
    /// <para>Two things are load-bearing and both are tested: the <b>count</b> the confirm dialog
    /// shows is the same set that actually gets destroyed, and the <b>order</b> is character-first so
    /// a crash mid-cascade lands on the survivable half.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CharacterDeletionTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-chardelete-tests", Guid.NewGuid().ToString("N"));
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

        private static Character Rotbloom(string id = "chr-1", string defaultDeckId = "") =>
            new Character(id, "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, defaultDeckId);

        private static DeckList Deck(string id, string characterId) =>
            new DeckList { Id = id, CharacterId = characterId, Name = id };

        // ---- the cascade ---------------------------------------------------------------

        [Test]
        public void Deleting_A_Character_Deletes_Every_Deck_Bound_To_It()
        {
            var characters = Characters();
            var decks = Decks();
            characters.Save(Rotbloom(defaultDeckId: "d1"));
            foreach (var id in new[] { "d1", "d2", "d3" }) decks.Save(Deck(id, "chr-1"));

            int destroyed = CharacterDeletion.Delete(characters, decks, "chr-1");

            Assert.That(destroyed, Is.EqualTo(3));
            Assert.That(characters.LoadAll(), Is.Empty);
            Assert.That(decks.LoadAll(), Is.Empty);
        }

        [Test]
        public void Another_Characters_Decks_Are_Untouched()
        {
            var characters = Characters();
            var decks = Decks();
            characters.Save(Rotbloom());
            characters.Save(new Character("chr-2", "Emberling", CardClass.EMBERWARD, CardClass.TIDECALLER));
            decks.Save(Deck("mine", "chr-1"));
            decks.Save(Deck("theirs", "chr-2"));

            CharacterDeletion.Delete(characters, decks, "chr-1");

            Assert.That(decks.LoadAll().Select(d => d.Id), Is.EqualTo(new[] { "theirs" }));
            Assert.That(characters.LoadAll().Single().Id, Is.EqualTo("chr-2"));
        }

        [Test]
        public void The_Dialogs_Count_Is_The_Set_That_Gets_Destroyed()
        {
            // §2.6: the confirm names the deck count. A dialog that under-counts what it destroys is
            // the one failure this cascade cannot afford, so the count and the deletion share a filter.
            var characters = Characters();
            var decks = Decks();
            characters.Save(Rotbloom());
            foreach (var id in new[] { "d1", "d2" }) decks.Save(Deck(id, "chr-1"));
            decks.Save(Deck("d3", "chr-2"));

            var previewed = CharacterDeletion.DecksOf("chr-1", decks.LoadAll());
            int destroyed = CharacterDeletion.Delete(characters, decks, "chr-1");

            Assert.That(previewed.Count, Is.EqualTo(2));
            Assert.That(destroyed, Is.EqualTo(previewed.Count));
        }

        [Test]
        public void A_Character_With_No_Decks_Deletes_Cleanly()
        {
            var characters = Characters();
            characters.Save(Rotbloom());

            Assert.That(CharacterDeletion.Delete(characters, Decks(), "chr-1"), Is.Zero);
            Assert.That(characters.LoadAll(), Is.Empty);
        }

        [Test]
        public void An_Unknown_Id_Destroys_Nothing()
        {
            var characters = Characters();
            var decks = Decks();
            characters.Save(Rotbloom());
            decks.Save(Deck("d1", "chr-1"));

            Assert.That(CharacterDeletion.Delete(characters, decks, "chr-NOPE"), Is.Zero);
            Assert.That(characters.LoadAll().Count, Is.EqualTo(1));
            Assert.That(decks.LoadAll().Count, Is.EqualTo(1));
        }

        [Test]
        public void Deleting_Leaves_No_Orphan_Behind()
        {
            // The whole reason the ruling changed: the normal path no longer manufactures orphans.
            var characters = Characters();
            var decks = Decks();
            characters.Save(Rotbloom(defaultDeckId: "d1"));
            decks.Save(Deck("d1", "chr-1"));

            CharacterDeletion.Delete(characters, decks, "chr-1");

            Assert.That(DeckQuery.Orphans(decks.LoadAll(), characters.LoadAll()), Is.Empty);
        }

        // ---- the crash order -----------------------------------------------------------

        [Test]
        public void A_Crash_Mid_Cascade_Leaves_Decks_Without_A_Character_Not_The_Reverse()
        {
            // Character first, then decks. If the deck sweep dies partway the residue is orphans —
            // reportable as L5 and deletable from the deck-pick chooser (O-4). The other order would
            // leave a live character whose decks had silently gone missing, which nothing surfaces.
            var characters = Characters();
            var real = Decks();
            characters.Save(Rotbloom());
            foreach (var id in new[] { "d1", "d2" }) real.Save(Deck(id, "chr-1"));

            var flaky = new ThrowsOnSecondDelete(real);
            Assert.Throws<IOException>(() => CharacterDeletion.Delete(characters, flaky, "chr-1"));

            Assert.That(characters.LoadAll(), Is.Empty, "The character went first.");
            var survivors = real.LoadAll();
            Assert.That(survivors.Count, Is.EqualTo(1), "One deck was swept before the failure.");
            Assert.That(DeckQuery.Orphans(survivors, characters.LoadAll()).Count, Is.EqualTo(1),
                "The residue is an orphan — visible, reportable, and deletable.");
        }

        [Test]
        public void Re_Running_The_Delete_Finishes_A_Crashed_Cascade()
        {
            var characters = Characters();
            var decks = Decks();
            decks.Save(Deck("d1", "chr-1"));   // residue: the character is already gone

            Assert.That(CharacterDeletion.Delete(characters, decks, "chr-1"), Is.EqualTo(1));
            Assert.That(decks.LoadAll(), Is.Empty);
        }

        [Test]
        public void Null_Stores_Are_Rejected_Before_Anything_Is_Destroyed()
        {
            Assert.Throws<ArgumentNullException>(() => CharacterDeletion.Delete(null!, Decks(), "chr-1"));
            Assert.Throws<ArgumentNullException>(() => CharacterDeletion.Delete(Characters(), null!, "chr-1"));
            Assert.That(CharacterDeletion.Delete(Characters(), Decks(), ""), Is.Zero);
        }

        /// <summary>A deck store that dies partway through the sweep, to pin the crash order.</summary>
        private sealed class ThrowsOnSecondDelete : IDeckStore
        {
            private readonly IDeckStore _inner;
            private int _deletes;
            public ThrowsOnSecondDelete(IDeckStore inner) { _inner = inner; }

            public IReadOnlyList<DeckList> LoadAll() => _inner.LoadAll();
            public void Save(DeckList deck) => _inner.Save(deck);
            public void Delete(string deckId)
            {
                if (++_deletes > 1) throw new IOException("disk went away mid-cascade");
                _inner.Delete(deckId);
            }
        }
    }
}
