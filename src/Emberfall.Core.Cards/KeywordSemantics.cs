using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// Separates live carrier behavior from enum slots that hold a wire/hash ordinal and nothing else.
    /// BACKDRAFT and SILENCE are typed actions whose presentation markers derive from those actions;
    /// REINFORCE names nothing. All three answer the same way here — not carrier behavior, and never
    /// admissible in runtime keyword state — which is the only question this class is asked.
    /// </summary>
    public static class KeywordSemantics
    {
        public static bool IsActionMarker(Keyword keyword)
            => keyword == Keyword.BACKDRAFT || keyword == Keyword.REINFORCE ||
               keyword == Keyword.SILENCE;

        public static bool IsCarrierBehavior(Keyword keyword)
            => System.Enum.IsDefined(typeof(Keyword), keyword) && !IsActionMarker(keyword);

        /// <summary>
        /// The player-facing word for a keyword whose enum id is not the word on the card, or null
        /// when title-casing the id is already right. The ONE place an authored keyword name lives:
        /// the card face humanises through <c>CardTextGenerator</c> and the tooltip through
        /// <c>KeywordCopy.DisplayName</c>, two humanisers in two assemblies, and an override written
        /// into only one of them is a card that disagrees with its own tooltip.
        ///
        /// <para>Null rather than the humanised default on purpose — a caller that forgets to fall
        /// back gets an obvious null, not a silently plausible name.</para>
        /// </summary>
        public static string? AuthoredName(Keyword keyword)
            // Sam, 2026-08-11. FROST keeps the enum id (ordinal 12 is wire/hash state, and the
            // engine's IsFrosted/FrostApplied/isFrosted spellings go with it), but the card says
            // "Freeze": frost is not a verb in English, so "Frost a minion." did not parse as a
            // sentence. Freeze collides with the genre's word for the same mechanic and that is
            // accepted here — one collision on a plain English verb, ruled cheaper than a keyword
            // the player cannot read. (This used to note that the vocabulary gate carved out an
            // exception for it; that gate was removed 2026-08-18. The ruling stands on its own.)
            => keyword == Keyword.FROST ? "Freeze" : null;
    }
}
