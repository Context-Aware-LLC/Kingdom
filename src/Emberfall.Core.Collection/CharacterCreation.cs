using System;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// Creating a character is three writes that must happen in one order, so it lives here rather
    /// than in the caller (ui/collection-deckbuilder.md §2, "Create a Character" → S6 Build mode).
    ///
    /// <para><b>The order is the point.</b> Generate the starter deck, save the <i>deck</i>, then save
    /// the character carrying its id. Do it the other way round and a process that dies between the
    /// two writes leaves a character whose default deck does not exist. This way the same crash
    /// leaves an unreferenced deck, which is invisible and harmless. Neither store can be asked to
    /// make the pair atomic — they are two files — so the fix is to choose which half is survivable.</para>
    /// </summary>
    public static class CharacterCreation
    {
        /// <summary>
        /// Create a character, give it a generated starter deck, and persist both.
        /// </summary>
        /// <param name="characterId">Caller-supplied; this assembly mints no identity.</param>
        /// <param name="deckId">Caller-supplied id for the generated deck.</param>
        /// <param name="rng">Seeded by the caller — the same seed produces the same starter deck.</param>
        /// <exception cref="ArgumentException">The pairing is invalid (see <see cref="Character.IsValidPairing"/>).</exception>
        /// <exception cref="DeckGenerationException">The pool cannot supply a legal 30.</exception>
        public static Character Create(
            ICharacterStore characterStore, IDeckStore deckStore, ICardDatabase db, DeterministicRandom rng,
            string characterId, string name, CardClass primary, CardClass secondary, string deckId)
        {
            if (characterStore == null) throw new ArgumentNullException(nameof(characterStore));
            if (deckStore == null) throw new ArgumentNullException(nameof(deckStore));

            // Checked before anything is generated or written, so a rejected pairing costs nothing
            // and leaves no half-created state. JsonCharacterStore.Save would catch it too; catching
            // it at the top is what keeps the deck from being written first.
            if (!Character.IsValidPairing(primary, secondary))
                throw new ArgumentException(
                    $"'{primary}/{secondary}' is not a valid pairing (both classes must be real and distinct).",
                    nameof(primary));

            var character = new Character(characterId, name, primary, secondary);
            var deck = DefaultDeckGenerator.Generate(character, db, rng, deckId, name + " Starter");

            deckStore.Save(deck);
            character.DefaultDeckId = deck.Id;
            characterStore.Save(character);

            return character;
        }
    }
}
