using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
{
    /// <summary>How many copies of each card a player holds — the collection's ownership model.</summary>
    /// <remarks>
    /// <para>An interface rather than a dictionary parameter because the authority differs by caller: the
    /// server projects it off <c>IEntitlementLedger</c> balances, a test hands over a literal, and the
    /// client (which never decides an open — ADR-0004) only ever renders the result.</para>
    ///
    /// <para><b>This lived in <c>BoosterPackOpener.cs</c> until NOR-238</b>, where it was reachable only
    /// by somebody already reading the pack opener. It is now the input to <see cref="DeckValidator"/>
    /// L6 as well, so it has a file named after what it is. Nothing about the type changed — same
    /// namespace, same members — so no call site moved with it.</para>
    /// </remarks>
    public interface IOwnedCardCounts
    {
        /// <summary>Copies of this card the player owns. 0 for a card they have never seen.</summary>
        int CopiesOwned(int cardId);
    }

    /// <summary>The trivial dictionary-backed <see cref="IOwnedCardCounts"/> for tests, dev and projections.</summary>
    public sealed class OwnedCardCounts : IOwnedCardCounts
    {
        private readonly Dictionary<int, int> _counts;

        public OwnedCardCounts() => _counts = new Dictionary<int, int>();

        public OwnedCardCounts(IReadOnlyDictionary<int, int> counts)
        {
            _counts = new Dictionary<int, int>();
            if (counts != null)
                foreach (var kv in counts) _counts[kv.Key] = kv.Value;
        }

        public int CopiesOwned(int cardId)
            => _counts.TryGetValue(cardId, out int n) ? n : 0;

        public void Set(int cardId, int copies) => _counts[cardId] = copies;

        /// <summary>How many distinct card ids carry a recorded count — including any recorded as 0.</summary>
        public int RecordedCards => _counts.Count;

        /// <summary>
        /// Every recorded (card id, copies) pair, <b>card id ascending</b>.
        ///
        /// <para>Sorted rather than in insertion order because this is what the store writes, and a
        /// collection file whose row order depends on the order packs happened to be opened produces a
        /// different diff on every save for no change in what the player owns.</para>
        /// </summary>
        public IReadOnlyList<KeyValuePair<int, int>> Entries()
        {
            var rows = new List<KeyValuePair<int, int>>(_counts.Count);
            foreach (var kv in _counts) rows.Add(kv);
            // NUMERIC ascending now, not ordinal over the spelling. The row order in a saved
            // collection file therefore changes once on the first write after this migration.
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            return rows;
        }
    }

    /// <summary>
    /// The one ownership record for an identity scope — what this account owns, as an
    /// <see cref="OwnershipRecord"/> carrying its rule and digest.
    ///
    /// <para><b>Load returns null when this device has no verified record, and that null is the whole
    /// design.</b> It means <i>ask the server</i>: the file was never written, predates the digest, or
    /// no longer matches its own digest. It is not "owns nothing" and it is not "do not constrain". An
    /// <i>empty list</i> record is a different thing and means what it says: the server told this device
    /// the account owns nothing.</para>
    ///
    /// <para>The file is a CACHE of the server's ledger, written only on an answer the server actually
    /// gave. Every failure to reach the server leaves it untouched, so absent and present-and-empty
    /// never blur. The starter grant's once-only key lives in the ledger (<c>signup:{playerId}</c>),
    /// not here.</para>
    ///
    /// <para><b>No Delete.</b> Same shape as <see cref="IProfileStore"/> and for the same reason: there
    /// is one record per scope, so there is nothing to enumerate, and clearing it is a Save of an empty
    /// list — which, per the paragraph above, is a real and very different statement from having none.</para>
    /// </summary>
    public interface IOwnedCardStore
    {
        /// <summary>This device's verified ownership record, or null when it has none.</summary>
        OwnershipRecord? Load();

        /// <summary>Write the record. Writing an empty list asserts "owns nothing", not "unknown".</summary>
        void Save(OwnershipRecord record);
    }

    /// <summary>
    /// Every pool-eligible card at its rarity limit — what a superuser owns, expressed as ownership
    /// rather than as "unconstrained". Resolved against a database, so a card added later is covered
    /// with no change to the record that names this rule. A token or non-published row is owned 0 times.
    /// </summary>
    public sealed class AtLimitOwnedCounts : IOwnedCardCounts
    {
        private readonly ICardDatabase _db;

        public AtLimitOwnedCounts(ICardDatabase db)
            => _db = db ?? throw new ArgumentNullException(nameof(db));

        public int CopiesOwned(int cardId)
            => _db.TryGet(cardId, out var def) && def.PoolEligible ? DeckValidator.MaxCopies(def.Rarity) : 0;
    }
}
