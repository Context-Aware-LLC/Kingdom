using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// The fixed fallback decklists — <b>30 cards, the format size</b> (<c>DeckValidator.DeckSize</c>,
    /// which lives in Emberfall.Core.Collection and is therefore named here rather than referenced —
    /// this assembly does not depend on that one). Ported out of the Unity tree
    /// so the headless server can construct matches
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §5-bis, RC F1) — this is the single source; the Unity-side
    /// <c>Emberfall.Data.PrebuiltDecks</c> delegates here. Max 2 copies per card; 1 for Legendaries;
    /// every card is the seat's own class or NEUTRAL, so each list is legal for any character whose
    /// primary is that class.
    ///
    /// <para><b>This type sets the HP and the deck size for EVERY live match, not just the ones that
    /// fall back to it.</b> <c>MatchGate.StartMatch</c> reads <see cref="MatchSetup.HeroMaxHealth"/>
    /// and the spark token off this pair even when both seats submitted their own decks — a submitted
    /// deck changes the cards and the hero class and nothing else. Anything here is a dial on the
    /// whole game.</para>
    /// </summary>
    public static class PrebuiltDecks
    {
        // 2026-08-27 (Sam): 30 HP and 30-card lists, ending the interim 20/20 dial set 2026-07-20.
        // That dial existed so a 20-CARD slice deck closed out on damage before both decks ran dry;
        // the lists are the format's 30 cards now and the format's 30 HP goes with them. HP is left
        // implicit — MatchSetup's own default is 30, so there is one number, not two that can drift.
        public static MatchSetup EmberwardVsTidecaller() => new MatchSetup(
            CardClass.EMBERWARD, EmberwardDeck(),
            CardClass.TIDECALLER, TidecallerDeck());

        public static IReadOnlyList<int> EmberwardDeck() => new int[]
        {
            // EMBERWARD class cards (2x each Common/Rare; 1x Legendary)
            1001,         1001,
            1002,          1002,
            151,            151,
            153,          153,
            1003,         1003,
            1019,         1019,
            51,    51,
            193,
            1052,
            1055,
            1105,
            1138,
            // Neutral fill
            1007,          1007,
            1009,        1009,
            1010,        1010,
            1011,         1011,
            1013,         1013,
            1018,
        };

        public static IReadOnlyList<int> TidecallerDeck() => new int[]
        {
            // TIDECALLER class cards. 152 frost_ripple is deliberately NOT here: it reads as removal
            // and hits only enemy BEASTS, of which the corpus holds 23 in 293 cards, so both copies
            // were blanks in the 20-card list this replaced.
            1004,         1004,
            169,          169,
            1108,        1108,
            1022,        1022,
            1005,           1005,
            168,          168,
            157,
            1006,         1006,
            1109,        1109,
            1057,
            1143,
            // Neutral fill
            1008,       1008,
            1010,        1010,
            1007,          1007,
            1017,        1017,
            1013,        1013,
            1018,
        };
    }
}
