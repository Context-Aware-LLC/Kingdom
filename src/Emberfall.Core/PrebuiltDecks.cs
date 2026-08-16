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

        public static IReadOnlyList<string> EmberwardDeck() => new string[]
        {
            // EMBERWARD class cards (2x each Common/Rare; 1x Legendary)
            "ember_recruit",         "ember_recruit",
            "ember_zealot",          "ember_zealot",
            "flame_bolt",            "flame_bolt",
            "ember_scorch",          "ember_scorch",
            "cinder_drake",
            "ember_overheat_golem",
            "ember_cinder_lance",    "ember_cinder_lance",
            // Neutral fill
            "stone_sentry",          "stone_sentry",
            "neutral_emberling",        "neutral_emberling",
            "neutral_stoneback_sentry",        "neutral_stoneback_sentry",
            "neutral_hollow_acolyte",
            "neutral_aegis_templar",
        };

        public static IReadOnlyList<string> TidecallerDeck() => new string[]
        {
            // TIDECALLER class cards
            "tide_initiate",         "tide_initiate",
            "tide_oracle",           "tide_oracle",
            "tide_frost_ripple",          "tide_frost_ripple",
            "tide_reefguard",        "tide_reefguard",
            "tide_deep_bargain",
            "tide_wave_caller",
            // Neutral fill
            "stone_sentry",          "stone_sentry",
            "wandering_scout",       "wandering_scout",
            "neutral_stoneback_sentry",        "neutral_stoneback_sentry",
            "neutral_bonehound",        "neutral_bonehound",
            "neutral_hollow_acolyte",
            "neutral_throneless_king",
        };
    }
}
