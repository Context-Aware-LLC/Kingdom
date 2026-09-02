using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The player-data-store contract, run against BOTH implementations. The store's job is to make a
    /// player's characters and decks survive a reinstall — there is no offline mode and no client
    /// mirror, so this is the only place they exist.
    /// </summary>
    [Category("Contract")]
    public abstract class PlayerDataStoreContractTests
    {
        protected abstract IPlayerDataStore Store { get; }

        private static Character NewCharacter(string id = "c1")
            => new Character(id, "Ashe", CardClass.EMBERWARD, CardClass.TIDECALLER);

        private static DeckList NewDeck(string id = "d1", string characterId = "c1")
        {
            var deck = new DeckList { Id = id, CharacterId = characterId, Name = "Aggro" };
            deck.Entries.Add(new DeckEntry(1001, 2));
            return deck;
        }

        [Test]
        public async Task An_Unknown_Player_Reads_Empty()
        {
            PlayerRoster roster = await Store.ReadAsync("no-such-player");

            Assert.That(roster.Characters, Is.Empty);
            Assert.That(roster.Decks, Is.Empty,
                "empty is a real answer here; the CLIENT distinguishes new from failed via onboarded");
        }

        [Test]
        public async Task A_Saved_Character_Round_Trips()
        {
            await Store.SaveCharacterAsync("p1", NewCharacter());

            PlayerRoster roster = await Store.ReadAsync("p1");

            Assert.That(roster.Characters.Count, Is.EqualTo(1));
            Assert.That(roster.Characters[0].Id, Is.EqualTo("c1"));
            Assert.That(roster.Characters[0].PrimaryClass, Is.EqualTo(CardClass.EMBERWARD));
            Assert.That(roster.Characters[0].SecondaryClass, Is.EqualTo(CardClass.TIDECALLER));
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
            var draft = new DeckList { Id = "d-draft", CharacterId = "c1", Name = "Work in progress" };
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

        // Deck ids are client-minted GUIDs, so an honest collision across two different players'
        // ids cannot occur — but IF one ever did, the two implementations resolve it differently:
        // Postgres's `where deck.player_id = excluded.player_id` guard makes p2's write a silent
        // no-op, while InMemoryPlayerDataStore's (playerId, id) keying stores it as p2's own,
        // separate entry (see that class's doc comment). That difference is allowed. What must NOT
        // differ is p1's row, so this test asserts ONLY that — nothing about what became of p2's
        // save, because that is exactly where the two stores legitimately diverge.
        [Test]
        public async Task A_Players_Deck_Is_Unchanged_By_Another_Players_Save_Under_The_Same_Id()
        {
            await Store.SaveDeckAsync("p1", NewDeck());

            var collision = NewDeck();
            collision.Name = "Not p1's deck";
            collision.Entries.Clear();
            collision.Entries.Add(new DeckEntry(2002, 3));
            await Store.SaveDeckAsync("p2", collision);

            DeckList p1Deck = (await Store.ReadAsync("p1")).Decks[0];
            Assert.That(p1Deck.Name, Is.EqualTo("Aggro"));
            Assert.That(p1Deck.CountOf(1001), Is.EqualTo(2));
        }
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
