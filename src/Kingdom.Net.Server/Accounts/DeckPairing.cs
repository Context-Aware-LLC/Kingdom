using Kingdom.Core.Collection;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// The one implementation of "a saved deck's class pairing is immutable" (ADR-0009 ruling 1),
    /// shared by every <see cref="IPlayerDataStore"/> so that two stores cannot answer differently.
    ///
    /// <para><b>Why this needs a home at all.</b> The old <c>character</c> table got immutability for
    /// free: its upsert's <c>do update set</c> simply did not list the class columns, so a re-save
    /// physically could not move them. That trick does not transfer. The pairing now rides inside
    /// <c>deck.body</c>, a single JSON blob that every save replaces wholesale — so Postgres would
    /// have happily rewritten it while <see cref="InMemoryPlayerDataStore"/>, which merges by hand,
    /// refused. Two stores held to one contract suite disagreeing about a RULING is the failure this
    /// file exists to prevent.</para>
    ///
    /// <para><b>It keeps rather than refuses.</b> A client sending a changed pairing is not told
    /// "no"; the write lands with the stored pairing and everything else the client sent. That is the
    /// same shape the character upsert had, and it is the right one here for a specific reason: the
    /// only realistic sender of a changed pairing is a client old enough to predate the rule, and
    /// refusing its save would silently stop backing up that player's decks. A refusal belongs at the
    /// route, where it can say why — see <see cref="DeckWriteEndpoint"/>, which has no such refusal
    /// because there is nothing a player could do about it.</para>
    /// </summary>
    public static class DeckPairing
    {
        /// <summary>
        /// The deck to actually store, given what is already stored under this id.
        ///
        /// <para>Returns <paramref name="incoming"/> unchanged when there is no stored row (a fresh
        /// insert decides its own pairing, which is the only moment anything ever does) or when the
        /// two pairings already agree — the overwhelmingly common case, and it allocates nothing.
        /// Otherwise returns a copy carrying the STORED pairing and the INCOMING everything-else.</para>
        /// </summary>
        public static DeckList Preserve(DeckList? stored, DeckList incoming)
        {
            if (stored is null) return incoming;
            if (stored.PrimaryClass == incoming.PrimaryClass
                && stored.SecondaryClass == incoming.SecondaryClass)
                return incoming;

            var kept = new DeckList(stored.PrimaryClass, stored.SecondaryClass)
            {
                Id = incoming.Id,
                Name = incoming.Name,
                CardBackId = incoming.CardBackId,
                ShowcaseCardId = incoming.ShowcaseCardId,
            };
            kept.Entries.AddRange(incoming.Entries);
            return kept;
        }
    }
}
