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
        /// default deck and must not change either class.</summary>
        Task SaveCharacterAsync(string playerId, Character character, CancellationToken ct = default);

        /// <summary>Upsert. <b>Stores an illegal deck.</b> Validation is a QUEUE-time concern.</summary>
        Task SaveDeckAsync(string playerId, DeckList deck, CancellationToken ct = default);

        /// <summary>True if a deck was removed; false if this player has no such deck.</summary>
        Task<bool> DeleteDeckAsync(string playerId, string deckId, CancellationToken ct = default);
    }

    /// <summary>In-memory implementation — the default for tests and single-box dev. Held to the
    /// same contract suite as Postgres, and identical on the property that suite exists to pin: no
    /// player can read, overwrite or delete another player's rows.
    ///
    /// <para><b>One case differs, and it is not a security gap.</b> Deck and character ids are
    /// client-minted GUIDs, so an honest collision across two different players' ids cannot occur —
    /// but if it ever did, the two stores resolve it differently. This store keys each save on
    /// <c>(playerId, id)</c>, so a second player's write with an id the first player already owns is
    /// stored as its own, separate entry. Postgres keys on a global primary key plus
    /// <c>where ... = excluded.player_id</c> on the upsert, so the same write silently no-ops. Either
    /// way, player one's row is untouched — see
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

        public Task SaveCharacterAsync(string playerId, Character character, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_characters.TryGetValue(playerId, out var bag))
                    _characters[playerId] = bag = new Dictionary<string, Character>(StringComparer.Ordinal);
                bag[character.Id] = character;
                return Task.CompletedTask;
            }
        }

        public Task SaveDeckAsync(string playerId, DeckList deck, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_decks.TryGetValue(playerId, out var bag))
                    _decks[playerId] = bag = new Dictionary<string, DeckList>(StringComparer.Ordinal);
                bag[deck.Id] = deck;
                return Task.CompletedTask;
            }
        }

        public Task<bool> DeleteDeckAsync(string playerId, string deckId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(
                    _decks.TryGetValue(playerId, out var bag) && bag.Remove(deckId));
            }
        }
    }
}
