using System.Collections.Generic;
using System.Text.RegularExpressions;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The summon sound for a minion, chosen by what its ART SHOWS rather than what it does.
    /// </summary>
    /// <remarks>
    /// <para><b>Creature kind outranks job title</b>, because a player expects to hear the body in
    /// the picture: <i>Charnel Priest</i> is a skeleton in robes and rattles like bone rather than
    /// intoning like a caster, and <i>Thornfang Matron</i> is a beast, not a serpent. That ordering
    /// is the whole design of the table below.</para>
    ///
    /// <para><b>Rules are ordered and the first match wins, so a NEW RULE MUST BE PLACED BY HAND.</b>
    /// Appended at the bottom it sits below the generic patterns and matches nothing — silently,
    /// because "no rule matched" and "matched the catch-all" produce the same silence for the three
    /// archetypes that currently have no clip.</para>
    ///
    /// <para><b>Why a pattern list and not 190 card ids.</b> A table of assignments drifts the
    /// moment a card is added or renamed, and nothing would report it: the new card would simply be
    /// silent. Matching on the name classifies a card that does not exist yet, which is the property
    /// worth having. <c>tooling/sfx/classify_minions.py</c> reads THIS FILE for its rules and prints
    /// the tally, so the reporting tool cannot disagree with the game.</para>
    ///
    /// <para><b>Three archetypes resolve to nothing on purpose.</b> <c>soldier</c> (the catch-all),
    /// <c>titan</c> and <c>treant</c> were rejected 2026-08-28 — the soldiers because they are human
    /// beings and a talking body may get a voice line instead of foley. An empty string means "no
    /// default", not "not classified": naming a clip that does not exist costs an Addressables
    /// warning on every summon and reads exactly like a broken router.</para>
    ///
    /// <para>RETIREMENT TRIGGER: promoting a clip for one of those three means filling in its stem
    /// here, in the same commit that lands the file under <c>Assets/Sound/SFX/</c>, and dropping it
    /// from the list in this paragraph and in <c>tooling/sfx/sound-library.md</c>.</para>
    /// </remarks>
    internal static class MinionSummonArchetypes
    {
        // Ordered; first match wins. Kept as one array rather than a switch so classify_minions.py
        // can read it: the script parses these lines, and a format change there is a parse failure
        // in that script rather than a silent divergence.
        private static readonly (string Stem, string Pattern)[] Rules =
        {
            ("imp_summon",       @"\bimp\b|imling|emberling pup"),
            ("beast_summon",     @"stag|\bcub\b|hawk|\bbat\b|wolf|hound|crawler|hoof|lifeclaw|ravenous|bog|matron|thornfang"),
            ("",                 @"colossus|titan|behemoth|leviathan|monolith|great mechanism|world-root|drowned"),
            ("drake_summon",     @"drake|wyrm|whelp|blazewing|venomscale|icescale"),
            ("serpent_summon",   @"viper|\basp\b|adder|venomfin|venomthorn|razorcoil|ink "),
            ("wisp_summon",      @"wisp|sprite|spectral|phantom|moth|shriek|hollow acolyte|memorial|lantern-bearer"),
            ("clockwork_summon", @"clockwork|automaton|construct|gear|cog|spring|drone|mechanism|artificer|voltaic|modular|blueprint|bronze frame|overclock|rebuilt|masterwork|assembly|mechanical|salvaged golem|nameless bell"),
            ("undead_summon",    @"revenant|risen|bone|skelet|corpse|charnel|tomb|grave|deathweave|plague|ashen|harrower|gatewatch|sable|tollkeeper|bell novice|soulbound|chained"),
            ("",                 @"root|bark|bramble|thorn|moss|canopy|grove|sapling|seed|dryad|overgrowth|thistleguard|windroot|praxis|ancient|elder"),
            ("stone_summon",     @"stone|granite|boulder|rock|pebble|quarry|fortress|fortified|unbreakable|wall|siege|duneborn|heartstone|cinderplate|slagshell|golem|ironclad|ironmount"),
            ("magma_summon",     @"magma|forge|kiln|pyre|brazier|ember|cinder|coal|blaze|slag|overheat|scald|zealot"),
            ("frost_summon",     @"frost|\bice|glacial|icecrown"),
            ("tide_summon",      @"tide|reef|wave|abyss|saltshore|flood|siren|maren|shell|prophet"),
            ("storm_summon",     @"storm|bolt|spark|static|thunder|crackl|gale|gust|wind|tempest|chain caller|arclight"),
            ("rogue_summon",     @"shadow|veil|spy|rogue|stalker|agent|recon|operative|scout|lurk|nightfall|whisper|dusk|kira|seraphine"),
            // Catch-all: an armoured humanoid is the default body. Empty because the soldiers were
            // rejected — see the class remarks.
            ("",                 @"."),
        };

        private static readonly Regex[] Compiled = Build();
        private static readonly Dictionary<int, string> Cache = new Dictionary<int, string>();

        private static Regex[] Build()
        {
            var compiled = new Regex[Rules.Length];
            for (int i = 0; i < Rules.Length; i++)
                compiled[i] = new Regex(Rules[i].Pattern, RegexOptions.CultureInvariant);
            return compiled;
        }

        /// <summary>The archetype stem for a minion, or an empty string where it has no clip.</summary>
        public static string Resolve(CardDefinition definition)
        {
            if (definition == null || definition.Type != CardType.MINION) return string.Empty;
            if (Cache.TryGetValue(definition.Id, out string cached)) return cached;

            // Name AND handle, because the handle carries words the display name drops
            // (clock_modular_stalker -> "modular"), and lowercased because every pattern is.
            string subject = ((definition.Name ?? string.Empty) + " " +
                              (definition.Handle ?? string.Empty)).ToLowerInvariant();
            string stem = string.Empty;
            for (int i = 0; i < Compiled.Length; i++)
            {
                if (!Compiled[i].IsMatch(subject)) continue;
                stem = Rules[i].Stem;
                break;
            }

            Cache[definition.Id] = stem;
            return stem;
        }
    }
}
