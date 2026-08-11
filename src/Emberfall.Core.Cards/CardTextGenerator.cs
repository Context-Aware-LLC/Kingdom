using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// Deterministically renders the normalized rules that a card already owns into player-facing
    /// text. Carrier keywords and normalized typed rules are the only inputs: trigger labels are
    /// derived from rule activation, never duplicated as keyword data.
    /// </summary>
    public static class CardTextGenerator
    {
        /// <summary>Renders a complete card face from its carrier keywords and its typed rules.</summary>
        public static string Generate(CardDefinition card)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            var carriers = new List<string>(card.Keywords.Count);
            foreach (Keyword keyword in card.Keywords)
            {
                // An action marker is rendered from its typed action instead. Printing it here as
                // well would print it twice on any row that still carries the legacy keyword.
                if (KeywordSemantics.IsActionMarker(keyword)) continue;
                carriers.Add(Name(keyword));
            }

            var parts = new List<string>(1 + card.Rules.Count);
            // Comma-separated and closed with a stop. Two separate jobs: the commas stop "Bulwark
            // Lifedrain" reading as one two-word keyword, and the stop separates the band from the
            // rules text that follows it. The trigger marker's colon used to do that second job —
            // "Bulwark Herald: Draw a card." — and ON_PLAY prints no marker any more, which would
            // have left "Bulwark Draw a card." Presentation emphasises each name on top of this.
            if (carriers.Count > 0) parts.Add(string.Join(", ", carriers) + ".");
            parts.AddRange(RenderRuleGroups(card.Rules));
            return string.Join(" ", parts);
        }

        /// <summary>Renders the rule half of a card face on its own, without any carrier keywords.</summary>
        /// <remarks>
        /// A test seam, not card-face API: every shipping caller renders a whole card. Kept internal
        /// so a rule list rendered without its carrier keywords cannot become a second, half-right
        /// way to print a card face. The friend declaration is in AssemblyInfo.cs — in code, because
        /// Unity never reads the .csproj.
        /// </remarks>
        internal static string Generate(IReadOnlyList<RuleDefinition> rules)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            return string.Join(" ", RenderRuleGroups(rules));
        }

        /// <summary>Renders the rules as card-face lines, one per run of rules sharing a prefix.</summary>
        /// <remarks>
        /// One line per RUN of consecutive rules sharing a trigger/condition/lifetime prefix, not one
        /// line per rule — a Toll with three effects is one "Toll:", not "Toll:" printed three
        /// times. Only adjacent rules merge, so an interleaved trigger (on-play, Toll, on-play)
        /// still prints the marker each time it actually changes.
        /// </remarks>
        private static List<string> RenderRuleGroups(IReadOnlyList<RuleDefinition> rules)
        {
            var lines = new List<string>();
            var groupActions = new List<string>();
            string groupPrefix = "";

            foreach (RuleDefinition rule in rules)
            {
                if (rule == null) throw new ArgumentNullException(nameof(rules));

                string prefix = TriggerMarker(rule.Activation) + ConditionPrefix(rule.Condition) +
                    LifetimePrefix(rule.Lifetime);
                if (groupActions.Count > 0 && prefix != groupPrefix)
                {
                    lines.Add(groupPrefix + string.Join(" ", groupActions));
                    groupActions.Clear();
                }

                groupPrefix = prefix;
                groupActions.Add(RenderAction(rule.Action));
            }

            if (groupActions.Count > 0)
                lines.Add(groupPrefix + string.Join(" ", groupActions));
            return lines;
        }

        /// <summary>The label an aura's rules are printed under.</summary>
        public const string AuraLabel = "Aura";

        /// <summary>The label the UNEARTH action prints. Not a <see cref="Keyword"/> — no card carries
        /// it — so it would be missing from any vocabulary derived from the enum alone.</summary>
        public const string UnearthLabel = "Unearth";

        /// <summary>
        /// Every word this generator prints as game vocabulary rather than as ordinary English:
        /// carrier keyword names, timing labels, and the two labels that belong to neither list.
        /// Presentation emphasises exactly these (Unity's CardTextStyling).
        ///
        /// <para>Derived, never hand-listed. A keyword added to the enum or a trigger given a label
        /// appears here without anyone remembering to, which is the whole reason this lives beside
        /// the code that prints the words instead of in the file that styles them. Ordered longest
        /// first so an alternation built from it prefers "Turn Start" over any shorter prefix.</para>
        /// </summary>
        public static IReadOnlyList<string> EmphasisVocabulary()
        {
            var words = new List<string> { AuraLabel, UnearthLabel };

            foreach (Keyword keyword in (Keyword[])Enum.GetValues(typeof(Keyword)))
                words.Add(Name(keyword));

            foreach (Trigger trigger in (Trigger[])Enum.GetValues(typeof(Trigger)))
            {
                // Trigger.AURA is not a triggered marker — an aura is an activation shape, and its
                // label is seeded above. TriggerLabel deliberately throws on it rather than growing
                // a branch that would let a TriggeredActivation(AURA) render as if it were valid.
                if (trigger == Trigger.AURA) continue;

                string label = TriggerLabel(trigger);
                if (label.Length > 0) words.Add(label);
            }

            words.Sort((a, b) => b.Length.CompareTo(a.Length));
            return words;
        }

        /// <summary>
        /// The bare timing label for a trigger — no colon, no punctuation — or "" for a trigger that
        /// prints unmarked. Public because Presentation emphasises the game's vocabulary by matching
        /// these words in the generated text (Unity's CardTextStyling): the labels are decided here,
        /// beside the code that prints them, so the styling list cannot drift from what is printed.
        ///
        /// <para><b>Playing the card is the unmarked default</b> (Sam, 2026-08-11). ON_CAST was always
        /// unmarked — a spell resolves when you cast it and needs no label saying so — and ON_PLAY is
        /// the same statement about a minion or a weapon, so it prints unmarked for the same reason.
        /// Every timing that is NOT "when you played it" still carries its label, which is what makes
        /// the absence of one meaningful. The retired label was "Herald": an invented word for the
        /// default case, on 84 of 261 rows, telling the player nothing they had not already assumed.
        /// Checked before removing it — all four ON_PLAY weapons carry that rule and no other, so no
        /// card anywhere gained a second possible reading.</para>
        /// </summary>
        public static string TriggerLabel(Trigger trigger) => trigger switch
        {
            Trigger.ON_PLAY => "",
            Trigger.ON_DEATH => "Toll",
            Trigger.ON_ATTACK => "Attack",
            Trigger.ON_DAMAGED => "Damaged",
            Trigger.ON_HEAL => "Healed",
            Trigger.ON_TURN_START => "Turn Start",
            Trigger.ON_TURN_END => "Turn End",
            Trigger.ON_FRIENDLY_SUMMON => "Friendly Summon",
            Trigger.ON_SPELL_CAST => "Spell Cast",
            Trigger.ON_CAST => "",
            _ => throw Unsupported("trigger", trigger),
        };

        /// <summary>Renders the marker a rule prints, derived from how it activates.</summary>
        /// <remarks>
        /// <b>The trailing colon is load-bearing.</b> Presentation renders both a timing marker and a
        /// carrier keyword as bold + 120% (Unity's CardTextStyling), so the two arrive at the player's
        /// eye styled identically with only a space between them — "Rekindle Toll Freeze a minion"
        /// reads as a list of three keywords rather than one keyword and one timing label. The colon
        /// is the only thing separating "what this minion IS" from "when this effect FIRES", and it
        /// stays outside the emphasised span. ON_PLAY and ON_CAST are deliberately unmarked — see
        /// <see cref="TriggerLabel"/> for why playing the card is the default that needs no label.
        /// </remarks>
        private static string TriggerMarker(RuleActivation activation)
        {
            if (activation is AuraActivation) return AuraLabel + ": ";
            if (activation is TriggeredActivation triggered) return TriggerMarker(triggered.Trigger);
            throw UnsupportedNode("activation", activation);
        }

        /// <summary>Maps one trigger to its marker; playing the card is deliberately unmarked.</summary>
        private static string TriggerMarker(Trigger trigger)
        {
            string label = TriggerLabel(trigger);
            return label.Length == 0 ? "" : label + ": ";
        }

        /// <summary>Renders the clause qualifying when a rule applies, ahead of its effects.</summary>
        private static string ConditionPrefix(RuleCondition condition)
        {
            if (condition is AlwaysCondition) return "";
            if (condition is TurnOwnerCondition turnOwner) return TurnOwnerPrefix(turnOwner.Owner);
            throw UnsupportedNode("condition", condition);
        }

        /// <summary>Maps a turn-owner condition to the clause naming whose turn it must be.</summary>
        private static string TurnOwnerPrefix(TurnOwner owner) => owner switch
        {
            TurnOwner.CONTROLLER => "While it is your turn, ",
            TurnOwner.OPPONENT => "While it is your opponent's turn, ",
            TurnOwner.ANY => "On either player's turn, ",
            _ => throw Unsupported("turn owner", owner),
        };

        /// <summary>Renders the clause bounding how long a rule applies, ahead of its effects.</summary>
        private static string LifetimePrefix(RuleLifetime lifetime)
        {
            if (lifetime is SourceBoundLifetime || lifetime is OneShotLifetime) return "";
            if (lifetime is PermanentLifetime) return "Permanently, ";
            if (lifetime is FiniteLifetime finite)
            {
                if (finite.Uses <= 0) throw Requires("a positive finite lifetime");
                return finite.Uses == 1 ? "For its next use, " : $"For its next {finite.Uses} uses, ";
            }
            throw UnsupportedNode("lifetime", lifetime);
        }

        /// <summary>Renders one typed action as a complete effect sentence.</summary>
        private static string RenderAction(RuleAction action) => action switch
        {
            DamageAction a => $"Deal {a.Amount} damage to {Target(a.Target)}.",
            HealAction a => $"Restore {a.Amount} Health to {Target(a.Target)}.",
            ModifyStatsAction a => $"Give {Target(a.Target)} {Delta(a.AttackDelta)}/{Delta(a.HealthDelta)}.",
            // Deliberately NOT a "Reinforce" marker, unlike its sibling action markers below.
            // REINFORCE is a fixed +1/+1 with no timing of its own — BuffHandler hardcodes 1/1 and
            // routes it through the same path as MODIFY_STATS — so the marker named a mechanic whose
            // whole definition is shorter than the word, had no entry in the player-facing keyword
            // glossary to look up, and sat next to the on-play marker it was routinely misread as a
            // synonym for. Printing the stats says the same thing with no vocabulary to learn, and
            // renders identically to an authored MODIFY_STATS +1/+1, which is what it already is.
            //
            // The target keeps saying "friendly" and must: nothing else on the line carries the
            // allegiance restriction once the marker is gone. RequiresFriendlyTarget enforces it
            // (RuleDefinition.cs), but enforcement is not what the player reads.
            ReinforceAction a => $"Give {Target(a.Target)} +1/+1.",
            GrantKeywordAction a => $"Give {Target(a.Target)} {Name(a.Keyword)}.",
            DestroyAction a => $"Destroy {Target(a.Target)}.",
            ReturnToHandAction a => $"Return {Target(a.Target)} to hand.",
            SilenceAction a => $"{Name(Keyword.SILENCE)} {Target(a.Target)}.",
            TransformAction a => $"Transform {Target(a.Target)} into {Name(a.CardId)}.",
            // Named through the keyword rather than spelled here, so the verb on the card and the
            // marker on a carrier of the same keyword cannot drift apart — KeywordSemantics
            // .AuthoredName is the one place the word is decided, and the tooltip reads the same
            // entry. Presentation emphasises the word by matching it, so it needs no delimiter.
            FrostAction a => $"{Name(Keyword.FROST)} {Target(a.Target)}.",
            TakeControlAction a => $"Take control of {Target(a.Target)}.",
            DrawAction a => $"Draw {Cards(a.Count)}.",
            SummonAction a => $"Summon {Copies(a.Count, Name(a.CardId))}.",
            AddToHandAction a => $"Add {Copies(a.Count, Name(a.CardId))} to your hand.",
            EquipAction a => $"Equip {Name(a.CardId)}.",
            GainManaAction a => Mana(a),
            BackdraftAction a => $"{Name(Keyword.BACKDRAFT)} ({a.Amount}).",
            GainArmorAction a => $"Gain {a.Amount} Armor.",
            UnearthAction a => $"{UnearthLabel} ({a.Count}).",
            _ => throw UnsupportedNode("action", action),
        };

        /// <summary>Renders a target spec as the noun phrase an effect sentence reads it as.</summary>
        private static string Target(TargetSpec target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            // -1 is the authored "every candidate" sentinel; any other non-positive count is nonsense.
            if (target.Count.HasValue && target.Count.Value <= 0 && target.Count.Value != -1)
                throw Unsupported("target count", target.Count);

            // Decided up front, not at the point of use, so an unrenderable choice policy is rejected
            // even when the phrasing below turns out to suppress the word "random".
            bool random = IsRandomlyChosen(target);

            string description = Quantity(PhrasingFor(target.Selector), target.Count, random);
            if (target.DistinctFrom.Length > 0) description = Another(description);
            if (target.Filter != null) description = Filter(description, target.Filter);
            // Only an explicit count of -1 still reads as "all" once random is accounted for, and
            // there the pick is every candidate in random ORDER — so the word "random" would claim
            // a narrowing that did not happen. Randomize() has no "all ..." case either, and used
            // to fall through to a nonsense "random all X".
            if (random && !description.StartsWith("all ", StringComparison.Ordinal))
                description = Randomize(description);
            return description;
        }

        /// <summary>Whether a target is chosen at random, rejecting any choice policy with no phrasing.</summary>
        private static bool IsRandomlyChosen(TargetSpec target) => target.ChosenBy switch
        {
            null => false,
            TargetChosenBy.PLAYER => false,
            TargetChosenBy.RANDOM => true,
            _ => throw Unsupported("target choice", target.ChosenBy),
        };

        /// <summary>How one selector is spoken, and which form an unspecified count means.</summary>
        private readonly struct SelectorPhrasing
        {
            public readonly string Singular;
            public readonly string Plural;

            /// <summary>Whether an unspecified count means every candidate rather than exactly one.</summary>
            public readonly bool DefaultsToAll;

            /// <summary>Binds the two spoken forms of a selector to the one an absent count means.</summary>
            public SelectorPhrasing(string singular, string plural, bool defaultsToAll)
            {
                Singular = singular;
                Plural = plural;
                DefaultsToAll = defaultsToAll;
            }
        }

        /// <summary>Phrases a selector that means ONE candidate unless an authored count widens it.</summary>
        private static SelectorPhrasing One(string singular, string plural)
            => new SelectorPhrasing(singular, plural, false);

        /// <summary>Phrases a selector that means EVERY candidate unless an authored count narrows it.</summary>
        private static SelectorPhrasing All(string singular, string plural)
            => new SelectorPhrasing(singular, plural, true);

        /// <summary>Maps a target selector to the words it is spoken with.</summary>
        private static SelectorPhrasing PhrasingFor(string selector) => selector switch
        {
            "SELF" => One("this", "these"),
            "TRIGGER_SOURCE" => One("the trigger source", "trigger sources"),
            "ADJACENT" => All("an adjacent minion", "adjacent minions"),
            "ALL_MINIONS" => All("a minion", "minions"),
            "ANY_CHARACTER" => One("a character", "characters"),
            "ANY_ENEMY" => One("an enemy character", "enemy characters"),
            "ANY_FRIENDLY" => One("a friendly character", "friendly characters"),
            "ANY_FRIENDLY_MINION" => One("a friendly minion", "friendly minions"),
            "ANY_MINION" => One("a minion", "minions"),
            "ENEMY_HERO" => One("the enemy hero", "enemy heroes"),
            "ENEMY_MINIONS" => All("an enemy minion", "enemy minions"),
            "FRIENDLY_HERO" => One("your hero", "friendly heroes"),
            "FRIENDLY_MINIONS" => All("a friendly minion", "friendly minions"),
            "OTHER_FRIENDLY_MINIONS" => All("another friendly minion", "other friendly minions"),
            "FRIENDLY_WEAPON" => One("your weapon", "friendly weapons"),
            "ENEMY_WEAPON" => One("the enemy weapon", "enemy weapons"),
            "FRIENDLY_HAND" => All("a card in your hand", "cards in your hand"),
            "ENEMY_HAND" => All("a card in the enemy hand", "cards in the enemy hand"),
            "ANY_HAND_CARD" => One("a hand card", "hand cards"),
            "FRIENDLY_DECK" => All("a card in your deck", "cards in your deck"),
            "ENEMY_DECK" => All("a card in the enemy deck", "cards in the enemy deck"),
            "ANY_DECK_CARD" => One("a deck card", "deck cards"),
            "FRIENDLY_GRAVEYARD" => All("a card in your graveyard", "cards in your graveyard"),
            "ENEMY_GRAVEYARD" => All("a card in the enemy graveyard", "cards in the enemy graveyard"),
            "ANY_GRAVEYARD_CARD" => One("a graveyard card", "graveyard cards"),
            _ => throw Unsupported("target selector", selector),
        };

        /// <summary>Renders how many of a selector's candidates an effect reaches.</summary>
        /// <remarks>
        /// This MUST agree with <c>Targeting.RequiredAutomaticCount</c>, which is what actually
        /// decides how many candidates an automatic spec takes. The non-obvious clause is
        /// <paramref name="random"/>: an unspecified count on a RANDOM target means ONE there, so a
        /// selector that would otherwise default to every candidate narrows to a single pick. Death
        /// Mark and Stomp are exactly that shape, and while the runtime always gave them one random
        /// minion, this printed "all friendly minions" on the card until 2026-08-10. A generator
        /// that disagrees with the resolver is the bug the sibling comment on RequiredPlayerCount
        /// records already having shipped once.
        /// </remarks>
        private static string Quantity(SelectorPhrasing phrasing, int? count, bool random)
        {
            if (count == -1 || (!count.HasValue && phrasing.DefaultsToAll && !random))
                return "all " + phrasing.Plural;
            if (!count.HasValue || count.Value == 1) return phrasing.Singular;
            return count.Value + " " + phrasing.Plural;
        }

        /// <summary>Rewrites a description as its "not the one already chosen" form, for distinctFrom.</summary>
        private static string Another(string description)
        {
            string rest;
            if (TryStripPrefix(description, "a ", out rest)) return "another " + rest;
            if (TryStripPrefix(description, "an ", out rest)) return "another " + rest;
            if (TryStripPrefix(description, "the ", out rest)) return "another " + rest;
            if (TryStripPrefix(description, "all ", out rest)) return "all other " + rest;
            // The possessive has to survive the rewrite the way "all" does — "another your weapon"
            // is what dropping it produces. That the result ("your other weapon") then claims you
            // hold two weapons is a fact about the authored data, not about this phrasing: the
            // "your" selectors name unique things, so a distinctFrom on one excludes nothing.
            //
            // UNDECIDED — issue #143. If you are here because you authored the first distinctFrom
            // on FRIENDLY_WEAPON/FRIENDLY_HERO, this wording was a renderer's guess and no card has
            // ever exercised it. Read #143 and settle it before shipping the card, because the
            // alternative on the table is that CardValidator refuses your target outright.
            if (TryStripPrefix(description, "your ", out rest)) return "your other " + rest;
            if (TryInsertAfterCount(description, "other ", out string counted)) return counted;
            return "another " + description;
        }

        /// <summary>Splices a filter's adjectives into a description, between determiner and noun.</summary>
        private static string Filter(string description, TargetFilterDef filter)
        {
            string modifier = "";
            if (filter.Damaged.HasValue) modifier += filter.Damaged.Value ? "damaged " : "undamaged ";
            if (filter.Tribe.HasValue) modifier += Name(filter.Tribe.Value.ToString()) + " ";
            if (modifier.Length == 0) return description;
            return SpliceAfterDeterminer(description, modifier);
        }

        /// <summary>Marks a description as randomly chosen, between determiner and noun.</summary>
        private static string Randomize(string description)
            => SpliceAfterDeterminer(description, "random ");

        /// <summary>
        /// Every determiner a description can open with, longest first so a shorter one cannot
        /// shadow a longer one it prefixes ("all " over "all other ").
        /// </summary>
        /// <remarks>
        /// This table is the full set of forms <see cref="Quantity"/> and <see cref="Another"/>
        /// can produce, minus the indefinite articles, which are handled separately because they
        /// have to be re-picked rather than carried over. Anything Another learns to emit belongs
        /// here in the same edit — a missing entry does not fail, it silently word-orders the
        /// adjective wrong ("your damaged other weapon").
        /// </remarks>
        private static readonly string[] Determiners =
            { "all other ", "your other ", "another ", "all ", "your ", "the " };

        /// <summary>Inserts an adjective where one belongs: after the determiner, before the noun.</summary>
        private static string SpliceAfterDeterminer(string description, string word)
        {
            // An indefinite article agrees with whatever now comes first, so it is re-picked
            // against the spliced word instead of carried over: "an enemy minion" takes "random "
            // as "a random enemy minion" and "undamaged " as "an undamaged enemy minion".
            string rest;
            if (TryStripPrefix(description, "a ", out rest) ||
                TryStripPrefix(description, "an ", out rest))
                return Article(word) + " " + word + rest;

            foreach (string determiner in Determiners)
                if (TryStripPrefix(description, determiner, out rest))
                    return determiner + word + rest;

            if (TryInsertAfterCount(description, word, out string counted)) return counted;
            return word + description;
        }

        /// <summary>Strips a leading determiner off a description, handing back what follows it.</summary>
        private static bool TryStripPrefix(string description, string prefix, out string rest)
        {
            if (!description.StartsWith(prefix, StringComparison.Ordinal))
            {
                rest = "";
                return false;
            }
            rest = description.Substring(prefix.Length);
            return true;
        }

        /// <summary>Splices a modifier in after a leading count, so "2 minions" becomes "2 other minions".</summary>
        private static bool TryInsertAfterCount(string description, string modifier, out string result)
        {
            int firstSpace = description.IndexOf(' ');
            if (firstSpace > 0 && char.IsDigit(description[0]))
            {
                result = description.Substring(0, firstSpace + 1) + modifier +
                    description.Substring(firstSpace + 1);
                return true;
            }
            result = "";
            return false;
        }

        /// <summary>Renders a mana gain, whose wording turns on how long the crystals last.</summary>
        private static string Mana(GainManaAction action)
        {
            string crystals = action.Amount == 1 ? "Mana Crystal" : "Mana Crystals";
            return action.Kind switch
            {
                ManaGainKind.TEMPORARY => $"Gain {action.Amount} {crystals} this turn only.",
                ManaGainKind.PERMANENT_FILLED => $"Gain {action.Amount} {crystals}.",
                ManaGainKind.PERMANENT_EMPTY => $"Gain {action.Amount} empty {crystals}.",
                _ => throw Unsupported("mana kind", action.Kind),
            };
        }

        /// <summary>Renders a draw count as the cards a player receives.</summary>
        private static string Cards(int count)
        {
            if (count <= 0) throw Requires("a positive card count");
            return count == 1 ? "a card" : $"{count} cards";
        }

        /// <summary>Renders a repeat count as copies of one named card.</summary>
        private static string Copies(int count, string cardName)
        {
            if (count <= 0) throw Requires("a positive copy count");
            return count == 1
                ? Article(cardName) + " " + cardName
                : count + " " + Plural(cardName);
        }

        /// <summary>Pluralises the last word of a card name, by English spelling rules.</summary>
        /// <remarks>
        /// Orthographic, not lexical, and that is forced rather than chosen: this renderer holds
        /// only a card ID (<c>SummonAction.CardId</c>), never the summoned card's definition, so
        /// there is no authored plural to read and no database here to read one from. The classes
        /// below cover every name the card data ships; a genuinely irregular one (Mouse, Child)
        /// would still come out wrong, and fixing THAT means giving this renderer the database.
        /// </remarks>
        private static string Plural(string cardName)
        {
            int start = cardName.LastIndexOf(' ') + 1;   // 0 when the name is a single word
            string head = cardName.Substring(0, start);
            string noun = cardName.Substring(start);

            // A sibilant ending takes "es", because the extra syllable is actually spoken: Ash,
            // Torch, Fox. Tested first — "sh" and "ch" would otherwise fall through to plain "s".
            if (EndsWithAny(noun, "s", "x", "z", "ch", "sh")) return head + noun + "es";
            // Consonant + y takes "ies" (Harpy); vowel + y keeps the y (Ley).
            if (noun.Length > 1 && EndsWithAny(noun, "y") && !IsVowel(noun[noun.Length - 2]))
                return head + noun.Substring(0, noun.Length - 1) + "ies";
            // -f/-fe takes "ves", which is right for this game's vocabulary (Wolf, Elf, Dwarf,
            // Wraithknife) and wrong for the words it does not use (Chief, Roof).
            if (EndsWithAny(noun, "fe")) return head + noun.Substring(0, noun.Length - 2) + "ves";
            if (EndsWithAny(noun, "f")) return head + noun.Substring(0, noun.Length - 1) + "ves";
            return head + noun + "s";
        }

        /// <summary>Picks the indefinite article a phrase reads with, "a" or "an".</summary>
        /// <remarks>
        /// Decided from spelling, since there is no pronunciation data to consult — so the two
        /// lists below are exactly the cases where the first LETTER disagrees with the first SOUND:
        /// an unspoken h ("an Hour") and a vowel spoken as "yoo" or "wuh" ("a Unicorn", "a One-Eyed
        /// Crow"). A leading digit is handled separately because a stat prefix puts one there.
        /// </remarks>
        private static string Article(string phrase)
        {
            if (string.IsNullOrEmpty(phrase)) throw Requires("a non-empty phrase to article");
            if (char.IsDigit(phrase[0])) return LeadingNumberTakesAn(phrase) ? "an" : "a";
            if (StartsWithAny(phrase, "hour", "honest", "honor", "honour", "heir")) return "an";
            if (StartsWithAny(phrase, "uni", "use", "usu", "uti", "eu", "ewe", "one")) return "a";
            return IsVowel(phrase[0]) ? "an" : "a";
        }

        /// <summary>Whether a phrase's leading number is spoken starting with a vowel sound.</summary>
        /// <remarks>
        /// A summoned token carries its stats as a prefix ("Summon a 1/1 Wisp"), so the article has
        /// to agree with a number read aloud. Every number leading with 8 is spoken "eight-" —
        /// 8, 8/8, 80, 800 — and 11 and 18 are the only others whose name opens on a vowel; 110 and
        /// 180 are spoken "one hundred…" and take "a".
        /// </remarks>
        private static bool LeadingNumberTakesAn(string phrase)
        {
            int end = 0;
            while (end < phrase.Length && char.IsDigit(phrase[end])) end++;
            string digits = phrase.Substring(0, end);
            return digits[0] == '8' || digits == "11" || digits == "18";
        }

        /// <summary>Whether a word ends with any of the given suffixes, ignoring case.</summary>
        private static bool EndsWithAny(string word, params string[] suffixes)
        {
            foreach (string suffix in suffixes)
                if (word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether a phrase starts with any of the given prefixes, ignoring case.</summary>
        private static bool StartsWithAny(string phrase, params string[] prefixes)
        {
            foreach (string prefix in prefixes)
                if (phrase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether a letter is a written vowel, which is not the same as a vowel sound.</summary>
        private static bool IsVowel(char c) => "aeiouAEIOU".IndexOf(c) >= 0;

        /// <summary>Renders a stat change with an explicit sign, as a buff line reads it.</summary>
        private static string Delta(int amount)
            => (amount >= 0 ? "+" : "") + amount.ToString(CultureInfo.InvariantCulture);

        /// <summary>Renders a keyword as the name printed on a card face.</summary>
        private static string Name(Keyword keyword)
            => KeywordSemantics.AuthoredName(keyword) ?? Name(keyword.ToString());

        /// <summary>Renders an identifier — card, token, tribe or keyword — as the words a player reads.</summary>
        private static string Name(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) throw Requires("a non-empty identifier");

            string[] parts = identifier.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);

            int start = 0;
            while (start < parts.Length && IsIdNamespaceWord(parts[start])) start++;

            // An attack/health pair (token_wisp_1_1) is the token's stats, not name words —
            // render it as the "1/1 " prefix a real card would carry instead of leaking it as
            // trailing digits. Nothing meaningful follows stats in an id: a word after the pair
            // (token_revenant_5_5_bulwark) is a disambiguator for the id only, never part of the
            // printed name, so the pair also ends the name — not just skips two segments.
            int end = parts.Length;
            string statPrefix = "";
            for (int i = start; i + 1 < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int attack) || !int.TryParse(parts[i + 1], out int health))
                    continue;
                statPrefix = attack + "/" + health + " ";
                end = i;
                break;
            }

            string name = TitleCase(parts, start, end);
            if (name.Length == 0) throw Requires("a readable identifier");
            return statPrefix + name;
        }

        /// <summary>
        /// Whether an id segment is a namespace word a token/hero id is built from
        /// (rekind_token_x for a Rekindling token), never part of the printed name.
        /// </summary>
        private static bool IsIdNamespaceWord(string segment)
            => segment == "token" || segment == "hero" || segment == "rekind";

        /// <summary>Joins the id segments in [start, end) into capitalised, space-separated words.</summary>
        private static string TitleCase(string[] parts, int start, int end)
        {
            var builder = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToUpperInvariant(parts[i][0]));
                if (parts[i].Length > 1) builder.Append(parts[i].Substring(1).ToLowerInvariant());
            }
            return builder.ToString();
        }

        /// <summary>Builds the rejection for an authored value this renderer has no phrasing for.</summary>
        private static NotSupportedException Unsupported(string what, object? value)
            => new NotSupportedException($"Card text generation does not support {what} '{value}'.");

        /// <summary>Builds the rejection for a rule node type this renderer has no phrasing for.</summary>
        private static NotSupportedException UnsupportedNode(string kind, object node)
            => new NotSupportedException(
                $"Card text generation does not support {kind} node '{node.GetType().Name}'.");

        /// <summary>Builds the rejection for input whose shape is renderable but whose value is not.</summary>
        private static NotSupportedException Requires(string requirement)
            => new NotSupportedException($"Card text generation requires {requirement}.");
    }
}
