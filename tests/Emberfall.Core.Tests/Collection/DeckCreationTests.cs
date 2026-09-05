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
    /// <see cref="DeckCreation"/> — the one path that mints a deck, and the two rules it enforces
    /// that nothing else can: a pairing must be real before anything is generated or written, and an
    /// account may keep <see cref="DeckCreation.MaxPerAccount"/> decks.
    ///
    /// <para><b>Most of this file is inherited from <c>DefaultDeckIdTests</c>, which ADR-0009
    /// deleted.</b> That file was about <c>Character.DefaultDeckId</c> — the deck a character opened
    /// on — and the field no longer exists, so the tests about it have nothing left to fail. Three of
    /// its tests were never about the default at all, and those are the ones below: they pin the §1
    /// rule that a SAVE is not legality-gated, which is the rule most likely to be "helpfully"
    /// reintroduced by someone who sees an illegal deck being written and reads it as a bug.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DeckCreationTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "emberfall-deckcreation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }

        private JsonDeckStore Decks() => JsonDeckStore.InDirectory(_root, "device-token");

        // ---- the pairing ----------------------------------------------------------------

        [Test]
        public void Creation_Rejects_An_Invalid_Pairing_Before_Writing_Anything()
        {
            var decks = Decks();

            Assert.Throws<ArgumentException>(() => DeckCreation.Create(
                decks, LiveCardSet.Db, new DeterministicRandom(7),
                "deck-1", "Doubled", CardClass.EMBERWARD, CardClass.EMBERWARD));

            Assert.That(decks.LoadAll(), Is.Empty,
                "A rejected pairing costs nothing and leaves no half-created deck behind.");
        }

        [Test]
        public void A_Created_Deck_Carries_The_Pairing_It_Was_Asked_For()
        {
            var decks = Decks();

            DeckList created = DeckCreation.Create(
                decks, LiveCardSet.Db, new DeterministicRandom(7),
                "deck-1", "Ashen Vow", CardClass.GRAVEKEEP, CardClass.VERDANT);

            Assert.That(created.PrimaryClass, Is.EqualTo(CardClass.GRAVEKEEP));
            Assert.That(created.SecondaryClass, Is.EqualTo(CardClass.VERDANT));

            // Through the store, because the pairing has to SURVIVE a write — it rides in the same
            // blob as the cards now rather than in a character row beside them.
            DeckList stored = decks.LoadAll().Single();
            Assert.That(stored.PrimaryClass, Is.EqualTo(CardClass.GRAVEKEEP));
            Assert.That(stored.SecondaryClass, Is.EqualTo(CardClass.VERDANT));
        }

        // ---- the cap (ADR-0009 ruling 2) -------------------------------------------------

        [Test]
        public void An_Account_May_Keep_MaxPerAccount_Decks_And_No_More()
        {
            var held = new List<DeckList>();
            for (int i = 0; i < DeckCreation.MaxPerAccount; i++)
            {
                Assert.That(DeckCreation.CanCreate(held), Is.True,
                    $"Room for deck {i + 1} of {DeckCreation.MaxPerAccount}.");
                held.Add(new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT) { Id = "deck-" + i });
            }

            Assert.That(DeckCreation.CanCreate(held), Is.False,
                $"The {DeckCreation.MaxPerAccount + 1}th is refused.");
        }

        [Test]
        public void A_Draft_Counts_Against_The_Cap()
        {
            // The cap is about how many decks a player KEEPS, not how many are legal. Any other
            // reading would make the limit move under the player as they edited.
            var held = new List<DeckList>();
            for (int i = 0; i < DeckCreation.MaxPerAccount; i++)
                held.Add(new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT) { Id = "deck-" + i });

            Assert.That(held.All(d => d.TotalCount == 0), Is.True, "Every one of these is an empty draft.");
            Assert.That(DeckCreation.CanCreate(held), Is.False);
        }

        [Test]
        public void Unknown_Ownership_Is_Not_A_Full_Account()
        {
            // Null means "this caller does not know", which every screen reads as "do not constrain".
            // Reading it as a full account would refuse the very first deck on a fresh install.
            Assert.That(DeckCreation.CanCreate(null!), Is.True);
        }

        // ---- the starter choice (§2.11) --------------------------------------------------

        [Test]
        public void The_First_Deck_Is_Never_Offered_The_Choice()
        {
            Assert.That(DeckCreation.OffersStarterChoice(Array.Empty<DeckList>()), Is.False,
                "A player with nothing has nothing to build from.");
            Assert.That(
                DeckCreation.OffersStarterChoice(
                    new[] { new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT) { Id = "deck-1" } }),
                Is.True,
                "From the second deck on it is asked.");
        }

        [Test]
        public void A_Blank_Starter_Consults_No_Card_Pool()
        {
            // A pairing too thin to generate 30 can still hold a deck built by hand, so Blank must
            // not reach the generator. Passing a null database is how that is proved rather than
            // asserted: anything that touched it would throw.
            var decks = Decks();

            DeckList created = DeckCreation.Create(
                decks, null!, new DeterministicRandom(7),
                "deck-1", "By hand", CardClass.GRAVEKEEP, CardClass.VERDANT, StarterDeck.Blank);

            Assert.That(created.Entries, Is.Empty);
            Assert.That(created.Name, Is.EqualTo("By hand"));
        }

        // ---- the rule that must NOT be reintroduced -------------------------------------

        [Test]
        public void An_Illegal_Draft_Deck_Saves_Without_Complaint()
        {
            // Save is NOT legality-gated (§1: what legality gates is queue-eligibility). A half-built
            // deck must survive the session; legality is computed on load and never stored.
            var decks = Decks();
            var draft = new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT)
            {
                Id = "deck-1",
                Name = "Half-built",
            };
            draft.Entries.Add(new DeckEntry(LiveCardSet.AnyCollectible(CardClass.GRAVEKEEP).Id, 1));

            Assert.DoesNotThrow(() => decks.Save(draft));

            var loaded = decks.LoadAll().Single();
            Assert.That(loaded.TotalCount, Is.EqualTo(1), "The draft is kept intact, not padded or rejected.");

            Assert.That(DeckValidator.Validate(loaded, LiveCardSet.Db).IsValid, Is.False,
                "It is a draft: illegal on load, and that is computed, never a stored flag.");
        }

        [Test]
        public void Nothing_In_The_Stored_File_Records_Legality()
        {
            // A stored flag would go stale the moment a balance patch moved the line — §1 requires a
            // deck to stop being a draft on its own when a patch re-validates it.
            var decks = Decks();
            decks.Save(new DeckList(CardClass.GRAVEKEEP, CardClass.VERDANT)
            {
                Id = "deck-1",
                Name = "Half-built",
            });

            string json = File.ReadAllText(decks.FilePath);
            foreach (var forbidden in new[] { "valid", "legal", "draft" })
                Assert.That(json.ToLowerInvariant(), Does.Not.Contain(forbidden), $"'{forbidden}' must not be persisted.");
        }
    }
}
