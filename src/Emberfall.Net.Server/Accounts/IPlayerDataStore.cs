using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>One player's decks, as read down by <c>GET /roster</c>.</summary>
    public sealed class PlayerRoster
    {
        public IReadOnlyList<DeckList> Decks { get; }

        public PlayerRoster(IReadOnlyList<DeckList>? decks = null)
        {
            Decks = decks ?? Array.Empty<DeckList>();
        }

        public static readonly PlayerRoster Empty = new PlayerRoster();
    }

    /// <summary>
    /// The server-authoritative store for decks — the thing that makes them survive a
    /// reinstall. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para><b>Authoritative, not a mirror.</b> There is no offline mode, so the usual argument for
    /// a local mirror does not exist. One record means DELETE MEANS DELETE — no tombstone, no
    /// last-write-wins, and no way for "absent on this device" and "deleted" to be confused.</para>
    ///
    /// <para><b>Every method takes playerId and every query filters on it.</b> Deck
    /// ids are globally unique, so a write that matched on id alone would let one account edit
    /// another's deck.</para>
    /// </summary>
    public interface IPlayerDataStore
    {
        Task<PlayerRoster> ReadAsync(string playerId, CancellationToken ct = default);

        /// <summary>Upsert. <b>Stores an illegal deck.</b> Validation is a QUEUE-time concern.
        ///
        /// <para><b>The pairing is immutable</b>, so a re-save may move the name, the card back and
        /// the cards, and must move NEITHER class. That rule came from <c>SaveCharacterAsync</c>
        /// with the pairing itself (ADR-0009).</para>
        ///
        /// <para><b>Returns false when the row was NOT written</b> because the id already belongs to
        /// a different player. A write that did not happen must never report success — the caller
        /// maps false to 409. True on both a fresh insert and a re-save of this player's own row.
        /// Nothing to do with the deck being legal.</para>
        /// </summary>
        Task<bool> SaveDeckAsync(string playerId, DeckList deck, CancellationToken ct = default);

        /// <summary>True if a deck was removed; false if this player has no such deck.
        ///
        /// <para><b><c>DeleteCharacterAsync</c> stood beside this until 2026-09-04</b> and cascaded
        /// to every deck bound to the character (ADR-0009). The cascade was a ruling — a deck could
        /// not be played without its character under L5, so keeping one preserved something
        /// unusable. Nothing inherits it: a deck is now the thing itself, and deleting one destroys
        /// exactly what the player asked to destroy.</para>
        /// </summary>
        Task<bool> DeleteDeckAsync(string playerId, string deckId, CancellationToken ct = default);
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
        private readonly Dictionary<string, Dictionary<string, DeckList>> _decks =
            new Dictionary<string, Dictionary<string, DeckList>>(StringComparer.Ordinal);

        // id -> owning playerId. Postgres gets this from the tables' primary keys; here it is the
        // only way to answer "does another player already own this id" without a scan.
        private readonly Dictionary<string, string> _deckOwner =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public Task<PlayerRoster> ReadAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var decks = _decks.TryGetValue(playerId, out var d)
                    ? (IReadOnlyList<DeckList>)d.Values.ToList()
                    : Array.Empty<DeckList>();
                return Task.FromResult(new PlayerRoster(decks));
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

                // The pairing is immutable: a re-save may move everything else and must move NEITHER
                // class (ADR-0009 ruling 1). Through DeckPairing rather than by hand here, so this
                // store and Postgres cannot answer differently — see that file for why the character
                // table's "leave the columns out of the do-update-set" trick did not transfer.
                bag.TryGetValue(deck.Id, out var stored);
                deck = DeckPairing.Preserve(stored, deck);

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

    }
}
