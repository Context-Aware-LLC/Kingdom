using System;
using System.Collections.Generic;

namespace Emberfall.Core.Collection
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
        int CopiesOwned(string cardId);
    }

    /// <summary>The trivial dictionary-backed <see cref="IOwnedCardCounts"/> for tests, dev and projections.</summary>
    public sealed class OwnedCardCounts : IOwnedCardCounts
    {
        private readonly Dictionary<string, int> _counts;

        public OwnedCardCounts() => _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public OwnedCardCounts(IReadOnlyDictionary<string, int> counts)
        {
            _counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (counts != null)
                foreach (var kv in counts) _counts[kv.Key] = kv.Value;
        }

        public int CopiesOwned(string cardId)
            => cardId != null && _counts.TryGetValue(cardId, out int n) ? n : 0;

        public void Set(string cardId, int copies) => _counts[cardId] = copies;

        /// <summary>How many distinct card ids carry a recorded count — including any recorded as 0.</summary>
        public int RecordedCards => _counts.Count;

        /// <summary>
        /// Every recorded (card id, copies) pair, <b>card id ascending</b>.
        ///
        /// <para>Sorted rather than in insertion order because this is what the store writes, and a
        /// collection file whose row order depends on the order packs happened to be opened produces a
        /// different diff on every save for no change in what the player owns.</para>
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, int>> Entries()
        {
            var rows = new List<KeyValuePair<string, int>>(_counts.Count);
            foreach (var kv in _counts) rows.Add(kv);
            rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return rows;
        }
    }

    /// <summary>
    /// The one ownership record for an identity scope — what this account owns, per card id.
    ///
    /// <para><b>Load returns null on a device that has no record, and that null is the whole design.</b>
    /// It is not an error and it is not "owns nothing": it means <i>ownership is unknown here</i>, which
    /// is the state every account is in until something grants it cards. Every consumer of L6 —
    /// <see cref="DeckValidator.Validate(DeckList, Character, Emberfall.Core.Contracts.ICardDatabase, IOwnedCardCounts)"/>,
    /// <see cref="DeckValidator.CanAdd(DeckList, Character, Emberfall.Core.Cards.CardDefinition, IOwnedCardCounts)"/>,
    /// the collection wall — takes a null ownership source to mean "do not constrain", which is exactly
    /// the full-pool alpha the game runs today. An <i>empty but present</i> record is a different thing
    /// and means what it says: this player owns nothing, and every deck they hold is a draft.</para>
    ///
    /// <para><b>The absence is also the once-only key for the starter grant</b> (Sam, 2026-08-12 —
    /// <see cref="OnboardingGrant"/>). What a new account starts out owning is no longer an open product
    /// decision, but the answer still does not live here: this interface stores a record, it does not
    /// decide one. <see cref="OnboardingGrant.IsDue"/> reads the null, and the Save that follows is what
    /// marks the account granted — so there is no first-run flag and no extra facet file. Note the
    /// consequence: an <i>empty but present</i> record is a granted account that spent everything, and is
    /// correctly never re-granted, while an absent one is granted on sight.</para>
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

        public int CopiesOwned(string cardId) => int.MaxValue;
    }
}
