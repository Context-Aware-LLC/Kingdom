using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// One row of the deck switcher (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.7): the deck, whether it is the
    /// Character's default, and its queue standing — everything the row draws, resolved once.
    /// </summary>
    public readonly struct DeckSummary
    {
        public readonly DeckList Deck;

        /// <summary>True iff <see cref="Character.DefaultDeckId"/> names this deck. Marked in the switcher.</summary>
        public readonly bool IsDefault;

        public readonly QueueCheck Queue;

        public DeckSummary(DeckList deck, bool isDefault, QueueCheck queue)
        {
            Deck = deck;
            IsDefault = isDefault;
            Queue = queue;
        }

        /// <summary>The draft badge (§2.7 <c>.draft-badge</c>).</summary>
        public bool IsDraft => Queue.IsDraft;

        /// <summary>The <c>n</c> of the row's <c>n / 30</c>.</summary>
        public int TotalCount => Deck != null ? Deck.TotalCount : 0;
    }

    /// <summary>
    /// What the sidebar needs to open on a Character (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.7): that
    /// Character's decks, default first, each already judged.
    /// </summary>
    public readonly struct CharacterDecks
    {
        public readonly Character? Character;

        /// <summary>Default first, then stored order. Empty for a Character with no decks.</summary>
        public readonly IReadOnlyList<DeckSummary> Decks;

        /// <summary>
        /// False when <see cref="Character.DefaultDeckId"/> is unset, or names a deck that no longer
        /// exists, or names one bound to a different Character. §3's rule verbatim: the sidebar opens
        /// on the first remaining deck <b>and says the default is missing</b> — it does not repair the
        /// reference. This bool is that sentence's input; without it the UI would have to re-derive it
        /// from the ordering, which is exactly the drift this query exists to remove.
        /// </summary>
        public readonly bool DefaultResolved;

        public CharacterDecks(Character? character, IReadOnlyList<DeckSummary> decks, bool defaultResolved)
        {
            Character = character;
            Decks = decks;
            DefaultResolved = defaultResolved;
        }

        public int Count => Decks.Count;

        /// <summary>
        /// The deck the sidebar opens on — the default when it resolved, otherwise the first remaining
        /// deck — or null for a Character with no decks. Because the default sorts first this is
        /// always <c>Decks[0]</c>; the property exists so no caller has to know that.
        /// </summary>
        public DeckSummary? Opening => Decks.Count > 0 ? Decks[0] : (DeckSummary?)null;
    }

    /// <summary>
    /// Read-side queries over saved decks. The counterpart to <see cref="CharacterQuery"/>, split off
    /// because these need the card database: legality is <b>computed</b>, never stored (§1), so any
    /// query that reports it has to resolve cards.
    ///
    /// <para>The point of <see cref="ForCharacter"/> is that the sidebar makes one call instead of
    /// four — load, filter on <see cref="DeckList.CharacterId"/>, resolve the default separately, run
    /// <see cref="DeckValidator"/> per deck. Each of those four is a chance to drift from what
    /// match-join does, and the draft badge disagreeing with the queue gate is precisely the bug that
    /// would produce.</para>
    /// </summary>
    public static class DeckQuery
    {
        /// <summary>
        /// A Character's decks, <b>default first</b>, each carrying its <see cref="QueueCheck"/>.
        ///
        /// <para><b>Order after the default is stored order</b> — the order
        /// <see cref="IDeckStore.LoadAll"/> returns, which is the order they were created in.
        /// <see cref="DeckList"/> carries no timestamp and does not gain one here: this assembly is
        /// deterministic and has no clock, a "recently edited" sort would reshuffle the switcher under
        /// the player between visits, and a persisted field with no consumer is a field that goes
        /// wrong quietly. If recency is ever wanted it is additive, exactly as
        /// <see cref="Character.DefaultDeckId"/> was.</para>
        ///
        /// <para>A null Character returns empty rather than every orphan — see
        /// <see cref="Orphans"/> for the deck whose owner is gone.</para>
        /// </summary>
        public static CharacterDecks ForCharacter(Character? character, IEnumerable<DeckList> decks, ICardDatabase db)
        {
            var rows = new List<DeckSummary>();
            if (character == null || decks == null)
                return new CharacterDecks(character, rows, defaultResolved: false);

            bool wantsDefault = !string.IsNullOrEmpty(character.DefaultDeckId);
            int defaultAt = -1;

            foreach (var deck in BoundTo(character.Id, decks))
            {
                bool isDefault = wantsDefault && defaultAt < 0 && deck.Id == character.DefaultDeckId;
                if (isDefault) defaultAt = rows.Count;
                rows.Add(new DeckSummary(deck, isDefault, QueueEligibility.Check(deck, character, db)));
            }

            // Default to the front, everything else stable behind it.
            if (defaultAt > 0)
            {
                var def = rows[defaultAt];
                rows.RemoveAt(defaultAt);
                rows.Insert(0, def);
            }

            return new CharacterDecks(character, rows, defaultResolved: defaultAt >= 0);
        }

        /// <summary>
        /// The decks bound to one <see cref="Character.Id"/>, in stored order — the L5 membership test,
        /// defined once. Card-database free, so callers that only need the set (the delete confirm's
        /// count, §2.6) do not have to hold one.
        ///
        /// <para><see cref="ForCharacter"/> and <see cref="CharacterDeletion"/> both go through here
        /// on purpose: "which decks are this character's" answered in two places is two answers
        /// waiting to disagree, and the place it would show up is a confirm dialog under-counting what
        /// it is about to destroy.</para>
        /// </summary>
        public static IReadOnlyList<DeckList> BoundTo(string characterId, IEnumerable<DeckList> decks)
        {
            var result = new List<DeckList>();
            if (decks == null || string.IsNullOrEmpty(characterId)) return result;

            foreach (var deck in decks)
                if (deck != null && deck.CharacterId == characterId)
                    result.Add(deck);
            return result;
        }

        /// <summary>
        /// Decks whose <see cref="DeckList.CharacterId"/> resolves to no Character on the roster.
        ///
        /// <para><b>An orphan is only ever an accident</b> (§5 O-4). A character delete destroys its
        /// decks (<see cref="CharacterDeletion"/>), so nothing produces orphans deliberately; what
        /// reaches here is crash residue — <see cref="CharacterCreation"/> writes the deck first, so a
        /// death between the two writes strands one — or a damaged <c>decks.json</c>. The store never
        /// filters, so they survive to be reported instead of vanishing.</para>
        ///
        /// <para><b>Its own query on purpose.</b> An orphan belongs to no Character, so it can appear
        /// in no Character's switcher; folding it into <see cref="ForCharacter"/> would put a deck in
        /// a list it does not belong to. Left only out it would be invisible, so the fact is queryable
        /// here and the deck-pick chooser (§2.11) is the surface that shows it — as an unselectable
        /// L5 row offering <b>delete only</b>. Recovering a deck whose Character never existed is
        /// export's job, not the chooser's.</para>
        ///
        /// <para>Every deck returned here is a draft by construction: no character means the L5
        /// violation, so <see cref="QueueEligibility"/> denies it the queue.</para>
        /// </summary>
        public static IReadOnlyList<DeckList> Orphans(IEnumerable<DeckList> decks, IEnumerable<Character> characters)
        {
            var result = new List<DeckList>();
            if (decks == null) return result;

            foreach (var deck in decks)
                if (deck != null && CharacterQuery.ById(characters, deck.CharacterId) == null)
                    result.Add(deck);
            return result;
        }
    }
}
