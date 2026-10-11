using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="DeckQuery"/> — the deck grid's one call
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.7).
    ///
    /// <para>What is under test is a contract about <i>order and completeness</i>: stored order, and
    /// one <see cref="QueueCheck"/> per deck computed by the same gate match-join uses.</para>
    ///
    /// <para><b>MOST OF THIS FILE WAS DELETED WITH ITS SUBJECT (ADR-0009), and what went is worth
    /// naming so nobody re-adds it looking for coverage.</b> <c>ForCharacter</c> answered "this
    /// character's decks, default first", and three groups of tests covered it: the default's
    /// ordering, the two broken references the store deliberately did not repair (a deleted default
    /// deck, a default pointing at another character's deck), and ORPHANS — decks whose character
    /// was gone, which needed their own query, their own row in the UI, and a test proving the two
    /// together accounted for every saved deck.</para>
    ///
    /// <para>None of those states is reachable. There is no default (#343 closed by deletion), no
    /// character to point at, and an orphan was crash residue from a two-write creation that is one
    /// write now. The query that replaced them takes every deck on the account and returns every
    /// deck on the account, so "accounts for every saved deck" is true by construction rather than
    /// by test.</para>
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

        private JsonDeckStore Decks() => JsonDeckStore.InDirectory(_root, "device-token");

        private const CardClass Primary = CardClass.GRAVEKEEP;
        private const CardClass Secondary = CardClass.VERDANT;

        private static DeckList Empty(string id, string name = "Deck") =>
            new DeckList(Primary, Secondary) { Id = id, Name = name };

        private static DeckList Legal(string id, ulong seed = 7) =>
            DefaultDeckGenerator.Generate(Primary, Secondary, LiveCardSet.Db, new DeterministicRandom(seed), id);

        private static List<string> Ids(IReadOnlyList<DeckSummary> rows) => rows.Select(d => d.Deck.Id).ToList();

        // ---- completeness and order ------------------------------------------------------

        [Test]
        public void Every_Deck_On_The_Account_Is_Returned()
        {
            var decks = new[] { Empty("d1"), Empty("d2"), Empty("d3") };

            Assert.That(Ids(DeckQuery.ForAccount(decks, LiveCardSet.Db)),
                Is.EqualTo(new[] { "d1", "d2", "d3" }),
                "No membership test any more: the account's decks ARE the decks.");
        }

        [Test]
        public void Stored_Order_Is_What_The_Real_Store_Returns()
        {
            // The ordering claim is about IDeckStore.LoadAll, so prove it through the store rather
            // than an array literal that only agrees with itself. Stored order is the ruling (no
            // timestamp on DeckList — see DeckQuery.ForAccount): the grid must not reshuffle itself
            // between visits.
            var decks = Decks();
            foreach (var id in new[] { "d1", "d2", "d3" }) decks.Save(Empty(id, id));

            var result = DeckQuery.ForAccount(decks.LoadAll(), LiveCardSet.Db);

            Assert.That(Ids(result), Is.EqualTo(new[] { "d1", "d2", "d3" }));
        }

        [Test]
        public void An_Account_With_No_Decks_Returns_An_Empty_List()
        {
            Assert.That(DeckQuery.ForAccount(Array.Empty<DeckList>(), LiveCardSet.Db), Is.Empty);
        }

        [Test]
        public void Null_Input_Returns_Empty_Rather_Than_Throwing()
        {
            Assert.That(DeckQuery.ForAccount(null!, LiveCardSet.Db), Is.Empty);
        }

        [Test]
        public void A_Null_Deck_In_The_List_Is_Skipped_Rather_Than_Throwing()
        {
            // A store never hands one back, but this query is also called with hand-built lists and
            // a throw here would take down the whole grid for one bad row.
            var decks = new[] { Empty("d1"), null!, Empty("d2") };

            Assert.That(Ids(DeckQuery.ForAccount(decks, LiveCardSet.Db)), Is.EqualTo(new[] { "d1", "d2" }));
        }

        // ---- legality, resolved once, per deck ------------------------------------------

        [Test]
        public void Each_Row_Carries_Its_Own_Queue_Standing()
        {
            var decks = new List<DeckList> { Legal("d-legal"), Empty("d-draft", "Half-built") };

            var rows = DeckQuery.ForAccount(decks, LiveCardSet.Db);

            Assert.That(rows[0].IsDraft, Is.False, "The generated starter is queue-eligible.");
            Assert.That(rows[1].IsDraft, Is.True);
            Assert.That(rows[1].Queue.Reason, Is.EqualTo("Add 30 more cards."));
            Assert.That(rows[1].TotalCount, Is.Zero, "The row's n of 'n / 30'.");
        }

        [Test]
        public void The_Rows_Verdict_Is_The_Same_Gate_Match_Join_Uses()
        {
            // The draft badge disagreeing with the queue is the exact bug this query exists to prevent.
            var decks = new List<DeckList> { Legal("d-legal"), Empty("d-draft") };

            foreach (var row in DeckQuery.ForAccount(decks, LiveCardSet.Db))
                Assert.That(row.IsDraft, Is.EqualTo(QueueEligibility.IsDraft(row.Deck, LiveCardSet.Db)),
                    $"Deck '{row.Deck.Id}'.");
        }

        [Test]
        public void Ownership_Reaches_The_Gate_Untouched()
        {
            // NOR-238: the badge and the gate have to be the same sentence, so an ownership record
            // that makes a deck unqueueable has to make its row a draft too. Passing an EMPTY record
            // — "owns nothing", which is a real answer — is the cheapest way to prove the argument
            // is not being dropped on the floor.
            var decks = new[] { Legal("d-legal") };

            Assert.That(DeckQuery.ForAccount(decks, LiveCardSet.Db)[0].IsDraft, Is.False,
                "Unknown ownership does not constrain.");
            Assert.That(DeckQuery.ForAccount(decks, LiveCardSet.Db, new OwnedCardCounts())[0].IsDraft, Is.True,
                "Owning nothing makes every deck a draft — and the row has to say so.");
        }
    }
}
