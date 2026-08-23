using System.Collections.Generic;

namespace Emberfall.Core.Collection
{
    /// <summary>One line of a decklist: a card definition id and how many copies (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3).</summary>
    public readonly struct DeckEntry
    {
        public readonly int CardId;
        public readonly int Count;
        public DeckEntry(int cardId, int count) { CardId = cardId; Count = count; }
        public DeckEntry With(int count) => new DeckEntry(CardId, count);
    }

    /// <summary>
    /// The persisted deck payload (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3): a <see cref="Character"/> binding,
    /// a name, and a small list of {definitionId, count}. Ported from the original Unity-only
    /// Emberfall.Data.DeckList (the C-G0-5 spike) — single-CardClass HeroClass replaced by CharacterId
    /// per the dual-class ruling. UnityEngine-free so this and <see cref="DeckValidator"/> move
    /// server-side unchanged (dev-docs/architecture/system-overview.md:15).
    /// </summary>
    public sealed class DeckList
    {
        public string Id { get; set; } = "";
        public string CharacterId { get; set; } = "";   // L5: a deck belongs to exactly one Character
        public string Name { get; set; } = "New Deck";

        /// <summary>
        /// The card back this deck presents, or empty for "whatever the build's default is".
        ///
        /// <para><b>Cosmetic, and unvalidated on purpose.</b> Nothing in this assembly knows which
        /// backs exist — the catalog is art, it lives client-side, and it grows with every set. An id
        /// naming a back this build has never heard of therefore loads intact and falls back to the
        /// default at render time, exactly like a card id a balance patch deleted. Refusing it here
        /// would make a deck unreadable because of a cosmetic.</para>
        ///
        /// <para><b>Per deck, not per seat.</b> The back is presented by the deck that is playing, so
        /// it belongs on the deck rather than on the account — but the seat is still what carries it
        /// onto the wire, and an opponent's back id is attacker-authored data by the time the client
        /// resolves it. Nothing sends it today.</para>
        /// </summary>
        public string CardBackId { get; set; } = "";

        /// <summary>
        /// The card whose art represents this deck, or empty to let the client pick one.
        ///
        /// <para><b>This is what a deck is recognised by</b> (Sam, 2026-08-07). Deck names are not
        /// drawn on the roster; the image is the identity, and the player chooses it from the cards
        /// in the deck. A name still exists and is still editable — it is what the dialog is titled
        /// and what a duplicate is disambiguated by — it is simply not the thing you scan for.</para>
        ///
        /// <para><b>Unvalidated, like <see cref="CardBackId"/> and for a stronger reason:</b> this id
        /// may name a card a balance patch deleted, or one the player has since removed from the
        /// deck. Neither is a corrupt deck. The client falls back when it cannot resolve the art, and
        /// the id is left alone so that putting the card back restores the image the player chose.</para>
        /// </summary>
        /// <summary>The card whose art fronts the deck. 0 when unset — no card is numbered 0.</summary>
        public int ShowcaseCardId { get; set; }

        public List<DeckEntry> Entries { get; } = new List<DeckEntry>();

        public int TotalCount
        {
            get
            {
                int n = 0;
                foreach (var e in Entries) n += e.Count;
                return n;
            }
        }

        public int CountOf(int cardId)
        {
            foreach (var e in Entries)
                if (e.CardId == cardId) return e.Count;
            return 0;
        }
    }
}
