using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// Fixed 20-card prebuilt decklists for the M1/M2 slice (MatchSetup, ARCHITECTURE §6 / M1-KICKOFF §3).
    /// Ported out of the Unity tree so the headless server can construct matches
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §5-bis, RC F1) — this is the single source; the Unity-side
    /// <c>Emberfall.Data.PrebuiltDecks</c> delegates here. Deckbuilding is out of slice scope;
    /// these lists give BeginMatch its deterministic input. Max 2 copies per card; 1 for Legendaries.
    /// </summary>
    public static class PrebuiltDecks
    {
        // Interim balance dial (2026-07-20, live-test feedback): 20 HP instead of the 30 default so
        // games close out on damage before both decks run dry to fatigue. Set here on the production
        // slice decks only — the MatchSetup default stays 30 so the golden-replay fixtures keep their
        // recorded hashes and the R1 determinism gate stays green. Revisit if 20 becomes permanent.
        public static MatchSetup EmberwardVsTidecaller() => new MatchSetup(
            CardClass.EMBERWARD, EmberwardDeck(),
            CardClass.TIDECALLER, TidecallerDeck(),
            heroMaxHealth: 20);

        public static IReadOnlyList<int> EmberwardDeck() => new int[]
        {
            // EMBERWARD class cards (2x each Common/Rare; 1x Legendary)
            1001,         1001,
            1002,          1002,
            151,            151,
            153,          153,
            1003,
            1019,
            51,    51,
            // Neutral fill
            1007,          1007,
            1009,        1009,
            1010,        1010,
            1011,
            1013,
        };

        public static IReadOnlyList<int> TidecallerDeck() => new int[]
        {
            // TIDECALLER class cards
            1004,         1004,
            1005,           1005,
            152,          152,
            1022,        1022,
            157,
            1006,
            // Neutral fill
            1007,          1007,
            1008,       1008,
            1010,        1010,
            1017,        1017,
            1011,
            1018,
        };
    }
}
