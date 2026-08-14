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
            var groupActions = new List<RuleAction>();
            string groupPrefix = "";

            foreach (RuleDefinition rule in rules)
            {
                if (rule == null) throw new ArgumentNullException(nameof(rules));

                string prefix = TriggerMarker(rule.Activation) + ConditionPrefix(rule.Condition) +
                    LifetimePrefix(rule.Lifetime);
                if (groupActions.Count > 0 && prefix != groupPrefix)
                {
                    lines.Add(groupPrefix + JoinActions(groupActions));
                    groupActions.Clear();
                }

                groupPrefix = prefix;
                groupActions.Add(rule.Action);
            }

            if (groupActions.Count > 0)
                lines.Add(groupPrefix + JoinActions(groupActions));
            return lines;
        }

        /// <summary>Joins the effects sharing one marker into a single sentence.</summary>
        /// <remarks>
        /// One marker, one sentence (Sam, 2026-08-11). These printed as separate sentences —
        /// "Toll: Deal 3 damage to all enemy minions. Deal 3 damage to the enemy hero." — so the
        /// marker's scope silently ended at the first full stop. That was merely vague until
        /// playing the card became the unmarked default; now an unmarked sentence positively
        /// asserts "on play", so a second Toll clause standing alone reads as a false statement
        /// rather than as ambiguity. The conjunction carries the marker across the whole run.
        ///
        /// <para>Clause-initial words are lowercased EXCEPT the game's own vocabulary, which stays
        /// capitalised mid-sentence the way card games print a keyword — "Deal 2 damage to a minion
        /// and Freeze it." That is also what keeps CardTextStyling's case-sensitive match working on
        /// a keyword that is no longer at the front of a sentence.</para>
        /// </remarks>
        private static string JoinActions(IReadOnlyList<RuleAction> group)
        {
            if (group.Count == 0) throw Requires("at least one action to join");

            IReadOnlyList<(string Text, TargetSpec? Target)> actions = RenderClauses(group);
            if (actions.Count == 1) return actions[0].Text;

            var clauses = new List<string>(actions.Count);
            int runStart = 0;
            for (int i = 0; i < actions.Count; i++)
            {
                string clause = Undot(actions[i].Text);

                if (i > runStart && IsProvablyTheSameTarget(actions[runStart].Target, actions[i].Target))
                {
                    string elided = DropSharedOpening(Undot(actions[runStart].Text), clause);
                    if (elided.Length > 0) clause = elided;
                }
                else if (i > 0)
                {
                    runStart = i;
                }

                clauses.Add(clause);
            }

            var sentence = new StringBuilder();
            for (int i = 0; i < clauses.Count; i++)
            {
                if (i > 0) sentence.Append(i == clauses.Count - 1 ? " and " : ", ");
                sentence.Append(i == 0 ? clauses[i] : Decapitalize(clauses[i]));
            }
            return sentence.Append('.').ToString();
        }

        /// <summary>
        /// Renders a marker's effects to clauses, first folding any run of repeated damage at one
        /// target description into a single clause: three rolls of Chain Lightning are
        /// "Deal 2, 1 and 1 damage each to a random enemy character", not the same sentence typed
        /// three times (Sam, 2026-08-11).
        ///
        /// <para><b>"each" is the load-bearing word</b> and the reason this is a separate mechanism
        /// from the elision below. These targets are NOT the same target — every roll picks
        /// independently and two of them can land on the same character. Naming the target once
        /// without "each" would claim a single character was struck three times, which is the
        /// rules change this phrasing exists to avoid making.</para>
        /// </summary>
        private static IReadOnlyList<(string Text, TargetSpec? Target)> RenderClauses(IReadOnlyList<RuleAction> group)
        {
            var clauses = new List<(string, TargetSpec?)>(group.Count);

            for (int i = 0; i < group.Count; i++)
            {
                // Checked before the repeated-roll scan below, which cannot also match: that one
                // needs the SAME selector rolled randomly, this one needs two DIFFERENT selectors
                // resolved automatically.
                if (TryFoldWholeSide(group, i, out string side))
                {
                    // Target left null for the same reason the spread below does: this clause has
                    // named its own target, and the same-target elision must not reopen it.
                    clauses.Add((side, null));
                    i++;
                    continue;
                }

                int run = i;
                while (run + 1 < group.Count && IsRepeatedRollOfTheSameDamage(group[run], group[run + 1])) run++;

                if (run > i)
                {
                    var amounts = new List<int>(run - i + 1);
                    for (int j = i; j <= run; j++) amounts.Add(((DamageAction)group[j]).Amount);

                    TargetSpec spec = ((DamageAction)group[i]).Target;
                    var spread = new StringBuilder("Deal ");

                    bool uniform = true;
                    for (int j = 1; j < amounts.Count; j++) if (amounts[j] != amounts[0]) uniform = false;

                    if (uniform)
                    {
                        // "Deal 1 damage twice to random enemy characters." The same hit repeated is
                        // a count, not a list — spelling it out as "1 and 1" made a hero power read
                        // twice as long as the thing it does (Sam, 2026-08-11).
                        spread.Append(amounts[0].ToString(CultureInfo.InvariantCulture))
                            .Append(" damage ").Append(Times(amounts.Count))
                            .Append(" to ").Append(RandomPluralTarget(spec)).Append('.');
                    }
                    else
                    {
                        for (int j = 0; j < amounts.Count; j++)
                        {
                            if (j > 0) spread.Append(j == amounts.Count - 1 ? " and " : ", ");
                            spread.Append(amounts[j].ToString(CultureInfo.InvariantCulture));
                        }
                        spread.Append(" damage each to ").Append(Target(spec)).Append('.');
                    }

                    // No target attached: this clause has already named its own, and it must never
                    // be folded again by the same-target elision, which would drop the "each".
                    clauses.Add((spread.ToString(), null));
                    i = run;
                    continue;
                }

                clauses.Add((RenderAction(group[i]),
                    group[i] is TargetedRuleAction targeted ? targeted.Target : null));
            }

            return clauses;
        }

        /// <summary>
        /// Folds "deal N to that side's minions, deal N to that side's hero" into the one thing it
        /// actually says — <c>Deal N damage to all enemies.</c> (Sam, 2026-08-13).
        ///
        /// <para><b>This is a set identity, not a paraphrase.</b> A side's minions and its hero are
        /// disjoint and together exhaust that side's characters, so the union has an exact name and
        /// naming it drops no target and adds none. The old text named the same population twice at
        /// twice the length, on a card face where every line competes with the art.</para>
        ///
        /// <para><b>Why it is honest about ordering.</b> The two rules are two queued effects and
        /// <see cref="Emberfall.Core"/>'s drain does settle deaths between them — but both hits are
        /// enqueued at play time and land before ANY response to either one resolves, because
        /// on-damage triggers and tolls enqueue behind them. That is the same "an AoE volley strikes
        /// simultaneously" semantics DamagePipeline's decision note spells out, so a reader who takes
        /// "all enemies" to mean one volley is reading the resolver correctly.</para>
        ///
        /// <para>Both authored orders fold, because the union does not care which was written first.
        /// A card data model that could express this side as ONE rule would retire the whole
        /// mechanism — <c>TargetChosenBy</c> has no AUTOMATIC member today, so an <c>ANY_ENEMY</c>
        /// spec is a player prompt and two rules are the only way to author the sweep.
        /// RETIREMENT TRIGGER: adding an automatic-choice policy, or an ALL_ENEMIES set selector,
        /// makes this fold dead weight — migrate these rows to the single rule and delete it.</para>
        /// </summary>
        private static bool TryFoldWholeSide(IReadOnlyList<RuleAction> group, int index, out string clause)
        {
            clause = "";
            if (index + 1 >= group.Count) return false;
            if (group[index] is not DamageAction first || group[index + 1] is not DamageAction second)
                return false;
            // Unequal amounts are two different effects that happen to share a side. Nothing about
            // "all enemies" can carry two numbers, and inventing a phrasing that could would be
            // making up a card rather than rendering one.
            if (first.Amount != second.Amount) return false;
            if (!ReachesEveryCandidate(first.Target) || !ReachesEveryCandidate(second.Target)) return false;

            string side = SideNamedByTheUnion(first.Target.Selector, second.Target.Selector);
            if (side.Length == 0) return false;

            clause = $"Deal {first.Amount} damage to {side}.";
            return true;
        }

        /// <summary>The name of the whole side two selectors exhaust between them, in either authored
        /// order, or "" when the pair is not a side.</summary>
        /// <remarks>
        /// Deliberately one entry. The friendly union has no card asking for it, and a phrasing with
        /// no card to print it on is an untested branch that reads as supported — the same fail-closed
        /// posture the rest of this generator takes. Adding the row is the whole cost when one lands.
        /// </remarks>
        private static string SideNamedByTheUnion(string first, string second)
            => IsUnorderedPair(first, second, "ENEMY_MINIONS", "ENEMY_HERO") ? "all enemies" : "";

        private static bool IsUnorderedPair(string first, string second, string one, string other)
            => (string.Equals(first, one, StringComparison.Ordinal) && string.Equals(second, other, StringComparison.Ordinal)) ||
               (string.Equals(first, other, StringComparison.Ordinal) && string.Equals(second, one, StringComparison.Ordinal));

        /// <summary>
        /// Whether a spec takes every candidate its selector enumerates, with nothing narrowing the
        /// set or renaming it. A filter, a count, a binding or a choice policy each mean the effect
        /// reaches less than its selector's population — so the union stops being the whole side and
        /// the fold would be claiming targets the card does not hit.
        /// </summary>
        private static bool ReachesEveryCandidate(TargetSpec spec)
        {
            if (spec.Filter != null) return false;
            if (spec.Binding.Length > 0 || spec.DistinctFrom.Length > 0) return false;
            if (TargetSpec.IsPlayerChosen(spec) || IsRandomlyChosen(spec)) return false;
            return !spec.Count.HasValue || spec.Count.Value == -1;
        }

        /// <summary>Whether two effects are the same damage rolled again at an independently chosen
        /// target — the shape "each" describes, and the exact opposite of
        /// <see cref="IsProvablyTheSameTarget"/>.</summary>
        private static bool IsRepeatedRollOfTheSameDamage(RuleAction first, RuleAction second)
        {
            if (first is not DamageAction a || second is not DamageAction b) return false;

            TargetSpec x = a.Target, y = b.Target;
            // Randomly chosen only. A player-chosen or automatic pair either resolves to the same
            // target (the elision handles it) or is two distinct prompts worth spelling out.
            if (!IsRandomlyChosen(x) || !IsRandomlyChosen(y)) return false;

            // A distinctFrom chained to the previous roll's binding still folds. It marks a hit that
            // cannot repeat the one before it — a real guarantee, and one this phrasing does NOT
            // carry, which is a live wording question rather than a reason to print the sentence
            // twice. Anything else bound or excluded is a shape this has not been asked to render.
            bool chained = y.DistinctFrom.Length > 0 &&
                string.Equals(y.DistinctFrom, x.Binding, StringComparison.Ordinal);
            if (y.DistinctFrom.Length > 0 && !chained) return false;
            if (x.DistinctFrom.Length > 0 && x.Binding.Length == 0) return false;

            return string.Equals(x.Selector, y.Selector, StringComparison.Ordinal) &&
                x.Count == y.Count && SameFilter(x.Filter, y.Filter);
        }

        /// <summary>How many times one repeated hit lands.</summary>
        private static string Times(int count) => count switch
        {
            2 => "twice",
            _ => NumberWord(count) + " times",
        };

        /// <summary>The plural of a randomly chosen target — "random enemy characters".</summary>
        private static string RandomPluralTarget(TargetSpec spec)
        {
            string plural = PhrasingFor(spec.Selector).Plural;
            if (spec.Filter != null) plural = Filter(plural, spec.Filter);
            return IsRandomlyChosen(spec) ? "random " + plural : plural;
        }

        private static string Undot(string sentence) =>
            sentence.EndsWith(".", StringComparison.Ordinal)
                ? sentence.Substring(0, sentence.Length - 1)
                : sentence;

        /// <summary>
        /// Whether two effects are known to land on the SAME target, rather than merely on targets
        /// described the same way. Only two things prove it, and both are read off the data:
        ///
        /// <para>A shared <c>Binding</c> — the authored way of saying "this one, again". It is the
        /// ONLY proof available for a player-chosen target: two unbound ANY_FRIENDLY_MINION specs
        /// are two separate prompts, so "a friendly minion ... a friendly minion" is honest there
        /// and eliding it would invent a constraint the resolver does not enforce.</para>
        ///
        /// <para>Or an automatic selector resolved identically twice — "all friendly minions" is the
        /// same board both times by construction. Randomly chosen targets are excluded exactly
        /// because they are NOT: each roll picks independently, which is what makes a card like
        /// Chain Lightning three separate hits and not one target struck three times.</para>
        /// </summary>
        private static bool IsProvablyTheSameTarget(TargetSpec? first, TargetSpec? second)
        {
            if (first == null || second == null) return false;
            if (first.Binding.Length > 0) return string.Equals(first.Binding, second.Binding, StringComparison.Ordinal);

            if (TargetSpec.IsPlayerChosen(first) || TargetSpec.IsPlayerChosen(second)) return false;
            if (IsRandomlyChosen(first) || IsRandomlyChosen(second)) return false;
            if (second.Binding.Length > 0) return false;
            if (first.DistinctFrom.Length > 0 || second.DistinctFrom.Length > 0) return false;

            return string.Equals(first.Selector, second.Selector, StringComparison.Ordinal) &&
                first.Count == second.Count && SameFilter(first.Filter, second.Filter);
        }

        /// <summary>Compares filters by value — TargetFilterDef is a plain class, so the default
        /// reference comparison would call two identical authored filters different.</summary>
        private static bool SameFilter(TargetFilterDef? first, TargetFilterDef? second)
        {
            if (first == null || second == null) return first == null && second == null;
            return first.Damaged == second.Damaged && first.Tribe == second.Tribe;
        }

        /// <summary>
        /// Strips the opening words a clause shares with the one it is joining, so a target proven
        /// identical is named once: "Give a friendly minion +2/+2" + "Give a friendly minion Bulwark"
        /// becomes "... +2/+2 and Bulwark". Returns "" when there is nothing to save or nothing would
        /// be left — two clauses that reduce to the same words are not a phrasing problem to solve
        /// here, and swallowing one whole would delete an effect from the card.
        /// </summary>
        private static string DropSharedOpening(string first, string second)
        {
            string[] a = first.Split(' ');
            string[] b = second.Split(' ');

            int shared = 0;
            while (shared < a.Length && shared < b.Length &&
                   string.Equals(a[shared], b[shared], StringComparison.Ordinal))
                shared++;

            if (shared == 0 || shared >= b.Length) return "";
            return string.Join(" ", b, shared, b.Length - shared);
        }

        /// <summary>Lowercases a clause's opening word unless it is a word the game capitalises.</summary>
        private static string Decapitalize(string clause)
        {
            int end = clause.IndexOf(' ');
            string first = end < 0 ? clause : clause.Substring(0, end);
            foreach (string word in EmphasisVocabulary())
                if (string.Equals(word, first, StringComparison.Ordinal)) return clause;
            return char.ToLowerInvariant(clause[0]) + clause.Substring(1);
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
            // "to hand" is jargon — a rules-text shorthand that assumes the reader already knows
            // whose hand. Naming the owner works for one minion and for a board of them alike.
            ReturnToHandAction a => $"Return {Target(a.Target)} to their owner's hand.",
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
            // No parentheses — trialling the bare number to see how it reads (Sam, 2026-08-11).
            BackdraftAction a => $"{Name(Keyword.BACKDRAFT)} {a.Amount}.",
            GainArmorAction a => $"Gain {a.Amount} Armor.",
            UnearthAction a => $"{UnearthLabel} {a.Count}.",
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
            if (target.DistinctFrom.Length > 0)
            {
                // Checked HERE and not inside Another(), because only the selector answers it — the
                // description alone cannot. "your weapon" and "enemy hero" are the same mistake and
                // do not look alike as strings, so a prefix test would have caught the possessive
                // and waved the other one through as "another enemy hero" (#143).
                if (RuleActionCapabilities.IsUniqueSelector(target.Selector))
                    throw Unsupported("distinctFrom on the unique selector", target.Selector);
                description = Another(description);
            }
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
            // SETTLED — issue #143, Sam 2026-08-12. There was a "your " branch here emitting
            // "your other weapon", and it is gone along with its determiner. Both readings of a
            // distinctFrom on a unique selector are wrong, not just the ugly one: "your other
            // weapon" and "another enemy hero" each assert a second one exists. The data shape is
            // what was meaningless, so RuleActionCapabilities.IsUniqueSelector rejects it — in
            // CardValidator at authoring, and at the Target() call site above for anything that
            // reaches this renderer without passing validation. Nothing arrives here needing a
            // possessive rewrite any more. See that relation for the dual-wield retirement trigger.
            if (TryInsertAfterCount(description, "other ", out string counted)) return counted;
            return "another " + description;
        }

        /// <summary>Splices a filter's adjectives into a description, between determiner and noun.</summary>
        private static string Filter(string description, TargetFilterDef filter)
        {
            // The tribe REPLACES the noun rather than qualifying it (Sam, 2026-08-11): "all friendly
            // Constructs", not "all friendly Construct minions". Splicing it after the determiner
            // produced "all Construct friendly minions", which is not English — a classifying noun
            // has to sit against the head noun, and once it does it has already said "minion".
            if (filter.Tribe.HasValue)
                description = ReplaceHeadNoun(description, Name(filter.Tribe.Value.ToString()));

            // "damaged" is an ordinary adjective and keeps its place after the determiner.
            if (filter.Damaged.HasValue)
                description = SpliceAfterDeterminer(description, filter.Damaged.Value ? "damaged " : "undamaged ");

            return description;
        }

        /// <summary>Swaps a minion phrase's head noun for a tribe, keeping its number.</summary>
        /// <remarks>
        /// Falls back to qualifying the phrase when it does not end in the word this replaces — a
        /// tribe filter is only meaningful on minions, so anything else is a shape this has never
        /// been asked to render and must not silently mangle.
        /// </remarks>
        private static string ReplaceHeadNoun(string description, string tribe)
        {
            if (description.EndsWith(" minions", StringComparison.Ordinal))
                return description.Substring(0, description.Length - " minions".Length) + " " + Plural(tribe);
            if (description.EndsWith(" minion", StringComparison.Ordinal))
                return ReArticle(description.Substring(0, description.Length - " minion".Length) + " " + tribe);
            if (string.Equals(description, "minions", StringComparison.Ordinal)) return Plural(tribe);
            if (string.Equals(description, "minion", StringComparison.Ordinal)) return tribe;
            return SpliceAfterDeterminer(description, tribe + " ");
        }

        /// <summary>Re-picks an indefinite article against whatever the phrase now starts with.</summary>
        /// <remarks>
        /// Dropping the head noun can leave the article agreeing with a word that is gone: "a minion"
        /// filtered to ELEMENTAL became "a Elemental", because nothing re-read the vowel. Only the
        /// indefinite article moves — "all" and "the" agree with nothing.
        /// </remarks>
        private static string ReArticle(string description)
        {
            string rest;
            if (TryStripPrefix(description, "a ", out rest) ||
                TryStripPrefix(description, "an ", out rest))
                return Article(rest) + " " + rest;
            return description;
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
        // "your other " was here until #143 deleted the branch that produced it. "all other " is now
        // the only two-word entry, and it is what keeps the longest-first ordering load-bearing.
        private static readonly string[] Determiners =
            { "all other ", "another ", "all ", "your ", "the " };

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
            if (count == 1) return Article(cardName) + " " + cardName;

            // A token's name carries its stats ("1/1 Wisp"), so a numeral count runs straight into
            // them: "Summon 2 1/1 Wisps." Spelled out only when the collision is real, which keeps
            // "Draw 2 cards." reading the way the genre writes it. Sam, 2026-08-11.
            string quantity = char.IsDigit(cardName[0]) ? NumberWord(count) : count.ToString(CultureInfo.InvariantCulture);
            return quantity + " " + Plural(cardName);
        }

        /// <summary>Spells a small count out; anything larger stays a numeral, where it reads better.</summary>
        private static string NumberWord(int count) => count switch
        {
            2 => "two",
            3 => "three",
            4 => "four",
            5 => "five",
            6 => "six",
            7 => "seven",
            8 => "eight",
            9 => "nine",
            10 => "ten",
            _ => count.ToString(CultureInfo.InvariantCulture),
        };

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
