using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// What a brand-new character's default deck starts as (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.11).
    /// </summary>
    public enum StarterDeck
    {
        /// <summary>A curve-weighted legal 30 from <see cref="DefaultDeckGenerator"/>.</summary>
        Generated,

        /// <summary>
        /// A real saved deck with no cards in it. Still the character's default, still opens in Build
        /// mode, and a draft under §1 until it reaches 30 — deliberately not a special case anywhere.
        /// </summary>
        Blank,
    }

    /// <summary>
    /// Creating a character is three writes that must happen in one order, so it lives here rather
    /// than in the caller (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2, "Create a Character" → S6 Build mode).
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
        /// Whether the create form offers the blank-or-generated choice at all (§2.11, Sam
        /// 2026-07-26). <b>The first character never gets the choice</b> — a player with an empty
        /// roster has nothing to build from, and an empty deck as the first thing the game shows is a
        /// worse introduction than a playable one. From the second character on it is asked.
        ///
        /// <para>The rule lives here rather than in the create screen for the same reason
        /// <see cref="QueueEligibility"/> does: a policy the UI re-derives is a policy that drifts.
        /// It keys on the roster being empty, not a first-run flag, so a player who deletes every
        /// character is genuinely back to having nothing to build from and is handed a deck again.</para>
        /// </summary>
        public static bool OffersStarterChoice(IReadOnlyList<Character> roster) => roster != null && roster.Count > 0;

        /// <summary>
        /// Create a character, give it a starter deck, and persist both.
        /// </summary>
        /// <param name="characterId">Caller-supplied; this assembly mints no identity.</param>
        /// <param name="deckId">Caller-supplied id for the starter deck.</param>
        /// <param name="rng">Seeded by the caller — the same seed produces the same starter deck. Unused when <paramref name="starter"/> is <see cref="StarterDeck.Blank"/>.</param>
        /// <param name="starter">Generated or blank (§2.11). The default is what the first character always gets.</param>
        /// <param name="owned">
        /// What this account owns, so a generated starter deck is one it can actually queue with
        /// (L6). Null means "ownership unknown, do not constrain" — see <see cref="DefaultDeckGenerator"/>.
        /// </param>
        /// <exception cref="ArgumentException">The pairing is invalid (see <see cref="Character.IsValidPairing"/>).</exception>
        /// <exception cref="DeckGenerationException">The pool cannot supply a legal 30.</exception>
        public static Character Create(
            ICharacterStore characterStore, IDeckStore deckStore, ICardDatabase db, DeterministicRandom rng,
            string characterId, string name, CardClass primary, CardClass secondary, string deckId,
            StarterDeck starter = StarterDeck.Generated, IOwnedCardCounts? owned = null)
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

            // A blank starter asks nothing of the card pool, so it does not consult one: a pairing too
            // thin to generate 30 can still hold a deck you build by hand.
            var deck = starter == StarterDeck.Blank
                ? NewEmptyDeck(characterId, deckId)
                : DefaultDeckGenerator.Generate(character, db, rng, deckId, name + " Starter", owned);

            deckStore.Save(deck);
            character.DefaultDeckId = deck.Id;
            characterStore.Save(character);

            return character;
        }

        /// <summary>
        /// Create a character with <b>no deck at all</b>, and persist just the roster row (NOR-278).
        ///
        /// <para><b>Why this exists alongside <see cref="Create"/>.</b> The portrait-grid creation flow
        /// (NOR-278) chooses only the class pairing and a name; the first deck is a later, separate
        /// decision made from the roster's "+" plate. A character with an empty
        /// <see cref="Character.DefaultDeckId"/> and no bound decks is already a legal, fully-rendered
        /// state — it is exactly what a character whose decks were all deleted looks like (a lone "+"
        /// row) — so landing there needs no deck, and manufacturing a throwaway one would be state the
        /// player did not ask for.</para>
        ///
        /// <para><b>No crash-ordering concern.</b> <see cref="Create"/>'s deck-first ordering exists to
        /// keep a half-written pair from leaving a character pointing at a missing deck. There is only
        /// one write here and nothing points anywhere, so a death mid-write leaves either no character
        /// or a whole one — never a dangling reference.</para>
        /// </summary>
        /// <param name="characterId">Caller-supplied; this assembly mints no identity.</param>
        /// <exception cref="ArgumentException">The pairing is invalid (see <see cref="Character.IsValidPairing"/>).</exception>
        public static Character CreateWithoutDeck(
            ICharacterStore characterStore,
            string characterId, string name, CardClass primary, CardClass secondary)
        {
            if (characterStore == null) throw new ArgumentNullException(nameof(characterStore));

            // Same up-front pairing guard as Create — rejected before the single write, so a bad
            // pairing leaves no roster row. JsonCharacterStore.Save re-checks it regardless.
            if (!Character.IsValidPairing(primary, secondary))
                throw new ArgumentException(
                    $"'{primary}/{secondary}' is not a valid pairing (both classes must be real and distinct).",
                    nameof(primary));

            var character = new Character(characterId, name, primary, secondary);
            characterStore.Save(character);
            return character;
        }

        /// <summary>
        /// The blank starter. Named "New Deck" — the same name every empty deck gets on creation
        /// (§2.11), because the player is about to name it and a character-specific placeholder would
        /// only look like it meant something.
        /// </summary>
        private static DeckList NewEmptyDeck(string characterId, string deckId)
        {
            if (string.IsNullOrWhiteSpace(deckId))
                throw new ArgumentException("Starter deck needs an id from the caller.", nameof(deckId));

            return new DeckList { Id = deckId, CharacterId = characterId, Name = "New Deck" };
        }
    }
}
