using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Plan decision 15: status marks are state-driven and open-ended.
    // An icon mark is registered here by adding one dictionary entry —
    // no CardPresenter rewrite required. State-derived marks (board position,
    // enchantments, etc.) are added as explicit entries rather than wired
    // into the caller.
    public static class StatusMarkCatalog
    {
        // Explicitly registered keyword → UI-atlas address.
        // To add an icon: drop an entry here (art must exist in UI_Core first).
        private static readonly Dictionary<Keyword, string> s_keywordIcons = new()
        {
            { Keyword.BULWARK,        "icon_kw_bulwark"       },
            { Keyword.FERVOR,       "icon_kw_fervor"      },
            { Keyword.SKIRMISH,         "icon_kw_skirmish"        },
            { Keyword.AEGIS,"icon_kw_divine_shield"},
            { Keyword.HERALD,    "icon_kw_herald"   },
            { Keyword.TOLL,  "icon_kw_toll" },
            { Keyword.LIFEDRAIN,    "icon_kw_lifedrain"   },
            { Keyword.DOUBLESTRIKE,     "icon_kw_doublestrike"    },
            { Keyword.SHROUD,      "icon_kw_shroud"     },
            { Keyword.VENOM,    "icon_kw_venom"   },
            { Keyword.ARCANE_SURGE, "icon_kw_spell_damage"},
            { Keyword.FROST,       "icon_kw_frost"      },
            { Keyword.BACKDRAFT,     "icon_kw_backdraft"    },
            { Keyword.REKINDLE,       "icon_kw_rekindle"      },
        };

        // Keywords with no icon by design or with art pending — silently skip, no warning.
        // To activate: move the entry to s_keywordIcons once its art is in UI_Core.
        private static readonly HashSet<Keyword> s_noIcon = new()
        {
            Keyword.SILENCE,    // effect verb, not a card-face mark
            Keyword.OVERWHELM,  // art deferred — EF-051 art track
            Keyword.VANGUARD,   // art deferred — EF-051 art track
        };

        // Returns the UI-atlas address for a keyword's icon mark, or null if none applies.
        // Null for a keyword in s_noIcon is intentional and produces no warning.
        // Null for a keyword absent from both tables means the engine grew a new keyword
        // without a catalog entry — a warning fires so the gap is immediately visible.
        public static string KeywordIconAddress(Keyword keyword)
        {
            if (s_keywordIcons.TryGetValue(keyword, out string addr)) return addr;
            if (s_noIcon.Contains(keyword)) return null;
            Debug.LogWarning($"[StatusMarkCatalog] {keyword} has no icon entry — add to s_keywordIcons or s_noIcon (EF-044)");
            return null;
        }
    }
}
