using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// One row of the deck switcher (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.7): the deck and its queue
    /// standing — everything the row draws, resolved once.
    ///
    /// <para><b><c>IsDefault</c> was here until 2026-09-04</b> and went with characters (ADR-0009).
    /// It marked the one deck a character opened on; with a flat deck list there is no "which of
    /// these do I open on" question, so there is no star and no ordering derived from one.</para>
    /// </summary>
    public readonly struct DeckSummary
    {
        public readonly DeckList Deck;

        public readonly QueueCheck Queue;

        public DeckSummary(DeckList deck, QueueCheck queue)
        {
            Deck = deck;
            Queue = queue;
        }

        /// <summary>The draft badge (§2.7 <c>.draft-badge</c>).</summary>
        public bool IsDraft => Queue.IsDraft;

        /// <summary>The <c>n</c> of the row's <c>n / 30</c>.</summary>
        public int TotalCount => Deck != null ? Deck.TotalCount : 0;
    }

    /// <summary>
    /// Read-side queries over saved decks. Split out from the stores because these need the card
    /// database: legality is <b>computed</b>, never stored (§1), so any query that reports it has to
    /// resolve cards.
    ///
    /// <para><b>Most of this file went with characters (ADR-0009, 2026-09-04)</b>, and the deletions
    /// are the interesting part:</para>
    ///
    /// <list type="bullet">
    /// <item><c>BoundTo(characterId, decks)</c> — the L5 membership test. There is no membership: a
    /// deck belongs to the account, and the account's decks are the decks.</item>
    /// <item><c>Orphans(decks, characters)</c> — decks whose <c>CharacterId</c> resolved to nobody.
    /// <b>The state is now unreachable</b>, not merely unqueried. It was crash residue from
    /// <c>CharacterCreation</c>'s two-write ordering, and there is one write now.</item>
    /// <item><c>CharacterDecks</c>, with its <c>DefaultResolved</c> and <c>Opening</c> — the sidebar's
    /// "open on the default, or say it is missing" contract. No default, nothing to resolve.</item>
    /// </list>
    /// </summary>
    public static class DeckQuery
    {
        /// <summary>
        /// Every deck on the account, in stored order, each carrying its <see cref="QueueCheck"/>.
        ///
        /// <para><b>Stored order</b> is the order <see cref="IDeckStore.LoadAll"/> returns, which is
        /// the order they were created in. <see cref="DeckList"/> carries no timestamp and does not
        /// gain one here: this assembly is deterministic and has no clock, a "recently edited" sort
        /// would reshuffle the switcher under the player between visits, and a persisted field with no
        /// consumer is a field that goes wrong quietly. If recency is ever wanted it is additive.</para>
        ///
        /// <para><paramref name="owned"/> reaches <see cref="QueueEligibility"/> untouched (NOR-238).
        /// The switcher's draft badge and the queue gate have to be the same sentence, so the row
        /// cannot be allowed to judge a deck by a rule the gate does not apply — a deck badged ready
        /// that match-join then refuses is precisely the disagreement this query exists to prevent.
        /// Null is the full-pool alpha, exactly as everywhere else.</para>
        /// </summary>
        public static IReadOnlyList<DeckSummary> ForAccount(IEnumerable<DeckList> decks, ICardDatabase db,
                                                            IOwnedCardCounts? owned = null)
        {
            var rows = new List<DeckSummary>();
            if (decks == null) return rows;

            foreach (var deck in decks)
                if (deck != null)
                    rows.Add(new DeckSummary(deck, QueueEligibility.Check(deck, db, owned)));

            return rows;
        }
    }
}
