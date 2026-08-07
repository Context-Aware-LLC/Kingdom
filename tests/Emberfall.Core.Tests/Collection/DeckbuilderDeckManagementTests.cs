using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The deck-management sequence the UI performs (EF-046): create a second deck, switch,
    /// set-default, delete — each step going through the real stores rather than an in-memory list,
    /// because every one of them is a write and the surface rebuilds itself by re-reading.
    ///
    /// <para>That surface was <c>DeckbuilderController</c>'s switcher bar when these were written;
    /// it is <c>CharacterRosterController</c>'s deck strip and its per-deck dialog since 2026-08-07.
    /// Nothing here changed with the move, which is the point of testing the core behaviours rather
    /// than the screen.</para>
    ///
    /// <para><b>Why this exists next to <see cref="DeckQueryTests"/>, which already covers ordering.</b>
    /// Those tests pin <see cref="DeckQuery.ForCharacter"/> against hand-built arrays. These pin the
    /// round trip the UI actually makes: store write, store re-read, next answer. A contract that holds
    /// for an array literal and breaks once it has been through JSON is exactly the failure the
    /// UI would hit and these would miss.</para>
    ///
    /// <para>The UI is not under test here — it is a MonoBehaviour and cannot be. What is under test is
    /// every core behaviour that screen's correctness rests on, so that a change to one of them fails
    /// here rather than silently in a scene nobody runs headlessly.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DeckbuilderDeckManagementTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-deckmgmt-tests", Guid.NewGuid().ToString("N"));
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

        private static Character Ashwarden(string defaultDeckId = "") =>
            new Character("chr-1", "Ashwarden", CardClass.EMBERWARD, CardClass.TIDECALLER, defaultDeckId);

        /// <summary>The deck strip's own call, re-read from disk every time — as the screen does.</summary>
        private CharacterDecks Switcher(Character character) =>
            DeckQuery.ForCharacter(character, Decks().LoadAll(), LiveCardSet.Db);

        // ---- create ("+") ---------------------------------------------------------------

        [Test]
        public void A_New_Deck_Is_Empty_Bound_To_The_Character_And_Visible_In_The_Switcher()
        {
            var character = Ashwarden(defaultDeckId: "starter");
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(7), "starter"));

            // What CharacterRosterController.NewDeck writes: empty, new id, bound at birth.
            Decks().Save(new DeckList { Id = "d2", CharacterId = character.Id, Name = "New Deck" });

            var switcher = Switcher(character);

            Assert.That(switcher.Count, Is.EqualTo(2));
            Assert.That(switcher.Decks.Select(d => d.Deck.Id), Is.EqualTo(new[] { "starter", "d2" }),
                "Default first, then stored order.");

            var made = switcher.Decks.Single(d => d.Deck.Id == "d2");
            Assert.That(made.TotalCount, Is.Zero, "A new deck is empty, not generated.");
            Assert.That(made.Deck.CharacterId, Is.EqualTo("chr-1"), "L5 binding survives the round trip.");
            Assert.That(made.IsDraft, Is.True, "An empty deck is a savable draft, never queue-eligible.");
        }

        [Test]
        public void An_Illegal_Draft_Saves_And_Reloads_Unchanged()
        {
            // The screen's central ruling (collection-deckbuilder-spec §1): save is never
            // legality-gated, so a half-built deck must survive a write/read intact.
            var character = Ashwarden();
            Characters().Save(character);

            var draft = new DeckList { Id = "d1", CharacterId = character.Id, Name = "Half Built" };
            draft.Entries.Add(new DeckEntry(LiveCardSet.Db.AllCollectible().First(c => character.AllowsClass(c.Class)).Id, 2));
            Decks().Save(draft);

            var reloaded = Decks().LoadAll().Single(d => d.Id == "d1");

            Assert.That(reloaded.Name, Is.EqualTo("Half Built"));
            Assert.That(reloaded.TotalCount, Is.EqualTo(2));
            Assert.That(QueueEligibility.Check(reloaded, character, LiveCardSet.Db).IsEligible, Is.False,
                "Persisted happily; still refused a match.");
        }

        // ---- rename ---------------------------------------------------------------------

        [Test]
        public void A_Rename_Reaches_Disk_Through_Save_And_Keeps_Its_Cards()
        {
            var character = Ashwarden();
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(3), "d1"));

            var open = Decks().LoadAll().Single(d => d.Id == "d1");
            int before = open.TotalCount;
            open.Name = "Tide and Ash";          // what the name field does, in memory
            Decks().Save(open);                  // and what Save/Done/switch-away then does

            var reloaded = Decks().LoadAll().Single(d => d.Id == "d1");
            Assert.That(reloaded.Name, Is.EqualTo("Tide and Ash"));
            Assert.That(reloaded.TotalCount, Is.EqualTo(before), "A rename is not a deck edit.");
        }

        // ---- set default ----------------------------------------------------------------

        [Test]
        public void Set_Default_Moves_The_Deck_To_The_Front_And_Survives_A_Reload()
        {
            var character = Ashwarden(defaultDeckId: "d1");
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(1), "d1"));
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(2), "d2"));

            Assert.That(Switcher(character).Decks[0].Deck.Id, Is.EqualTo("d1"));

            // CharacterRosterController.SetDefaultDeck: write the deck, then point the character at it.
            character.DefaultDeckId = "d2";
            Characters().Save(character);

            var afterReload = Characters().LoadAll().Single(c => c.Id == "chr-1");
            Assert.That(afterReload.DefaultDeckId, Is.EqualTo("d2"), "The pointer is persisted, not just held.");

            var switcher = Switcher(afterReload);
            Assert.That(switcher.Decks[0].Deck.Id, Is.EqualTo("d2"));
            Assert.That(switcher.Decks[0].IsDefault, Is.True);
            Assert.That(switcher.DefaultResolved, Is.True);
        }

        // ---- delete ---------------------------------------------------------------------

        [Test]
        public void Deleting_A_Non_Default_Deck_Leaves_The_Default_Alone()
        {
            var character = Ashwarden(defaultDeckId: "d1");
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(1), "d1"));
            Decks().Save(new DeckList { Id = "d2", CharacterId = character.Id, Name = "Scratch" });

            Decks().Delete("d2");

            var switcher = Switcher(character);
            Assert.That(switcher.Count, Is.EqualTo(1));
            Assert.That(switcher.Decks[0].Deck.Id, Is.EqualTo("d1"));
            Assert.That(switcher.DefaultResolved, Is.True);
        }

        [Test]
        public void Deleting_The_Default_Deck_Leaves_The_Pointer_Dangling_Rather_Than_Repairing_It()
        {
            // The contract is on Character.DefaultDeckId itself: "a dangling id is surfaced, never
            // repaired". Silently re-pointing would open the character on a deck nobody chose, so the
            // roster must NOT clear this — it renders the dangle instead. This test is what stops
            // a later "tidy-up" from quietly adding the repair.
            var character = Ashwarden(defaultDeckId: "d1");
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(1), "d1"));
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(2), "d2"));

            Decks().Delete("d1");

            var reloaded = Characters().LoadAll().Single(c => c.Id == "chr-1");
            Assert.That(reloaded.DefaultDeckId, Is.EqualTo("d1"),
                "The id still names the deleted deck — untouched, on purpose.");

            var switcher = Switcher(reloaded);
            Assert.That(switcher.DefaultResolved, Is.False, "…and the screen is told the default is unresolvable.");
            Assert.That(switcher.Decks.Any(d => d.IsDefault), Is.False);
            Assert.That(CharacterQuery.DefaultDeckOf(reloaded, Decks().LoadAll()), Is.Null);
        }

        [Test]
        public void After_Deleting_The_Open_Deck_There_Is_Still_Something_To_Open()
        {
            // A delete is followed by a rebuild that re-reads DecksOf(...), and DeckbuilderController
            // opens on .Opening. That must be non-null whenever the delete was allowed at all, or the
            // next screen dereferences null.
            var character = Ashwarden(defaultDeckId: "d1");
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(1), "d1"));
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(2), "d2"));

            Decks().Delete("d1");

            var opening = Switcher(Characters().LoadAll().Single(c => c.Id == "chr-1")).Opening;
            Assert.That(opening, Is.Not.Null);
            Assert.That(opening!.Value.Deck.Id, Is.EqualTo("d2"));
        }

        [Test]
        public void The_Last_Deck_Is_The_One_The_Screen_Refuses_To_Delete()
        {
            // The guard is in the controller, not the store — the store will happily delete the last
            // one. This pins WHY the guard has to exist: with zero decks, Opening is null and
            // DeckbuilderController.OnEnable bails out with "no deck to open".
            var character = Ashwarden(defaultDeckId: "d1");
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(1), "d1"));

            Assert.That(Switcher(character).Count, Is.EqualTo(1), "One deck: the delete button is disabled here.");

            Decks().Delete("d1");

            Assert.That(Switcher(character).Opening, Is.Null,
                "Had the screen allowed it, this is the state it would have to open in.");
        }

        // ---- switching ------------------------------------------------------------------

        [Test]
        public void Switching_Away_Persists_The_Outgoing_Deck()
        {
            // _deck is the only copy of unsaved edits; SwitchTo saves before repointing.
            var character = Ashwarden(defaultDeckId: "d1");
            Characters().Save(character);
            Decks().Save(DefaultDeckGenerator.Generate(character, LiveCardSet.Db, new DeterministicRandom(1), "d1"));
            Decks().Save(new DeckList { Id = "d2", CharacterId = character.Id, Name = "Other" });

            var outgoing = Decks().LoadAll().Single(d => d.Id == "d1");
            outgoing.Entries.RemoveAt(0);            // an edit made just before clicking another chip
            int edited = outgoing.TotalCount;
            Decks().Save(outgoing);                  // SwitchTo's save-on-leave

            Assert.That(Decks().LoadAll().Single(d => d.Id == "d1").TotalCount, Is.EqualTo(edited),
                "The edit outlived the switch.");
        }
    }
}
