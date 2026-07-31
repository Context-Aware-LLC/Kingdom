using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    // Plan decision 15: status marks are state-driven and open-ended.
    // An icon mark is registered here by adding one dictionary entry —
    // no CardPresenter rewrite required. State-derived marks (board position,
    // enchantments, etc.) are added as explicit entries rather than wired
    // into the caller.
    public static class StatusMarkCatalog
    {
        // Explicitly registered keyword → UI-atlas address. Opt-in: a keyword
        // not present here silently shows no icon (not a warning — intentional
        // for SILENCE, OVERWHELM, VANGUARD, REINFORCE until art is authored).
        private static readonly Dictionary<Keyword, string> s_keywordIcons = new()
        {
            { Keyword.TAUNT,        "icon_kw_taunt"       },
            { Keyword.CHARGE,       "icon_kw_charge"      },
            { Keyword.RUSH,         "icon_kw_rush"        },
            { Keyword.DIVINE_SHIELD,"icon_kw_divine_shield"},
            { Keyword.BATTLECRY,    "icon_kw_battlecry"   },
            { Keyword.DEATHRATTLE,  "icon_kw_deathrattle" },
            { Keyword.LIFESTEAL,    "icon_kw_lifesteal"   },
            { Keyword.WINDFURY,     "icon_kw_windfury"    },
            { Keyword.STEALTH,      "icon_kw_stealth"     },
            { Keyword.POISONOUS,    "icon_kw_poisonous"   },
            { Keyword.SPELL_DAMAGE, "icon_kw_spell_damage"},
            { Keyword.FREEZE,       "icon_kw_freeze"      },
            { Keyword.OVERLOAD,     "icon_kw_overload"    },
            { Keyword.REBORN,       "icon_kw_reborn"      },
            // SILENCE   — an effect verb, not a card-face mark: intentionally absent
            // OVERWHELM — art deferred (EF-051 art track): intentionally absent
            // VANGUARD  — art deferred (EF-051 art track): intentionally absent
            // REINFORCE — art deferred: intentionally absent
        };

        // Returns the UI-atlas address for a keyword's icon mark, or null if none
        // is registered. A null return is intentional (art pending / not a face mark)
        // and suppresses no warning — the caller only warns when the atlas load fails
        // for an address that IS registered.
        public static string KeywordIconAddress(Keyword keyword) =>
            s_keywordIcons.TryGetValue(keyword, out string addr) ? addr : null;
    }
}
