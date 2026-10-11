using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
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
    /// The persisted deck payload (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3): a permanent class
    /// PAIRING, a name, and a small list of {definitionId, count}. UnityEngine-free so this and
    /// <see cref="DeckValidator"/> move server-side unchanged (dev-docs/architecture/system-overview.md:15).
    ///
    /// <para><b>The deck carries the pairing itself (ADR-0009, Sam 2026-09-04).</b> It used to carry a
    /// <c>CharacterId</c> pointing at a <c>Character</c> that held the two classes. That type is gone:
    /// the pairing was the only thing it did, so the folder was deleted and its contents moved down.
    /// The pairing ruling itself is untouched — ordered, permanent, two distinct real classes, and the
    /// hero power always comes from <see cref="PrimaryClass"/> (the HARD ruling: exactly one hero
    /// power on the board, never two). Pyra+Maren and Maren+Pyra remain different decks.</para>
    /// </summary>
    public sealed class DeckList
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "New Deck";

        /// <summary>
        /// The half of the pairing the hero — and therefore the hero power — comes from.
        ///
        /// <para><b>Immutable, and mandatory at construction.</b> There is no setter and no
        /// parameterless constructor, so a deck cannot exist without having decided its classes. The
        /// reason is weaker than the one that made a <c>Character</c>'s pairing permanent — a deck is
        /// not an identity — but it is sufficient: a mutable pairing would need a policy for the
        /// off-class cards already in the deck, and "delete it and build another" answers that
        /// completely at no cost to the player. <c>New Deck</c> therefore opens on a class picker.</para>
        /// </summary>
        public CardClass PrimaryClass { get; }

        /// <summary>The other half of the pool. Contributes no hero power. Immutable, as <see cref="PrimaryClass"/> is.</summary>
        public CardClass SecondaryClass { get; }

        /// <summary>The numeric card-back identity, or zero for the default. Unknown positive IDs
        /// survive storage for forward compatibility; the server checks ownership when saving.</summary>
        public int CardBackId { get; set; }

        /// <summary>
        /// The card whose art represents this deck, or empty to let the client pick one.
        ///
        /// <para><b>This is what a deck is recognised by</b> (Sam, 2026-08-07). Deck names are not
        /// drawn on the roster; the image is the identity, and the player chooses it from the cards
        /// in the deck. A name still exists and is still editable — it is what the dialog is titled
        /// and what a duplicate is disambiguated by — it is simply not the thing you scan for.</para>
        ///
        /// <para><b>Unvalidated on read, like <see cref="CardBackId"/>:</b> this id
        /// may name a card a balance patch deleted, or one the player has since removed from the
        /// deck. Neither is a corrupt deck. The client falls back when it cannot resolve the art, and
        /// the id is left alone so that putting the card back restores the image the player chose.</para>
        ///
        /// <para>0 when unset — no card is numbered 0.</para>
        /// </summary>
        public int ShowcaseCardId { get; set; }

        public List<DeckEntry> Entries { get; } = new List<DeckEntry>();

        /// <param name="primaryClass">The hero-power half. See <see cref="PrimaryClass"/>.</param>
        /// <param name="secondaryClass">The other half of the pool.</param>
        public DeckList(CardClass primaryClass, CardClass secondaryClass)
        {
            PrimaryClass = primaryClass;
            SecondaryClass = secondaryClass;
        }

        /// <summary>True iff a card of <paramref name="cardClass"/> is legal in this deck (L4).</summary>
        public bool AllowsClass(CardClass cardClass) =>
            AllowsClass(PrimaryClass, SecondaryClass, cardClass);

        /// <summary>
        /// L4's pool rule, for a caller holding a pairing but no deck — <see cref="DefaultDeckGenerator"/>
        /// filtering candidates before it has built anything. The instance overload delegates here so
        /// the rule has exactly one definition; a generator that drew from a pool the validator then
        /// rejected would be the defect, and it is only reachable if these two ever disagree.
        /// </summary>
        public static bool AllowsClass(CardClass primary, CardClass secondary, CardClass cardClass) =>
            cardClass == CardClass.NEUTRAL || cardClass == primary || cardClass == secondary;

        /// <summary>
        /// A pairing is valid iff both classes are real (non-NEUTRAL) classes and distinct
        /// (Pyra+Pyra is not a pairing). Construction-time helper — callers decide whether to
        /// reject or just flag an invalid pairing, and a STORED deck is reported as it was stored.
        /// </summary>
        public static bool IsValidPairing(CardClass primary, CardClass secondary) =>
            primary != CardClass.NEUTRAL && secondary != CardClass.NEUTRAL && primary != secondary;

        /// <summary>True iff this deck's own pairing is well-formed. See <see cref="IsValidPairing"/>.</summary>
        public bool HasValidPairing => IsValidPairing(PrimaryClass, SecondaryClass);

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
