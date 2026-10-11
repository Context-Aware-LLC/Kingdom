using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The deck-management sequence the UI performs (EF-046): create a second deck, rename, delete,
    /// switch — each step going through the real store rather than an in-memory list, because every
    /// one of them is a write and the surface rebuilds itself by re-reading.
    ///
    /// <para>That surface was <c>DeckbuilderController</c>'s switcher bar when these were written,
    /// then <c>CharacterRosterController</c>'s per-character deck strip, and is its flat deck grid
    /// since ADR-0009. Nothing here changed with either move, which is the point of testing the core
    /// behaviours rather than the screen.</para>
    ///
    /// <para><b>SET-DEFAULT WAS A THIRD OF THIS FILE AND IS GONE (ADR-0009).</b> Four tests covered
    /// it: promoting a deck to the front, the promotion surviving a reload, deleting a non-default
    /// leaving the pointer alone, and — the one worth naming — deleting the default leaving the
    /// pointer DANGLING rather than repairing it, which existed to stop a later "tidy-up" adding the
    /// silent repair. There is no pointer to dangle. A fifth test pinned why the screen refused to
    /// delete a character's last deck; that guard is deleted too, and what replaced it is the
    /// opposite assertion below.</para>
    ///
    /// <para><b>Why this exists next to <see cref="DeckQueryTests"/>, which already covers ordering.</b>
    /// Those tests pin <see cref="DeckQuery.ForAccount"/> against hand-built arrays. These pin the
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
            _root = Path.Combine(Path.GetTempPath(), "kingdom-deckmgmt-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private JsonDeckStore Decks() => JsonDeckStore.InDirectory(_root, "device-token");

        private const CardClass Primary = CardClass.EMBERWARD;
        private const CardClass Secondary = CardClass.TIDECALLER;

        private static DeckList Blank(string id, string name = "New Deck") =>
            new DeckList(Primary, Secondary) { Id = id, Name = name };

        private static DeckList Generated(string id, ulong seed) =>
            DefaultDeckGenerator.Generate(Primary, Secondary, LiveCardSet.Db, new DeterministicRandom(seed), id);

        /// <summary>The deck grid's own call, re-read from disk every time — as the screen does.</summary>
        private System.Collections.Generic.IReadOnlyList<DeckSummary> Grid() =>
            DeckQuery.ForAccount(Decks().LoadAll(), LiveCardSet.Db);

        // ---- create ("+") ---------------------------------------------------------------

        [Test]
        public void A_New_Deck_Is_Empty_Carries_Its_Pairing_And_Appears_In_The_Grid()
        {
            Decks().Save(Generated("starter", 7));

            // What the create flow writes: empty, new id, pairing decided at birth.
            Decks().Save(Blank("d2"));

            var grid = Grid();

            Assert.That(grid.Count, Is.EqualTo(2));
            Assert.That(grid.Select(d => d.Deck.Id), Is.EqualTo(new[] { "starter", "d2" }),
                "Stored order, and nothing sorts ahead of it — there is no default to promote.");

            var made = grid.Single(d => d.Deck.Id == "d2");
            Assert.That(made.TotalCount, Is.Zero, "A blank deck is empty, not generated.");
            Assert.That((made.Deck.PrimaryClass, made.Deck.SecondaryClass), Is.EqualTo((Primary, Secondary)),
                "The pairing survives the round trip. This was an L5 BINDING assertion until " +
                "ADR-0009 — deck.CharacterId pointing back at a character — and it is the same " +
                "property under a different owner: the deck knows which pool it draws from.");
        }

        [Test]
        public void An_Illegal_Draft_Saves_And_Reloads_Unchanged()
        {
            // The screen's central ruling (collection-deckbuilder-spec §1): save is never
            // legality-gated, so a half-built deck must survive a write/read intact.
            var draft = Blank("d1", "Half Built");
            draft.Entries.Add(new DeckEntry(
                LiveCardSet.Db.AllCollectible().First(c => draft.AllowsClass(c.Class)).Id, 2));
            Decks().Save(draft);

            var reloaded = Decks().LoadAll().Single(d => d.Id == "d1");

            Assert.That(reloaded.Name, Is.EqualTo("Half Built"));
            Assert.That(reloaded.TotalCount, Is.EqualTo(2));
            Assert.That(QueueEligibility.Check(reloaded, LiveCardSet.Db).IsEligible, Is.False,
                "Persisted happily; still refused a match.");
        }

        // ---- rename ---------------------------------------------------------------------

        [Test]
        public void A_Rename_Reaches_Disk_Through_Save_And_Keeps_Its_Cards()
        {
            Decks().Save(Generated("d1", 3));

            var open = Decks().LoadAll().Single(d => d.Id == "d1");
            int before = open.TotalCount;
            open.Name = "Tide and Ash";          // what the rename pane does, in memory
            Decks().Save(open);                  // and what Save then does

            var reloaded = Decks().LoadAll().Single(d => d.Id == "d1");
            Assert.That(reloaded.Name, Is.EqualTo("Tide and Ash"));
            Assert.That(reloaded.TotalCount, Is.EqualTo(before), "A rename is not a deck edit.");
        }

        [Test]
        public void A_Rename_Cannot_Move_The_Pairing()
        {
            // Immutability made structural: DeckList has no setter for either class, so the only way
            // to "rename into" a different pairing is to construct a new deck under the same id. The
            // LOCAL store cheerfully accepts that — this is a client cache, and the refusal that
            // matters is the server's (DeckPairing.Preserve). What this pins is that an ordinary
            // edit-and-save round trip cannot move it by accident.
            Decks().Save(Generated("d1", 3));

            var open = Decks().LoadAll().Single(d => d.Id == "d1");
            open.Name = "Renamed";
            Decks().Save(open);

            var reloaded = Decks().LoadAll().Single(d => d.Id == "d1");
            Assert.That((reloaded.PrimaryClass, reloaded.SecondaryClass), Is.EqualTo((Primary, Secondary)));
        }

        // ---- delete ---------------------------------------------------------------------

        [Test]
        public void Deleting_One_Deck_Leaves_The_Others_Alone()
        {
            Decks().Save(Generated("d1", 1));
            Decks().Save(Blank("d2", "Scratch"));

            Decks().Delete("d2");

            var grid = Grid();
            Assert.That(grid.Count, Is.EqualTo(1));
            Assert.That(grid[0].Deck.Id, Is.EqualTo("d1"));
        }

        [Test]
        public void Deleting_The_Last_Deck_Is_Allowed_And_Leaves_An_Empty_Account()
        {
            // THE INVERSE OF THE TEST THIS REPLACES. The old roster refused to delete a character's
            // last deck — "{name} needs at least one deck" — and a test here pinned WHY that guard
            // had to exist: with zero decks the switcher had nothing to open on and the deckbuilder
            // bailed out. The guard protected the CONTAINER, not the player (Sam, on being shown it:
            // "why that makes no sense"), and with decks at the top level the state it feared is
            // simply a new account. Nothing may reintroduce the floor.
            Decks().Save(Generated("d1", 1));
            Assert.That(Grid().Count, Is.EqualTo(1));

            Assert.DoesNotThrow(() => Decks().Delete("d1"));

            Assert.That(Grid(), Is.Empty, "Zero decks is a legitimate state, not a broken one.");
        }

        // ---- switching ------------------------------------------------------------------

        [Test]
        public void Switching_Away_Persists_The_Outgoing_Deck()
        {
            // _deck is the only copy of unsaved edits; leaving saves before repointing.
            Decks().Save(Generated("d1", 1));
            Decks().Save(Blank("d2", "Other"));

            var outgoing = Decks().LoadAll().Single(d => d.Id == "d1");
            outgoing.Entries.RemoveAt(0);            // an edit made just before opening another deck
            int edited = outgoing.TotalCount;
            Decks().Save(outgoing);                  // the save-on-leave

            Assert.That(Decks().LoadAll().Single(d => d.Id == "d1").TotalCount, Is.EqualTo(edited),
                "The edit outlived the switch.");
        }
    }
}
