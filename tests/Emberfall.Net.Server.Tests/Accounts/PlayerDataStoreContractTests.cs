using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The player-data-store contract, run against BOTH implementations. The store's job is to make a
    /// player's decks survive a reinstall — there is no offline mode and no client mirror, so this is
    /// the only place they exist.
    ///
    /// <para><b>Half of this file was about characters and went with them (ADR-0009).</b> Four of
    /// those tests were about the DELETE CASCADE — a character delete taking its decks with it, not
    /// taking another character's, not reaching across players on a shared id — and none of it has a
    /// successor: deleting a deck destroys exactly the thing named. What DID survive by moving is the
    /// pairing-immutability test, because the rule moved with the pairing; it is now
    /// <see cref="Re_Saving_A_Deck_Changes_Everything_But_Its_Classes"/>, and it is the reason
    /// <c>DeckPairing.Preserve</c> exists.</para>
    /// </summary>
    [Category("Contract")]
    public abstract class PlayerDataStoreContractTests
    {
        protected abstract IPlayerDataStore Store { get; }

        private static DeckList NewDeck(string id = "d1", string name = "Aggro")
        {
            var deck = new DeckList(CardClass.EMBERWARD, CardClass.TIDECALLER) { Id = id, Name = name };
            deck.Entries.Add(new DeckEntry(1001, 2));
            return deck;
        }

        [Test]
        public async Task An_Unknown_Player_Reads_Empty()
        {
            PlayerRoster roster = await Store.ReadAsync("no-such-player");

            Assert.That(roster.Decks, Is.Empty,
                "empty is a real answer here; the CLIENT distinguishes new from failed via onboarded");
        }

        [Test]
        public async Task Re_Saving_A_Deck_Changes_Everything_But_Its_Classes()
        {
            await Store.SaveDeckAsync("p1", NewDeck());

            // A client that re-sends the whole deck with different classes must not be able to
            // re-pair it (ADR-0009 ruling 1). THE OLD CHARACTER TABLE GOT THIS FREE: its upsert's
            // `do update set` simply did not list the class columns. The pairing rides inside
            // `deck.body` now — one blob, replaced wholesale — so BOTH stores have to preserve it
            // deliberately, and they do it through the same DeckPairing.Preserve so they cannot
            // answer differently. This test is the one that catches it if they ever do.
            var repaired = new DeckList(CardClass.VERDANT, CardClass.NIGHTVEIL)
            {
                Id = "d1",
                Name = "Renamed",
                CardBackId = 42,
                ShowcaseCardId = 1001,
            };
            repaired.Entries.Add(new DeckEntry(1001, 1));

            bool saved = await Store.SaveDeckAsync("p1", repaired);

            Assert.That(saved, Is.True, "the player's own row — an upsert, not a refusal");
            DeckList stored = (await Store.ReadAsync("p1")).Decks[0];
            Assert.That(stored.Name, Is.EqualTo("Renamed"), "name is mutable");
            Assert.That(stored.CardBackId, Is.EqualTo(42), "so is the card back");
            Assert.That(stored.ShowcaseCardId, Is.EqualTo(1001), "and the showcase card");
            Assert.That(stored.CountOf(1001), Is.EqualTo(1), "and the cards themselves");
            Assert.That(stored.PrimaryClass, Is.EqualTo(CardClass.EMBERWARD),
                "the pairing is permanent — a re-save is not a repair");
            Assert.That(stored.SecondaryClass, Is.EqualTo(CardClass.TIDECALLER));
        }

        [Test]
        public async Task A_Saved_Deck_Round_Trips_Its_Entries()
        {
            await Store.SaveDeckAsync("p1", NewDeck());

            PlayerRoster roster = await Store.ReadAsync("p1");

            Assert.That(roster.Decks.Count, Is.EqualTo(1));
            Assert.That(roster.Decks[0].Name, Is.EqualTo("Aggro"));
            Assert.That(roster.Decks[0].CountOf(1001), Is.EqualTo(2));
        }

        [Test]
        public async Task Saving_The_Same_Deck_Twice_Updates_It()
        {
            await Store.SaveDeckAsync("p1", NewDeck());

            DeckList edited = NewDeck();
            edited.Name = "Renamed";
            await Store.SaveDeckAsync("p1", edited);

            PlayerRoster roster = await Store.ReadAsync("p1");
            Assert.That(roster.Decks.Count, Is.EqualTo(1), "upsert, not insert — a save is an edit");
            Assert.That(roster.Decks[0].Name, Is.EqualTo("Renamed"));
        }

        [Test]
        public async Task An_Illegal_Deck_Is_Stored()
        {
            var draft = NewDeck("d-draft", "Work in progress");
            draft.Entries.Clear();
            draft.Entries.Add(new DeckEntry(1001, 1));   // 1 card, nowhere near a legal 30

            await Store.SaveDeckAsync("p1", draft);

            PlayerRoster roster = await Store.ReadAsync("p1");
            Assert.That(roster.Decks.Count, Is.EqualTo(1),
                "drafts are a legitimate state — validation happens on QUEUE, never on write");
        }

        [Test]
        public async Task Deleting_A_Deck_Removes_It()
        {
            await Store.SaveDeckAsync("p1", NewDeck());

            bool deleted = await Store.DeleteDeckAsync("p1", "d1");

            Assert.That(deleted, Is.True);
            Assert.That((await Store.ReadAsync("p1")).Decks, Is.Empty);
        }

        [Test]
        public async Task Deleting_An_Unknown_Deck_Is_False()
            => Assert.That(await Store.DeleteDeckAsync("p1", "no-such-deck"), Is.False);

        [Test]
        public async Task One_Player_Cannot_Read_Or_Delete_Anothers_Deck()
        {
            await Store.SaveDeckAsync("p1", NewDeck());

            Assert.That((await Store.ReadAsync("p2")).Decks, Is.Empty);
            Assert.That(await Store.DeleteDeckAsync("p2", "d1"), Is.False,
                "deck_id is globally unique, so every write path must ALSO match on player_id");
            Assert.That((await Store.ReadAsync("p1")).Decks.Count, Is.EqualTo(1));
        }

        // Deck ids are client-minted GUIDs, so an honest collision across two different players' ids
        // cannot occur — but IF one ever did, both implementations must answer the same way, because
        // the endpoint maps the answer to a status code. Postgres's
        // `where deck.player_id = excluded.player_id` guard matches zero rows; InMemoryPlayerDataStore
        // consults its id-to-owner index. Both leave p1's row alone and both report the write as not
        // done — see DeckWriteStatus.DuplicateId for why reporting 200 here would be the real damage.
        [Test]
        public async Task A_Players_Deck_Is_Unchanged_By_Another_Players_Save_Under_The_Same_Id()
        {
            await Store.SaveDeckAsync("p1", NewDeck());

            var collision = NewDeck();
            collision.Name = "Not p1's deck";
            collision.Entries.Clear();
            collision.Entries.Add(new DeckEntry(2002, 3));
            bool saved = await Store.SaveDeckAsync("p2", collision);

            Assert.That(saved, Is.False,
                "the row was not written, and a write that did not happen must not report success");
            Assert.That((await Store.ReadAsync("p2")).Decks, Is.Empty);

            DeckList p1Deck = (await Store.ReadAsync("p1")).Decks[0];
            Assert.That(p1Deck.Name, Is.EqualTo("Aggro"));
            Assert.That(p1Deck.CountOf(1001), Is.EqualTo(2));
        }

        // FIVE CASCADE TESTS STOOD HERE AND ALL FIVE LOST THEIR SUBJECT (ADR-0009):
        // deleting a character took its decks with it, did not take another character's, refused
        // across players, refused an unknown id, and did not cross players on a shared character id.
        // The cascade was a RULING — a deck could not be played without its character under L5, so
        // keeping one preserved something unusable — and nothing inherits it. Deleting a deck now
        // destroys exactly what the player asked to destroy. The deck-level delete tests above still
        // pin the identity boundary those tests also guarded.
    }

    /// <summary>The in-memory implementation against the shared contract. Always runs.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class InMemoryPlayerDataStoreContractTests : PlayerDataStoreContractTests
    {
        private InMemoryPlayerDataStore _store = null!;
        protected override IPlayerDataStore Store => _store;

        [SetUp]
        public void SetUp() => _store = new InMemoryPlayerDataStore();
    }

    /// <summary>The identical suite against Postgres. Gated on <c>EMBERFALL_LEDGER_DB</c> (see
    /// <see cref="PostgresProbe"/>); truncates before each test for a clean store.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class PostgresPlayerDataStoreContractTests : PlayerDataStoreContractTests
    {
        private string _conn = null!;
        private PostgresPlayerDataStore _store = null!;
        protected override IPlayerDataStore Store => _store;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _conn = PostgresProbe.RequireOrIgnore();
            _store = new PostgresPlayerDataStore(_conn);
            await _store.EnsureSchemaAsync();
        }

        [SetUp]
        public async Task SetUp() => await PostgresProbe.TruncateAsync(_conn);
    }
}
