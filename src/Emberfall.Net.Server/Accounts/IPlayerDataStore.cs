using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>One player's characters and decks, as read down by <c>GET /roster</c>.</summary>
    public sealed class PlayerRoster
    {
        public IReadOnlyList<Character> Characters { get; }
        public IReadOnlyList<DeckList> Decks { get; }

        public PlayerRoster(IReadOnlyList<Character>? characters = null,
                            IReadOnlyList<DeckList>? decks = null)
        {
            Characters = characters ?? Array.Empty<Character>();
            Decks = decks ?? Array.Empty<DeckList>();
        }

        public static readonly PlayerRoster Empty = new PlayerRoster();
    }

    /// <summary>
    /// The server-authoritative store for characters and decks — the thing that makes them survive a
    /// reinstall. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para><b>Authoritative, not a mirror.</b> There is no offline mode, so the usual argument for
    /// a local mirror does not exist. One record means DELETE MEANS DELETE — no tombstone, no
    /// last-write-wins, and no way for "absent on this device" and "deleted" to be confused.</para>
    ///
    /// <para><b>Every method takes playerId and every query filters on it.</b> Deck and character
    /// ids are globally unique, so a write that matched on id alone would let one account edit
    /// another's deck.</para>
    /// </summary>
    public interface IPlayerDataStore
    {
        Task<PlayerRoster> ReadAsync(string playerId, CancellationToken ct = default);

        /// <summary>Upsert. The pairing is immutable, so a re-save may change name and
        /// default deck and must not change either class.
        ///
        /// <para><b>Returns false when the row was NOT written</b> because the id already belongs to
        /// a different player. A write that did not happen must never report success — the caller
        /// maps false to 409. True on both a fresh insert and a re-save of this player's own row.</para>
        /// </summary>
        Task<bool> SaveCharacterAsync(string playerId, Character character, CancellationToken ct = default);

        /// <summary>Upsert. <b>Stores an illegal deck.</b> Validation is a QUEUE-time concern.
        ///
        /// <para><b>Returns false when the row was NOT written</b> because the id already belongs to
        /// a different player — the same "could not attribute the write" refusal as
        /// <see cref="SaveCharacterAsync"/>, and nothing to do with the deck being legal.</para>
        /// </summary>
        Task<bool> SaveDeckAsync(string playerId, DeckList deck, CancellationToken ct = default);

        /// <summary>True if a deck was removed; false if this player has no such deck.</summary>
        Task<bool> DeleteDeckAsync(string playerId, string deckId, CancellationToken ct = default);

        /// <summary>
        /// Remove a character <b>and every deck bound to it</b>. True if the character existed for
        /// this player; false if it did not.
        ///
        /// <para><b>The cascade is not a convenience.</b> Deleting a character deletes its decks is
        /// a ruling, not an implementation detail — a deck cannot be played without its character
        /// (L5), so keeping one preserves something unusable, and the client's confirm dialog names
        /// the count it is about to destroy. Doing it as one call rather than a character delete
        /// followed by N deck deletes is what stops a drop between them leaving decks bound to a
        /// character that no longer exists: those would read back down on the next sync and appear
        /// in a chooser with nothing to open them.</para>
        /// </summary>
        Task<bool> DeleteCharacterAsync(string playerId, string characterId, CancellationToken ct = default);
    }

    /// <summary>In-memory implementation — the default for tests and single-box dev. Held to the
    /// same contract suite as Postgres, and identical on the property that suite exists to pin: no
    /// player can read, overwrite or delete another player's rows.
    ///
    /// <para><b>Ids are globally unique here too, and that is deliberate.</b> Deck and character ids
    /// are client-minted GUIDs, so an honest collision across two players cannot occur — but the
    /// stores must not DISAGREE about what happens if one did. Postgres keys on a global primary key
    /// plus <c>where ... = excluded.player_id</c> on the upsert, which matches zero rows and reports
    /// the write as not done; this store keeps an id-to-owner index for the same answer. Both refuse
    /// and both leave the first player's row untouched — see
    /// <c>PlayerDataStoreContractTests.A_Players_Deck_Is_Unchanged_By_Another_Players_Save_Under_The_Same_Id</c>.
    /// Thread-safe under a single lock.</para>
    /// </summary>
    public sealed class InMemoryPlayerDataStore : IPlayerDataStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Dictionary<string, Character>> _characters =
            new Dictionary<string, Dictionary<string, Character>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, DeckList>> _decks =
            new Dictionary<string, Dictionary<string, DeckList>>(StringComparer.Ordinal);

        // id -> owning playerId. Postgres gets this from the tables' primary keys; here it is the
        // only way to answer "does another player already own this id" without a scan.
        private readonly Dictionary<string, string> _characterOwner =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _deckOwner =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public Task<PlayerRoster> ReadAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var chars = _characters.TryGetValue(playerId, out var c)
                    ? (IReadOnlyList<Character>)c.Values.ToList()
                    : Array.Empty<Character>();
                var decks = _decks.TryGetValue(playerId, out var d)
                    ? (IReadOnlyList<DeckList>)d.Values.ToList()
                    : Array.Empty<DeckList>();
                return Task.FromResult(new PlayerRoster(chars, decks));
            }
        }

        public Task<bool> SaveCharacterAsync(string playerId, Character character, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_characterOwner.TryGetValue(character.Id, out var owner) &&
                    !string.Equals(owner, playerId, StringComparison.Ordinal))
                    return Task.FromResult(false);

                if (!_characters.TryGetValue(playerId, out var bag))
                    _characters[playerId] = bag = new Dictionary<string, Character>(StringComparer.Ordinal);

                // The pairing is immutable: a re-save may move the name and the default deck and
                // must move NEITHER class. Postgres gets this from its upsert's `do update set`,
                // which touches only name/default_deck_id/updated_at; here it is by hand, because a
                // plain `bag[id] = character` replaces all four fields.
                if (bag.TryGetValue(character.Id, out var stored))
                    character = new Character(stored.Id, character.Name,
                                              stored.PrimaryClass, stored.SecondaryClass,
                                              character.DefaultDeckId);

                bag[character.Id] = character;
                _characterOwner[character.Id] = playerId;
                return Task.FromResult(true);
            }
        }

        public Task<bool> SaveDeckAsync(string playerId, DeckList deck, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_deckOwner.TryGetValue(deck.Id, out var owner) &&
                    !string.Equals(owner, playerId, StringComparison.Ordinal))
                    return Task.FromResult(false);

                if (!_decks.TryGetValue(playerId, out var bag))
                    _decks[playerId] = bag = new Dictionary<string, DeckList>(StringComparer.Ordinal);
                bag[deck.Id] = deck;
                _deckOwner[deck.Id] = playerId;
                return Task.FromResult(true);
            }
        }

        public Task<bool> DeleteDeckAsync(string playerId, string deckId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                bool removed = _decks.TryGetValue(playerId, out var bag) && bag.Remove(deckId);
                if (removed)
                    _deckOwner.Remove(deckId);
                return Task.FromResult(removed);
            }
        }

        public Task<bool> DeleteCharacterAsync(string playerId, string characterId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                bool removed = _characters.TryGetValue(playerId, out var bag) && bag.Remove(characterId);
                if (!removed)
                    return Task.FromResult(false);

                _characterOwner.Remove(characterId);

                // The cascade, inside the same lock, so no reader ever sees a deck whose character
                // has already gone. Postgres gets the same atomicity from one transaction.
                if (_decks.TryGetValue(playerId, out var deckBag))
                {
                    var bound = deckBag.Values
                        .Where(d => string.Equals(d.CharacterId, characterId, StringComparison.Ordinal))
                        .Select(d => d.Id)
                        .ToList();
                    foreach (string deckId in bound)
                    {
                        deckBag.Remove(deckId);
                        _deckOwner.Remove(deckId);
                    }
                }

                return Task.FromResult(true);
            }
        }
    }
}
