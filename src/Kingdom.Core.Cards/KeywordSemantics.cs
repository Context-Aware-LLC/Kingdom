using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>
    /// Separates live carrier behavior from enum slots that hold a wire/hash ordinal and nothing else.
    /// BACKDRAFT, SILENCE and DROWN are typed actions whose presentation markers derive from those
    /// actions; REINFORCE is the same shape with its action retired outright, leaving only the enum
    /// ordinal a renumbering would otherwise disturb. All four answer the same way here — not carrier
    /// behavior, and never admissible in runtime keyword state — which is the only question this
    /// class is asked.
    /// </summary>
    public static class KeywordSemantics
    {
        public static bool IsActionMarker(Keyword keyword)
            => keyword == Keyword.BACKDRAFT || keyword == Keyword.SILENCE || keyword == Keyword.DROWN
               || keyword == Keyword.REINFORCE;

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
            // No keyword overrides its enum id today, and FROST is the one that used to.
            //
            // Sam, 2026-08-25: the KEYWORD prints "Frost" and the ACTION prints "Freeze". Until now
            // one string did both jobs, and the cost was that a card converted from the action to
            // the keyword read the same afterwards as before — "Freeze an enemy." became "Freeze."
            // and nothing on the face said which of the two mechanics the card had. They are not
            // the same mechanic: the action picks a target and freezes it on the spot, the keyword
            // freezes whatever the carrier damages, and Tidecaller is being moved from the first to
            // the second across a dozen rows.
            //
            // This REPLACES the 2026-08-11 ruling, whose objection was that "frost is not a verb in
            // English, so 'Frost a minion.' did not parse as a sentence". True, and the split
            // dissolves it rather than overrules it — the verb is still "Freeze", it just is not
            // the keyword's name any more. See CardTextGenerator.FrostVerb, which is where the verb
            // now lives and the ONLY reason this may return null for FROST.
            => null;
    }
}
