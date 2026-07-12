using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Contracts;

namespace Emberfall.Data
{
    /// <summary>
    /// Fixed 20-card prebuilt decklists for the M1 vertical slice (MatchSetup, ARCHITECTURE §6 / M1-KICKOFF §3).
    /// Deckbuilding is out of M1 scope; these lists give BeginMatch its deterministic input.
    /// Max 2 copies per card; 1 for Legendaries (GDD §deckbuilding).
    /// </summary>
    public static class PrebuiltDecks
    {
        public static MatchSetup EmberwardVsTidecaller() => new MatchSetup(
            CardClass.EMBERWARD, EmberwardDeck(),
            CardClass.TIDECALLER, TidecallerDeck());

        private static IReadOnlyList<string> EmberwardDeck() => new string[]
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
            "core_emberling",        "core_emberling",
            "core_stoneback",        "core_stoneback",
            "core_hollow_acolyte",
            "core_aegis_templar",
        };

        private static IReadOnlyList<string> TidecallerDeck() => new string[]
        {
            // TIDECALLER class cards
            "tide_initiate",         "tide_initiate",
            "tide_oracle",           "tide_oracle",
            "frost_ripple",          "frost_ripple",
            "tide_reefguard",        "tide_reefguard",
            "tide_deep_bargain",
            "wave_caller",
            // Neutral fill
            "stone_sentry",          "stone_sentry",
            "wandering_scout",       "wandering_scout",
            "core_stoneback",        "core_stoneback",
            "core_bonehound",        "core_bonehound",
            "core_hollow_acolyte",
            "core_throneless_king",
        };
    }
}
