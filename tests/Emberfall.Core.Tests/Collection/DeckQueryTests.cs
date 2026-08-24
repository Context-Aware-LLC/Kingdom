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
    /// <see cref="DeckQuery"/> — the sidebar's one call (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.7).
    ///
    /// <para>What is under test is a contract about <i>order and completeness</i>: default first,
    /// stored order behind it, one <see cref="QueueCheck"/> per deck, and a stated answer for the two
    /// broken references the store deliberately does not repair — a default deck that was deleted, and
    /// a deck whose character was.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DeckQueryTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-deckquery-tests", Guid.NewGuid().ToString("N"));
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

        private static Character Rotbloom(string defaultDeckId = "") =>
            new Character("chr-1", "Rotbloom", CardClass.GRAVEKEEP, CardClass.VERDANT, defaultDeckId);

        private static DeckList Empty(string id, string characterId, string name = "Deck") =>
            new DeckList { Id = id, CharacterId = characterId, Name = name };

        private static DeckList Legal(Character c, string id, ulong seed = 7) =>
            DefaultDeckGenerator.Generate(c, LiveCardSet.Db, new DeterministicRandom(seed), id);

        private static List<string> Ids(CharacterDecks result) => result.Decks.Select(d => d.Deck.Id).ToList();

        // ---- membership and order ------------------------------------------------------

        [Test]
        public void Only_This_Characters_Decks_Are_Returned()
        {
            var mine = Rotbloom();
            var decks = new[] { Empty("d1", "chr-1"), Empty("d2", "chr-OTHER"), Empty("d3", "chr-1") };

            Assert.That(Ids(DeckQuery.ForCharacter(mine, decks, LiveCardSet.Db)), Is.EqualTo(new[] { "d1", "d3" }));
        }

        [Test]
        public void The_Default_Comes_First_And_Everything_Else_Keeps_Stored_Order()
        {
            // Stored order is the ruling (no timestamp on DeckList — see DeckQuery.ForCharacter): the
            // switcher must not reshuffle itself between visits.
            var character = Rotbloom(defaultDeckId: "d3");
            var decks = new[] { Empty("d1", "chr-1"), Empty("d2", "chr-1"), Empty("d3", "chr-1"), Empty("d4", "chr-1") };

            var result = DeckQuery.ForCharacter(character, decks, LiveCardSet.Db);

            Assert.That(Ids(result), Is.EqualTo(new[] { "d3", "d1", "d2", "d4" }));
            Assert.That(result.Decks[0].IsDefault, Is.True);
            Assert.That(result.Decks.Skip(1).Any(d => d.IsDefault), Is.False, "Exactly one row is the default.");
            Assert.That(result.DefaultResolved, Is.True);
        }

        [Test]
        public void Stored_Order_Is_What_The_Real_Store_Returns()
        {
            // The ordering claim is about IDeckStore.LoadAll, so prove it through the store rather
            // than an array literal that only agrees with itself.
            var decks = Decks();
            foreach (var id in new[] { "d1", "d2", "d3" }) decks.Save(Empty(id, "chr-1", id));
            var character = Rotbloom(defaultDeckId: "d2");
            Characters().Save(character);

            var result = DeckQuery.ForCharacter(Characters().LoadAll().Single(), decks.LoadAll(), LiveCardSet.Db);

            Assert.That(Ids(result), Is.EqualTo(new[] { "d2", "d1", "d3" }));
        }

        // ---- the default that does not resolve -----------------------------------------

        [Test]
        public void An_Unset_Default_Opens_On_The_First_Stored_Deck_And_Says_So()
        {
            var result = DeckQuery.ForCharacter(Rotbloom(), new[] { Empty("d1", "chr-1"), Empty("d2", "chr-1") }, LiveCardSet.Db);

            Assert.That(result.DefaultResolved, Is.False);
            Assert.That(result.Opening!.Value.Deck.Id, Is.EqualTo("d1"));
            Assert.That(result.Opening!.Value.IsDefault, Is.False, "Opening on it does not make it the default.");
        }

        [Test]
        public void A_Deleted_Default_Surfaces_Rather_Than_Repairing()
        {
            // §3: the sidebar opens on the first remaining deck AND says the default is missing.
            var character = Rotbloom(defaultDeckId: "d-gone");
            var result = DeckQuery.ForCharacter(character, new[] { Empty("d1", "chr-1") }, LiveCardSet.Db);

            Assert.That(result.DefaultResolved, Is.False, "This bool is how the UI says 'the default is missing'.");
            Assert.That(result.Opening!.Value.Deck.Id, Is.EqualTo("d1"));
            Assert.That(character.DefaultDeckId, Is.EqualTo("d-gone"), "The query reports; it never rewrites.");
        }

        [Test]
        public void A_Default_Pointing_At_Another_Characters_Deck_Does_Not_Resolve()
        {
            // Not reachable through the UI, but reachable through a hand-edited file — and it must not
            // pull a foreign deck into this character's switcher.
            var character = Rotbloom(defaultDeckId: "d-theirs");
            var decks = new[] { Empty("d-theirs", "chr-OTHER"), Empty("d1", "chr-1") };

            var result = DeckQuery.ForCharacter(character, decks, LiveCardSet.Db);

            Assert.That(Ids(result), Is.EqualTo(new[] { "d1" }));
            Assert.That(result.DefaultResolved, Is.False);
        }

        [Test]
        public void A_Character_With_No_Decks_Has_Nothing_To_Open_On()
        {
            var result = DeckQuery.ForCharacter(Rotbloom(), Array.Empty<DeckList>(), LiveCardSet.Db);

            Assert.That(result.Count, Is.Zero);
            Assert.That(result.Opening, Is.Null);
            Assert.That(result.DefaultResolved, Is.False);
        }

        [Test]
        public void Null_Inputs_Return_Empty_Rather_Than_Throwing()
        {
            Assert.That(DeckQuery.ForCharacter(null!, new[] { Empty("d1", "chr-1") }, LiveCardSet.Db).Count, Is.Zero);
            Assert.That(DeckQuery.ForCharacter(Rotbloom(), null!, LiveCardSet.Db).Count, Is.Zero);
        }

        // ---- legality, resolved once, per deck ------------------------------------------

        [Test]
        public void Each_Row_Carries_Its_Own_Queue_Standing()
        {
            var character = Rotbloom(defaultDeckId: "d-legal");
            var decks = new List<DeckList> { Legal(character, "d-legal"), Empty("d-draft", "chr-1", "Half-built") };

            var result = DeckQuery.ForCharacter(character, decks, LiveCardSet.Db);

            Assert.That(result.Decks[0].IsDraft, Is.False, "The generated starter is queue-eligible.");
            Assert.That(result.Decks[1].IsDraft, Is.True);
            Assert.That(result.Decks[1].Queue.Reason, Is.EqualTo("Add 30 more cards."));
            Assert.That(result.Decks[1].TotalCount, Is.Zero, "The row's n of 'n / 30'.");
        }

        [Test]
        public void The_Rows_Verdict_Is_The_Same_Gate_Match_Join_Uses()
        {
            // The draft badge disagreeing with the queue is the exact bug this query exists to prevent.
            var character = Rotbloom(defaultDeckId: "d-legal");
            var decks = new List<DeckList> { Legal(character, "d-legal"), Empty("d-draft", "chr-1") };

            foreach (var row in DeckQuery.ForCharacter(character, decks, LiveCardSet.Db).Decks)
                Assert.That(row.IsDraft, Is.EqualTo(QueueEligibility.IsDraft(row.Deck, character, LiveCardSet.Db)),
                    $"Deck '{row.Deck.Id}'.");
        }

        // ---- orphans, which belong to no character's list -------------------------------

        [Test]
        public void Orphans_Are_The_Decks_No_Character_Claims()
        {
            var roster = new[] { Rotbloom() };
            var decks = new[] { Empty("d1", "chr-1"), Empty("d2", "chr-GONE"), Empty("d3", "") };

            Assert.That(DeckQuery.Orphans(decks, roster).Select(d => d.Id), Is.EqualTo(new[] { "d2", "d3" }));
        }

        [Test]
        public void The_Character_Store_Does_Not_Reach_Into_The_Deck_Store()
        {
            // Deleting a character deletes its decks (Sam, 2026-07-26) — but that cascade is
            // CharacterDeletion's job, a composition over both stores. ICharacterStore.Delete on its
            // own still touches only characters, which is what keeps the stores independent and what
            // makes the crash-order guarantee expressible at all.
            var characters = Characters();
            var decks = Decks();
            var character = Rotbloom();
            characters.Save(character);
            decks.Save(Legal(character, "d1"));

            characters.Delete("chr-1");

            var stranded = DeckQuery.Orphans(decks.LoadAll(), characters.LoadAll());
            Assert.That(stranded.Count, Is.EqualTo(1), "The store deleted a character, nothing more.");
            Assert.That(stranded[0].TotalCount, Is.EqualTo(DeckValidator.DeckSize), "And intact — a store never filters.");
        }

        [Test]
        public void Every_Orphan_Is_A_Draft_By_Construction()
        {
            var roster = Array.Empty<Character>();
            var decks = new[] { Legal(Rotbloom(), "d1") };

            foreach (var orphan in DeckQuery.Orphans(decks, roster))
                Assert.That(QueueEligibility.IsDraft(orphan, roster, LiveCardSet.Db), Is.True,
                    "No character means L5, so an orphan can never be queued.");
        }

        [Test]
        public void An_Orphan_Appears_In_No_Characters_Switcher()
        {
            // Stated as a test because it is the cost of the design: the sidebar reaches decks only
            // through a Character, so the deck-pick chooser (§2.11) is the surface that shows an
            // orphan at all.
            var character = Rotbloom();
            var decks = new[] { Empty("d1", "chr-1"), Empty("d-orphan", "chr-GONE") };

            Assert.That(Ids(DeckQuery.ForCharacter(character, decks, LiveCardSet.Db)), Does.Not.Contain("d-orphan"));
            Assert.That(DeckQuery.Orphans(decks, new[] { character }).Single().Id, Is.EqualTo("d-orphan"));
        }

        [Test]
        public void The_Roster_And_Its_Orphans_Account_For_Every_Saved_Deck()
        {
            // What the deck-pick screen relies on to be honest (§2.11). It draws one row per
            // character from ForCharacter and one trailing row from Orphans; if those two together
            // could miss a deck, a deck would exist on disk with no surface in the game that shows
            // it — which is exactly the state an orphan was in before the trailing row existed.
            var roster = new[] { Rotbloom() };
            var decks = new[]
            {
                Empty("d1", "chr-1"), Empty("d2", "chr-1"),
                Empty("d3", "chr-GONE"), Empty("d4", ""),
            };

            var drawn = roster.SelectMany(c => Ids(DeckQuery.ForCharacter(c, decks, LiveCardSet.Db)))
                              .Concat(DeckQuery.Orphans(decks, roster).Select(d => d.Id))
                              .ToList();

            Assert.That(drawn, Is.Unique, "A deck drawn twice is a deck the player can edit from two places.");
            Assert.That(drawn, Is.EquivalentTo(decks.Select(d => d.Id)));
        }

        [Test]
        public void An_Orphans_Reason_Is_The_Shared_L5_Message()
        {
            // The orphan row shows QueueCheck.Reason verbatim rather than copy of its own. Pinned
            // because the failure is silent: a hand-written "this character is gone" on the row
            // would still READ correctly while having stopped agreeing with the gate that produced
            // it, and nothing in the game compares the two.
            var roster = Array.Empty<Character>();
            var orphan = DeckQuery.Orphans(new[] { Legal(Rotbloom(), "d1") }, roster).Single();

            QueueCheck queue = QueueEligibility.Check(orphan, roster, LiveCardSet.Db);

            Assert.That(queue.IsEligible, Is.False, "An orphan is never selectable at deck pick.");
            Assert.That(queue.Reason, Is.Not.Empty, "Disabled without a reason is a dead end.");
            Assert.That(queue.Validation.Violations.Any(v => v.Rule == DeckRule.L5_CharacterBound), Is.True);
            Assert.That(queue.Reason, Is.EqualTo(queue.Validation.Violations[0].Message));
        }
    }
}
