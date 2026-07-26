using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// Read-side queries over a character roster. Pure list work — no storage, no card database — so
    /// the deckbuilder can call it against a freshly-loaded roster or an in-memory edit buffer.
    ///
    /// <para>Every method preserves the roster's stored order, because that order is what the S6a
    /// side panel shows and a list that reshuffles between visits reads as a bug.</para>
    /// </summary>
    public static class CharacterQuery
    {
        /// <summary>
        /// Characters whose <b>Primary or Secondary</b> is <paramref name="cardClass"/> — the S6a
        /// side panel's list for the class tab you're standing on (ui/collection-deckbuilder.md §2).
        ///
        /// <para>Order matters to the caller in one way the filter does not express: a character
        /// listed under its <i>secondary</i> class plays a different hero power than one listed under
        /// its primary. The panel is expected to say which; this method only decides membership.</para>
        ///
        /// <para><c>NEUTRAL</c> returns empty by construction — no valid pairing contains it
        /// (<see cref="Character.IsValidPairing"/>), so the S6 Neutral tab has no character list.</para>
        /// </summary>
        public static IReadOnlyList<Character> ForClass(IEnumerable<Character> characters, CardClass cardClass)
        {
            var result = new List<Character>();
            if (characters == null) return result;
            foreach (var c in characters)
                if (c != null && (c.PrimaryClass == cardClass || c.SecondaryClass == cardClass))
                    result.Add(c);
            return result;
        }

        /// <summary>
        /// Characters whose <b>Primary</b> is <paramref name="cardClass"/> — i.e. the ones that play
        /// this class's hero power. Narrower than <see cref="ForClass"/>; pairs are ordered, so this
        /// is the set a "which of my characters casts Searing Bolt?" question actually wants.
        /// </summary>
        public static IReadOnlyList<Character> ForPrimary(IEnumerable<Character> characters, CardClass cardClass)
        {
            var result = new List<Character>();
            if (characters == null) return result;
            foreach (var c in characters)
                if (c != null && c.PrimaryClass == cardClass)
                    result.Add(c);
            return result;
        }

        /// <summary>
        /// Resolve the <see cref="DeckList.CharacterId"/> binding. Returns <c>null</c> when the
        /// character is gone — which is not an error here but the input to one: pass the null
        /// straight to <see cref="DeckValidator.Validate"/> and it becomes the L5 violation the deck
        /// is supposed to show on open (§3, "surface as invalid, never silently repair").
        /// </summary>
        public static Character? ById(IEnumerable<Character> characters, string characterId)
        {
            if (characters == null || string.IsNullOrEmpty(characterId)) return null;
            foreach (var c in characters)
                if (c != null && c.Id == characterId) return c;
            return null;
        }

        /// <summary>
        /// The deck a character opens on, or <c>null</c> if <see cref="Character.DefaultDeckId"/> is
        /// unset or points at a deck that no longer exists.
        ///
        /// <para>Null is the reportable state, not a condition to repair. The panel shows "no default
        /// deck" and lets the player pick one; silently substituting another deck would open the
        /// character on something they never chose. Same call as an orphaned deck reporting L5 rather
        /// than being rebound.</para>
        /// </summary>
        public static DeckList? DefaultDeckOf(Character character, IEnumerable<DeckList> decks)
        {
            if (character == null || decks == null || string.IsNullOrEmpty(character.DefaultDeckId)) return null;
            foreach (var d in decks)
                if (d != null && d.Id == character.DefaultDeckId) return d;
            return null;
        }
    }
}
