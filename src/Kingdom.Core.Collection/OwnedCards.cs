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
    /// The one ownership record for an identity scope — what this account owns, per card id.
    ///
    /// <para><b>Load returns null on a device that has no record, and that null is the whole design.</b>
    /// It is not an error and it is not "owns nothing": it means <i>ownership is unknown here</i>, which
    /// is the state every account is in until something grants it cards. Every consumer of L6 —
    /// <see cref="DeckValidator.Validate(DeckList, Kingdom.Core.Contracts.ICardDatabase, IOwnedCardCounts)"/>,
    /// <see cref="DeckValidator.CanAdd(DeckList, Kingdom.Core.Cards.CardDefinition, IOwnedCardCounts)"/>,
    /// the collection wall — takes a null ownership source to mean "do not constrain", which is exactly
    /// the full-pool alpha the game runs today. An <i>empty but present</i> record is a different thing
    /// and means what it says: this player owns nothing, and every deck they hold is a draft.</para>
    ///
    /// <para><b>The once-only key for the starter grant no longer lives here</b> (2026-09-01; spec
    /// <c>docs/dev-docs/specs/card-ownership.md</c>, slice 1). It used to be this record's absence:
    /// a client-side <c>IsDue</c> read the null and the Save that followed marked the account granted.
    /// With the server's entitlement ledger as the authority, the key is the ledger transaction id
    /// <c>signup:{playerId}</c> — a replay writes nothing, so the grant is attempted on every account
    /// resolve and lands exactly once. That <c>IsDue</c> was deleted with slice 3, which is what its
    /// own retirement trigger asked for: once the client mirrors the server's projection, a
    /// grant-shaped check sitting in this assembly with no caller is a trap, not a leftover.</para>
    ///
    /// <para><b>What DOES still live here is this record's own null,</b> and slice 3 sharpened rather
    /// than weakened it. The file is a CACHE of the ledger now, written only by
    /// <c>CollectionGateway</c> and only on an answer the server actually gave — so absent still means
    /// "never synced, do not constrain", and present-and-empty still means "the server says you own
    /// nothing". Every failure to reach the server leaves the file untouched, precisely so those two
    /// never blur.</para>
    ///
    /// <para>The distinction that key was drawing survives and is stronger for the move.
    /// <i>Granted then spent</i> is still never re-granted, now because the ledger keeps the original
    /// grant rows forever: a balance falling to zero cannot be mistaken for never having received one,
    /// and no convention about empty-versus-absent files has to be honoured by every future writer.
    /// <b>What has NOT changed is this record's own null</b> — it still means "ownership unknown, do
    /// not constrain", every consumer still depends on that, and it is the reason a client that has
    /// never synced shows the full pool rather than an empty collection.</para>
    ///
    /// <para><b>No Delete.</b> Same shape as <see cref="IProfileStore"/> and for the same reason: there
    /// is one record per scope, so there is nothing to enumerate, and clearing it is a Save of an empty
    /// one — which, per the paragraph above, is a real and very different statement from having none.</para>
    /// </summary>
    public interface IOwnedCardStore
    {
        /// <summary>This account's ownership record, or null when it has never had one.</summary>
        OwnedCardCounts? Load();

        /// <summary>Write the record. Writing an empty one asserts "owns nothing", not "unknown".</summary>
        void Save(OwnedCardCounts owned);
    }

    /// <summary>
    /// Every card, unlimited copies — L6 passes for any id without a ledger, a collection file, or a
    /// pack ever opened. For an account whose identity IS "owns everything" (a superuser: a bot
    /// opponent, an internal/QA identity) rather than one that merely hasn't been constrained yet.
    ///
    /// <para>Deliberately distinct from passing <c>owned: null</c>. Null means "unknown, do not
    /// constrain" — the day a real ownership source exists for that account, L6 starts applying. This
    /// type means the account is never meant to be constrained, no matter what else lands.</para>
    /// </summary>
    public sealed class AllCardsOwnedCounts : IOwnedCardCounts
    {
        public static readonly AllCardsOwnedCounts Instance = new AllCardsOwnedCounts();

        private AllCardsOwnedCounts() { }

        public int CopiesOwned(int cardId) => int.MaxValue;
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
