using System;
using System.Collections.Generic;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// Deleting a character deletes its decks (Sam, 2026-07-26 — hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3).
    ///
    /// <para><b>Why a cascade here when the rest of this assembly refuses to touch what it did not
    /// ask about.</b> A deck belongs to exactly one Character (L5) and cannot be played without one,
    /// so keeping it would preserve something the player can no longer use. What makes that
    /// acceptable — and makes it different from the silent repairs §3 forbids — is that the player is
    /// told: the confirm dialog names the count (§2.6), and <see cref="DecksOf"/> is where it gets the
    /// number. A destruction the player authorised by count is not a silent one.</para>
    ///
    /// <para><b>The order is the point, same as <see cref="CharacterCreation"/>.</b> Character first,
    /// then its decks. Two stores are two files and cannot be made atomic, so the choice is which half
    /// survives a crash between them — and decks with no character are the survivable half. They are
    /// reachable, reportable (L5) and deletable from the deck-pick chooser (§5 O-4); a character whose
    /// decks are half gone is none of those.</para>
    /// </summary>
    public static class CharacterDeletion
    {
        /// <summary>
        /// The decks a delete would destroy — what the confirm dialog counts (§2.6). Card-database
        /// free, because counting what is about to be destroyed does not need to know if it is legal.
        /// </summary>
        public static IReadOnlyList<DeckList> DecksOf(string characterId, IEnumerable<DeckList> decks) =>
            DeckQuery.BoundTo(characterId, decks);

        /// <summary>
        /// Delete the character and every deck bound to it. Returns the number of decks destroyed, so
        /// the caller's toast can say it without recounting.
        ///
        /// <para>Idempotent in the way that matters: an id that names no character still sweeps any
        /// decks bound to it, which is exactly the crash-residue case above finishing the job on the
        /// next attempt.</para>
        /// </summary>
        public static int Delete(ICharacterStore characterStore, IDeckStore deckStore, string characterId)
        {
            if (characterStore == null) throw new ArgumentNullException(nameof(characterStore));
            if (deckStore == null) throw new ArgumentNullException(nameof(deckStore));
            if (string.IsNullOrEmpty(characterId)) return 0;

            // Read the victims before anything is destroyed: after the character is gone they are
            // still findable by CharacterId, but reading first keeps the count and the deletion
            // describing the same set even if the store is swapped for a caching one later.
            var doomed = DecksOf(characterId, deckStore.LoadAll());

            characterStore.Delete(characterId);
            foreach (var deck in doomed)
                deckStore.Delete(deck.Id);

            return doomed.Count;
        }
    }
}
