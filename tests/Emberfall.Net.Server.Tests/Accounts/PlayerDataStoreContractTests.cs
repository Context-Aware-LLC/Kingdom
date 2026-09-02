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
        public async Task Re_Saving_A_Character_Changes_Its_Name_And_Never_Its_Classes()
        {
            await Store.SaveCharacterAsync("p1", NewCharacter());

            // A client that re-sends the whole character with different classes must not be able to
            // re-pair it. Postgres gets this from its upsert's `do update set`, which touches only
            // name/default_deck_id/updated_at; the in-memory store has to preserve them by hand.
            var repaired = new Character("c1", "Renamed", CardClass.VERDANT, CardClass.NIGHTVEIL);
            bool saved = await Store.SaveCharacterAsync("p1", repaired);

            Assert.That(saved, Is.True, "the player's own row — an upsert, not a refusal");
            Character stored = (await Store.ReadAsync("p1")).Characters[0];
            Assert.That(stored.Name, Is.EqualTo("Renamed"), "name is mutable");
            Assert.That(stored.PrimaryClass, Is.EqualTo(CardClass.EMBERWARD),
                "the pairing is a permanent identity decision — a re-save is not a repair");
            Assert.That(stored.SecondaryClass, Is.EqualTo(CardClass.TIDECALLER));
        }

        [Test]
        public async Task A_Character_Save_Under_Another_Players_Id_Reports_False()
        {
            await Store.SaveCharacterAsync("p1", NewCharacter());

            bool saved = await Store.SaveCharacterAsync("p2", new Character(
                "c1", "Not p1's character", CardClass.VERDANT, CardClass.NIGHTVEIL));

            Assert.That(saved, Is.False,
                "the row was not written, and a write that did not happen must not report success");
            Assert.That((await Store.ReadAsync("p2")).Characters, Is.Empty);
            Assert.That((await Store.ReadAsync("p1")).Characters[0].Name, Is.EqualTo("Ashe"));
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

        [Test]
        public async Task Deleting_A_Character_Deletes_The_Decks_Bound_To_It()
        {
            await Store.SaveCharacterAsync("p1", NewCharacter());
            await Store.SaveDeckAsync("p1", NewDeck("d1"));
            await Store.SaveDeckAsync("p1", NewDeck("d2"));

            bool deleted = await Store.DeleteCharacterAsync("p1", "c1");

            Assert.That(deleted, Is.True);
            PlayerRoster roster = await Store.ReadAsync("p1");
            Assert.That(roster.Characters, Is.Empty);
            Assert.That(roster.Decks, Is.Empty,
                "a deck cannot be played without its character, so keeping one preserves something " +
                "unusable — and it would read straight back down on the next GET /roster");
        }

        [Test]
        public async Task Deleting_A_Character_Leaves_Another_Characters_Decks_Alone()
        {
            await Store.SaveCharacterAsync("p1", NewCharacter("c1"));
            await Store.SaveCharacterAsync("p1", new Character(
                "c2", "Bryn", CardClass.VERDANT, CardClass.NIGHTVEIL));
            await Store.SaveDeckAsync("p1", NewDeck("d1", "c1"));
            await Store.SaveDeckAsync("p1", NewDeck("d2", "c2"));

            await Store.DeleteCharacterAsync("p1", "c1");

            PlayerRoster roster = await Store.ReadAsync("p1");
            Assert.That(roster.Characters.Count, Is.EqualTo(1));
            Assert.That(roster.Characters[0].Id, Is.EqualTo("c2"));
            Assert.That(roster.Decks.Count, Is.EqualTo(1), "the cascade is scoped to ONE character");
            Assert.That(roster.Decks[0].Id, Is.EqualTo("d2"));
        }

        [Test]
        public async Task Deleting_An_Unknown_Character_Is_False()
            => Assert.That(await Store.DeleteCharacterAsync("p1", "no-such-character"), Is.False);

        // The delete that must not reach across the identity boundary. character_id on `deck` is a
        // plain text column with no foreign key, so nothing but the player_id predicate stops the
        // cascade from sweeping up a row another player wrote under the same character id.
        [Test]
        public async Task One_Player_Cannot_Delete_Anothers_Character_Or_Its_Decks()
        {
            await Store.SaveCharacterAsync("p1", NewCharacter("c1"));
            await Store.SaveDeckAsync("p1", NewDeck("d1", "c1"));

            bool deleted = await Store.DeleteCharacterAsync("p2", "c1");

            Assert.That(deleted, Is.False,
                "character_id is globally unique, so the delete must ALSO match on player_id");
            PlayerRoster victim = await Store.ReadAsync("p1");
            Assert.That(victim.Characters.Count, Is.EqualTo(1));
            Assert.That(victim.Decks.Count, Is.EqualTo(1), "and the cascade must not have run either");
        }

        // p2 owns a character with the SAME id as p1's deck binding. The cascade must delete only
        // p2's own rows — the pair (character_id, player_id), never character_id alone.
        [Test]
        public async Task The_Cascade_Does_Not_Cross_Players_On_A_Shared_Character_Id()
        {
            await Store.SaveCharacterAsync("p1", NewCharacter("c1"));
            await Store.SaveDeckAsync("p1", NewDeck("d1", "c1"));

            // p2 cannot own "c1" (the id is taken), so p2 gets their own character and a deck that
            // points at p1's character id — the shape a forged or buggy client would produce.
            await Store.SaveCharacterAsync("p2", new Character(
                "c2", "Bryn", CardClass.VERDANT, CardClass.NIGHTVEIL));
            await Store.SaveDeckAsync("p2", NewDeck("d2", "c1"));

            await Store.DeleteCharacterAsync("p2", "c2");

            PlayerRoster victim = await Store.ReadAsync("p1");
            Assert.That(victim.Characters.Count, Is.EqualTo(1), "p1's character is not p2's to delete");
            Assert.That(victim.Decks.Count, Is.EqualTo(1), "nor p1's deck, whatever p2's rows point at");
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
