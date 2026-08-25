using System;
using System.Collections.Generic;
using System.Globalization;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// Derives a card's rules FROM its text. The inverse of <see cref="CardTextGenerator"/>.
    ///
    /// <para><b>Sam ruled on 2026-08-20 that rules are not authored</b> — "any form of rules that are
    /// required are derived from card text by the engine or a pre-processor". This is the beginning
    /// of that. It is deliberately partial: it covers a subset of sentence frames and REFUSES
    /// everything else, reporting why. Coverage is a number <c>CardTextParserCoverage</c> prints, not
    /// a claim made here.</para>
    ///
    /// <para><b>Refusal is the design, not a limitation.</b> A parser that guesses produces a card
    /// which quietly does something other than what it says, and that is strictly worse than no
    /// parser. Every uncertain case returns false with a reason, so the failure mode is a card the
    /// pipeline declines to derive rather than a card that misbehaves in a match.</para>
    ///
    /// <para><b>It inverts the renderer by generate-and-test, never by a second grammar.</b> To read
    /// a noun phrase it enumerates the possible target specs, renders each through
    /// <see cref="CardTextGenerator.DescribeTarget"/>, and keeps the one whose rendering equals the
    /// phrase. A hand-written inverse table would be the same fact stated twice with nothing
    /// checking agreement — the bug class this whole effect-model effort exists to delete — whereas
    /// generate-and-test cannot disagree with the renderer because it calls it.</para>
    /// </summary>
    public static class CardTextParser
    {
        /// <summary>
        /// Reads a card's rules out of its text, or explains what stopped it.
        /// </summary>
        /// <remarks>
        /// <paramref name="card"/> supplies the text, the card TYPE — which is what says whether an
        /// unmarked clause is a spell resolving or a minion arriving — and the carrier keywords,
        /// which are a separate authored field and are skipped rather than parsed.
        /// </remarks>
        public static bool TryParse(CardDefinition card, out IReadOnlyList<RuleDefinition> rules,
            out string reason)
            => TryParse(card, CardNameIndex.Empty, out rules, out reason);

        /// <summary>
        /// Reads a card's rules out of its text against a known card pool, or explains what stopped
        /// it.
        /// </summary>
        /// <remarks>
        /// <paramref name="names"/> is the pool a sentence may name — the ONE thing a card's own row
        /// cannot supply. Everything else a sentence mentions comes from a closed vocabulary this
        /// class enumerates on the spot; a card id does not, so "Summon a 1/1 Wisp." is underivable
        /// without it. Passing <see cref="CardNameIndex.Empty"/> is legitimate and simply keeps every
        /// card-naming sentence refused.
        /// </remarks>
        public static bool TryParse(CardDefinition card, CardNameIndex names,
            out IReadOnlyList<RuleDefinition> rules, out string reason)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (names == null) throw new ArgumentNullException(nameof(names));
            rules = Array.Empty<RuleDefinition>();

            if (!TryStripCarrierBand(card, out string body, out reason)) return false;
            if (body.Length == 0) { reason = "no rules text"; return true; }

            var parsed = new List<RuleDefinition>();
            foreach (string line in Sentences(body))
            {
                if (!TryTrigger(card.Type, line, out Trigger trigger, out string clause, out reason))
                    return false;

                // A cost prefixes the run it buys, so it is peeled off before the run is read — the
                // same order the renderer built them in. Absent one, `clause` is unchanged and this
                // is the path every card took before costs existed.
                RuleAction? cost = TryCostPrefix(ref clause, names);
                // Then the condition, which the renderer prints AFTER the cost clause and before the
                // effects. The two never co-occur — a rule carries one condition, and a cost scope
                // only renders for COST_PAID — so finding one settles the condition outright.
                RuleCondition? qualifier = TryConditionPrefix(ref clause);
                if (cost != null)
                    parsed.Add(new RuleDefinition(
                        new RuleId("rule_" + (parsed.Count + 1).ToString(CultureInfo.InvariantCulture)),
                        new TriggeredActivation(trigger),
                        AlwaysCondition.Instance,
                        LifetimeFor(trigger),
                        cost));

                if (!TryActions(clause, names, out IReadOnlyList<RuleAction> actions, out reason))
                    return false;

                // One sentence can be several rules: the renderer joins everything under one marker
                // into a single sentence, so the conjunction is a rule boundary rather than a rule.
                RuleCondition condition = qualifier
                    ?? (cost == null ? (RuleCondition)AlwaysCondition.Instance : CostPaidCondition.Instance);
                foreach (RuleAction action in actions)
                    parsed.Add(new RuleDefinition(
                        new RuleId("rule_" + (parsed.Count + 1).ToString(CultureInfo.InvariantCulture)),
                        new TriggeredActivation(trigger),
                        condition,
                        LifetimeFor(trigger),
                        action));
            }

            rules = parsed;
            reason = "";
            return true;
        }

        /// <summary>
        /// The lifetime a trigger implies. Measured across all 223 shipped rules on 2026-08-20: the
        /// trigger determines the lifetime for every one of the four triggers in use, with no
        /// exceptions, and <c>TestRules</c> had already encoded the same derivation by hand.
        ///
        /// <para><b>This is narrower than the engine.</b> FINITE and PERMANENT exist and no shipped
        /// card uses them, so a card wanting one has no spelling yet and this returns the wrong
        /// answer rather than refusing. That is acceptable only because the round-trip gate re-renders
        /// what it built: a lifetime that changes the sentence is caught, and one that does not
        /// change the sentence is a genuine gap recorded here rather than a silent guess.</para>
        /// </summary>
        private static RuleLifetime LifetimeFor(Trigger trigger)
            => trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                ? (RuleLifetime)OneShotLifetime.Instance
                : SourceBoundLifetime.Instance;

        /// <summary>
        /// Removes the leading carrier-keyword band, which is authored state on its own field and
        /// not something to read back out of prose. Rebuilt through the renderer rather than
        /// pattern-matched, so a change to how the band is printed moves both halves at once.
        /// </summary>
        private static bool TryStripCarrierBand(CardDefinition card, out string body, out string reason)
        {
            body = (card.Text ?? "").Trim();
            reason = "";

            string band = CardTextGenerator.CarrierBand(card);
            if (band.Length == 0) return true;

            if (!body.StartsWith(band, StringComparison.Ordinal))
            {
                reason = $"carrier band '{band}' is not how the text opens";
                return false;
            }
            body = body.Substring(band.Length).Trim();
            return true;
        }

        /// <summary>
        /// Splits the rules text into one string per rendered group line.
        /// </summary>
        /// <remarks>
        /// A full stop followed by a space ends a group, which is exactly how <c>RenderRuleGroups</c>
        /// joins them. It is approximate — a sentence could contain such a stop — and that is
        /// tolerable ONLY because a bad split cannot survive the round trip: the re-rendered card
        /// will not equal the original, so the card is refused rather than mis-derived.
        /// </remarks>
        private static IEnumerable<string> Sentences(string body)
        {
            int start = 0;
            for (int i = 0; i + 1 < body.Length; i++)
            {
                if (body[i] != '.' || body[i + 1] != ' ') continue;
                yield return body.Substring(start, i + 1 - start).Trim();
                start = i + 2;
            }
            if (start < body.Length) yield return body.Substring(start).Trim();
        }

        /// <summary>
        /// Reads the trigger off a clause's marker, and the card type when there is none.
        ///
        /// <para><b>Every marker the renderer prints is read back, not a chosen few.</b> The labels
        /// are enumerated out of the enum and rendered through <see cref="CardTextGenerator.TriggerLabel"/>,
        /// so a trigger becomes readable in the same change that makes it printable. Adding
        /// ON_TURN_START and ON_HEAL cards to the corpus is what showed the hand-listed pair had
        /// silently become a second, shorter list of triggers.</para>
        /// </summary>
        /// <remarks>
        /// Measured 2026-08-20: ZERO shipped rules need a marker their text does not carry. An
        /// unmarked clause means the card was played, which for a spell or a hero power the engine
        /// currently calls ON_CAST and for a minion or weapon ON_PLAY — a distinction nothing in the
        /// engine acts on, and one that is noted for retirement. Until it retires, the card type
        /// picks between them, which is precisely what <c>CommandProcessor</c> already does at the
        /// raise site.
        /// </remarks>
        private static bool TryTrigger(CardType type, string line, out Trigger trigger,
            out string clause, out string reason)
        {
            reason = "";
            clause = line;
            foreach (Trigger candidate in (Trigger[])Enum.GetValues(typeof(Trigger)))
            {
                string label;
                // AURA has no timing label and TriggerLabel throws rather than invent one, which is
                // the renderer refusing to print an activation that cannot be triggered. Enumerating
                // the enum means meeting it here, and skipping it is the same refusal read back.
                try { label = CardTextGenerator.TriggerLabel(candidate); }
                catch (Exception) { continue; }
                if (label.Length == 0) continue;

                string marker = label + ": ";
                if (!line.StartsWith(marker, StringComparison.Ordinal)) continue;
                clause = line.Substring(marker.Length).Trim();
                trigger = candidate;
                return true;
            }

            trigger = type == CardType.SPELL || type == CardType.HERO_POWER
                ? Trigger.ON_CAST
                : Trigger.ON_PLAY;
            return true;
        }

        /// <summary>
        /// Peels a cost prefix off the front of a clause, returning the cost's action and leaving
        /// <paramref name="clause"/> as the run it buys — or returning null and leaving the clause
        /// untouched.
        /// </summary>
        /// <remarks>
        /// <para><b>The colon is the whole signal.</b> A clause and a colon is the cost's entire
        /// grammar, exactly as it is <c>Toll:</c>'s, and a colon cannot appear mid-sentence — so
        /// splitting on the first one is not a heuristic. The trigger marker has already been taken
        /// off by the time this runs, so any colon left belongs to a cost.</para>
        ///
        /// <para><b>Failure is silent and falls through on purpose.</b> A line whose head does not
        /// parse as an action is simply not a cost prefix — an "Aura: " line reaches here and leaves
        /// unchanged, then refuses further down for the reason it always did. Returning a refusal
        /// here would rename every one of those refusals after a feature they have nothing to do
        /// with.</para>
        ///
        /// <para><b>Generate-and-test, like everything else here.</b> The head is not read; a cost
        /// action is proposed at every target spec and kept only when
        /// <see cref="CardTextGenerator.DescribeCostPrefix"/> prints back the exact head that was
        /// split off, "You may" and all. So the optional spelling cannot be mistaken for the
        /// mandatory one — they differ in the rendering, which is the only thing compared.</para>
        /// </remarks>
        private static RuleAction? TryCostPrefix(ref string clause, CardNameIndex names)
        {
            int colon = clause.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) return null;

            string head = clause.Substring(0, colon);
            string rest = clause.Substring(colon + 2).Trim();
            if (rest.Length == 0) return null;

            foreach (CostKind kind in new[] { CostKind.Required, CostKind.Optional })
            {
                string standalone = Standalone(kind == CostKind.Optional
                    ? StripOptionalOpening(head)
                    : head);
                foreach (RuleAction candidate in Candidates(standalone, names, kind))
                {
                    if (!(candidate is TargetedRuleAction)) continue;
                    string? printed;
                    try { printed = CardTextGenerator.DescribeCostPrefix(candidate, names); }
                    catch (Exception) { continue; }
                    if (!string.Equals(printed, head + ": ", StringComparison.Ordinal)) continue;

                    clause = rest;
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// Peels a condition clause off the front of a clause, returning the condition and leaving
        /// <paramref name="clause"/> as the effect it qualifies — or returning null and leaving the
        /// clause untouched.
        /// </summary>
        /// <remarks>
        /// <para><b>Generate-and-test over a closed vocabulary, like everything else here.</b> The
        /// condition space that PRINTS anything is one enum, so every value is proposed, rendered
        /// through <see cref="CardTextGenerator.DescribeConditionPrefix"/>, and kept only when its
        /// rendering is literally how the clause opens. Nothing reads the English.</para>
        ///
        /// <para><b>The recapitalisation is the inverse of the renderer's comma rule.</b> A clause
        /// after a comma-terminated prefix was lowercased on the way out, so it is raised on the way
        /// back in — a no-op on the game vocabulary the renderer spares, because raising a capital
        /// changes nothing. A wrong guess here cannot survive: the run trip re-renders and the card
        /// is refused rather than mis-derived.</para>
        /// </remarks>
        private static RuleCondition? TryConditionPrefix(ref string clause)
        {
            foreach (RuleCondition candidate in Conditions())
            {
                string printed;
                try { printed = CardTextGenerator.DescribeConditionPrefix(candidate); }
                catch (Exception) { continue; }
                if (printed.Length == 0) continue;
                if (!clause.StartsWith(printed, StringComparison.Ordinal)) continue;

                clause = Capitalized(clause.Substring(printed.Length).Trim());
                return candidate;
            }
            return null;
        }

        /// <summary>
        /// Every condition worth proposing — the ones that print a clause of their own. ALWAYS and
        /// COST_PAID render nothing and so cannot be read out of a sentence; ALWAYS is the absence
        /// this returns to, and COST_PAID is settled by the cost prefix instead.
        /// </summary>
        private static IEnumerable<RuleCondition> Conditions()
        {
            foreach (TurnOwner owner in (TurnOwner[])Enum.GetValues(typeof(TurnOwner)))
                yield return new TurnOwnerCondition(owner);

            // Both determiners are proposed, because the flag is not derivable from a sentence:
            // "another Construct" and "a Construct" are two different conditions, and the one the
            // card carries is the one whose clause it printed.
            foreach (Tribe tribe in (Tribe[])Enum.GetValues(typeof(Tribe)))
            {
                if (tribe == Tribe.NONE) continue;
                yield return new ControlsCondition(tribe, excludesSource: true);
                yield return new ControlsCondition(tribe, excludesSource: false);
            }
        }

        /// <summary>Drops the words that mark a cost declinable, so the clause beneath can be
        /// proposed as the ordinary action it otherwise is.</summary>
        private static string StripOptionalOpening(string head)
            => head.StartsWith(OptionalOpening, StringComparison.Ordinal)
                ? head.Substring(OptionalOpening.Length)
                : head;

        private const string OptionalOpening = "You may ";

        /// <summary>
        /// Reads one sentence into the actions it prints as, by PROPOSING lists of typed actions and
        /// asking the renderer which list would print this sentence.
        ///
        /// <para><b>Nothing here interprets a sentence.</b> The verb narrows what is worth proposing
        /// and that is all it does; a list becomes a parse only when
        /// <see cref="CardTextGenerator.DescribeActions"/> renders it back byte for byte. A frame
        /// written carelessly therefore produces a REFUSAL rather than a wrong rule, because a
        /// misread candidate simply fails to re-render. Adding a verb is adding candidates, never
        /// adding a reading.</para>
        ///
        /// <para><b>Why the conjunction needs no theory of conjunctions.</b> The renderer joins a
        /// marker's actions into one sentence, decapitalising each and dropping the subject of any
        /// clause that shares the previous one's target. Inverting that by hand would mean restating
        /// three rules — the separator, the case, the elision — and hoping they stay in step. Instead
        /// this splits on the separators the renderer uses, proposes an action per piece, and hands
        /// the whole list back to the renderer. If the join was read wrongly the sentence does not
        /// come back, so a wrong reading cannot survive.</para>
        ///
        /// <para><b>The elision is the grouping.</b> A shared pick is exactly what makes the renderer
        /// drop a subject, so "…and Bulwark" says the second action reuses the first's target and
        /// "…and give a friendly minion +1/+1" says it does not. That is why the pieces are tried
        /// against the run's target as well as on their own, and why the shared binding is derived
        /// here rather than read: the sentence carries the PARTITION, and a binding name is only ever
        /// a label for it.</para>
        /// </summary>
        private static bool TryActions(string sentence, CardNameIndex names,
            out IReadOnlyList<RuleAction> actions, out string reason)
        {
            actions = Array.Empty<RuleAction>();

            foreach (List<RuleAction> run in FoldedDamage(sentence))
            {
                string folded;
                try { folded = CardTextGenerator.DescribeActions(run, names); }
                catch (Exception) { continue; }
                if (!string.Equals(folded, sentence, StringComparison.Ordinal)) continue;

                actions = run;
                reason = "";
                return true;
            }

            IReadOnlyList<string> pieces = Pieces(sentence);

            foreach (List<RuleAction> group in Groups(pieces, names, 0, null, new List<RuleAction>()))
            {
                string rendered;
                // A list the renderer refuses to print is simply not this sentence. Enumerating
                // freely and letting it throw is cheaper than teaching this side which combinations
                // are legal, and it cannot go wrong: an exception ends that candidate, never the parse.
                try { rendered = CardTextGenerator.DescribeActions(group, names); }
                catch (Exception) { continue; }
                if (!string.Equals(rendered, sentence, StringComparison.Ordinal)) continue;

                actions = group;
                reason = "";
                return true;
            }

            // Two refusals, held apart because they are different work. An unrecognised verb wants a
            // new generator in Candidates; a recognised verb whose sentence nothing reproduces wants
            // a wider target vocabulary — a filter or a distinctFrom, neither of which is enumerated.
            reason = (Any(Candidates(Standalone(pieces[0]), names))
                ? "no target spec renders in "
                : "no frame matches ") + Quoted(sentence);
            return false;
        }

        /// <summary>
        /// Runs of repeated damage a sentence could be. <c>RenderClauses</c> collapses several rolls
        /// at one target description into a single clause — "Deal 1 damage twice to random enemies."
        /// — so this is the one place a piece produces SEVERAL actions, and it is tried ahead of the
        /// piece machinery because the fold's own commas and "and" would otherwise be split.
        ///
        /// <para><b>Every roll is proposed bare, and that is now the whole meaning of the
        /// sentence.</b> Sam ruled on 2026-08-21 that a random pick may land on the same character
        /// twice — <c>distinctFrom</c> came off <c>hero_power_chain_spark</c> in the same change and
        /// is no longer used by any row in the corpus.</para>
        ///
        /// <para><b>Until that ruling this had to refuse, and the reason is worth keeping.</b> A fold
        /// prints the amounts and the FIRST roll's target and nothing else, so a CHAINED run — each
        /// roll carrying a <c>distinctFrom</c> naming the previous roll's binding, guaranteeing the
        /// hits land on different characters — folds into the byte-identical sentence. The renderer
        /// says as much itself: that guarantee is "one this phrasing does NOT carry". Two readings
        /// differing in real play and agreeing in every character is exactly what the round trip
        /// cannot catch.</para>
        ///
        /// <para><b>So the chained form has no spelling, and must not be authored until it does.</b>
        /// Sam's lean is the word "different" — "Deal 2 damage and 1 damage twice, each to a
        /// different random enemy" — but that is unruled, and nothing renders it. A chained run
        /// authored today would derive back as this unchained one, silently, because its chain never
        /// reaches the page. RETIREMENT TRIGGER: teaching the renderer to say "different" is what
        /// makes the chain expressible; this paragraph and the guard it asks for retire together.</para>
        ///
        /// <para><b>Sam refined what the chain has to mean, 2026-08-22, and it is NOT
        /// <c>distinctFrom</c> over the whole run.</b> A chain forbids a CONSECUTIVE repeat only:
        /// <i>"if it hits one target, it's gotta hit a different target on the next strike. The
        /// third target could be the first target again."</i> So the constraint is between adjacent
        /// rolls, not global — <c>distinctFrom</c> naming the previous roll's binding says exactly
        /// that, and a run-wide exclusion would say something else and be wrong. Random staying
        /// repeatable in general is unchanged and still correct; a chain is one card's extra
        /// promise, not a property of rolling. He expects this shape to need real cooperation
        /// between the authored text and possibly an engine helper, and ruled that nothing changes
        /// until then: the card ships as what it already says and does.</para>
        /// </summary>
        private static IEnumerable<List<RuleAction>> FoldedDamage(string sentence)
        {
            // Every fold is a damage run, so nothing else is worth probing — and this runs ahead of
            // the whole parse, where the spec enumeration is not cheap.
            if (!sentence.StartsWith("Deal ", StringComparison.Ordinal)) yield break;

            foreach (TargetSpec spec in Specs())
            {
                if (spec.Binding.Length > 0) continue;
                foreach (List<int> amounts in FoldRuns(sentence))
                {
                    var run = new List<RuleAction>(amounts.Count);
                    foreach (int amount in amounts) run.Add(new DamageAction(amount, spec));
                    yield return run;
                }
            }
        }

        /// <summary>
        /// The runs of amounts a fold might hold: the sentence's own numbers in order for the spread
        /// form ("Deal 2, 1 and 1 damage each…"), and each number repeated for the uniform form
        /// ("Deal 1 damage twice…"), whose count is a WORD and so is not in the sentence at all.
        /// </summary>
        private static IEnumerable<List<int>> FoldRuns(string sentence)
        {
            List<int> written = InOrder(sentence);
            if (written.Count > 1) yield return written;

            foreach (int amount in Amounts(sentence))
                for (int length = 2; length <= 4; length++)
                {
                    var uniform = new List<int>(length);
                    for (int i = 0; i < length; i++) uniform.Add(amount);
                    yield return uniform;
                }
        }

        /// <summary>Every number in a sentence, in order and keeping repeats — which is what
        /// separates a spread of rolls from <see cref="Amounts"/>' set of distinct values.</summary>
        private static List<int> InOrder(string sentence)
        {
            var written = new List<int>();
            for (int i = 0; i < sentence.Length; i++)
            {
                if (!char.IsDigit(sentence[i])) continue;
                int end = i;
                while (end < sentence.Length && char.IsDigit(sentence[end])) end++;
                if (int.TryParse(sentence.Substring(i, end - i), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int value))
                    written.Add(value);
                i = end;
            }
            return written;
        }

        /// <summary>
        /// Every list of actions worth proposing for a sentence, one piece at a time.
        ///
        /// <para><c>runStart</c> is the action a shared subject would have been elided against — the
        /// start of the current run, not the previous piece, because that is what the renderer
        /// compares to. A piece read on its own begins a new run; a piece read against the run's
        /// target continues it.</para>
        /// </summary>
        private static IEnumerable<List<RuleAction>> Groups(IReadOnlyList<string> pieces,
            CardNameIndex names, int index, RuleAction? runStart, List<RuleAction> soFar)
        {
            if (index == pieces.Count)
            {
                yield return new List<RuleAction>(soFar);
                yield break;
            }

            foreach (KeyValuePair<RuleAction, bool> candidate in
                     PieceCandidates(pieces[index], names, index == 0 ? null : runStart))
            {
                soFar.Add(candidate.Key);
                RuleAction next = candidate.Value ? runStart! : candidate.Key;
                foreach (List<RuleAction> group in Groups(pieces, names, index + 1, next, soFar))
                    yield return group;
                soFar.RemoveAt(soFar.Count - 1);
            }
        }

        /// <summary>
        /// The actions one piece could be, paired with whether it CONTINUES the run rather than
        /// starting one.
        ///
        /// <para>Both readings are offered because the sentence alone does not always settle it, and
        /// both are filtered by rendering: a piece read on its own must print as itself, and a piece
        /// read against the run's target must print as a sentence ENDING in it — which is what a
        /// dropped subject leaves behind.</para>
        /// </summary>
        private static IEnumerable<KeyValuePair<RuleAction, bool>> PieceCandidates(string piece,
            CardNameIndex names, RuleAction? runStart)
        {
            string standalone = Standalone(piece);
            foreach (RuleAction candidate in Candidates(standalone, names))
                if (string.Equals(Rendered(candidate, names), standalone, StringComparison.Ordinal))
                    yield return new KeyValuePair<RuleAction, bool>(candidate, false);

            if (!(runStart is TargetedRuleAction targeted)) yield break;

            string tail = " " + piece + ".";
            foreach (RuleAction candidate in Continuations(piece, targeted.Target))
            {
                string? rendered = Rendered(candidate, names);
                if (rendered != null && rendered.EndsWith(tail, StringComparison.Ordinal))
                    yield return new KeyValuePair<RuleAction, bool>(candidate, true);
            }
        }

        /// <summary>
        /// Every action shape at an already-chosen target. No verb gate: a continued piece has had
        /// its opening words dropped, so the verb is exactly what is missing from it.
        /// </summary>
        private static IEnumerable<RuleAction> Continuations(string piece, TargetSpec target)
        {
            foreach (int amount in Amounts(piece))
            {
                yield return new DamageAction(amount, target);
                yield return new HealAction(amount, target);
            }
            foreach (KeyValuePair<int, int> delta in Deltas(piece))
                yield return new ModifyStatsAction(delta.Key, delta.Value, target);
            foreach (Keyword keyword in GrantableKeywords())
                yield return new GrantKeywordAction(keyword, target);

            yield return new DestroyAction(target);
            yield return new ReturnToHandAction(target);
            yield return new TakeControlAction(target);
            yield return new FrostAction(target);
            yield return new SilenceAction(target);
        }

        /// <summary>
        /// Splits a sentence on the separators <c>JoinActions</c> joins with — commas between the
        /// clauses and "and" before the last.
        ///
        /// <para>Approximate on purpose, exactly as the sentence split is: a comma or an "and" inside
        /// one clause splits wrongly here, and a wrong split cannot survive the re-render, so the
        /// card is refused rather than mis-derived.</para>
        /// </summary>
        private static IReadOnlyList<string> Pieces(string sentence)
        {
            string body = sentence.TrimEnd('.');
            int at = body.LastIndexOf(" and ", StringComparison.Ordinal);
            if (at < 0) return new[] { body };

            var pieces = new List<string>(body.Substring(0, at).Split(new[] { ", " }, StringSplitOptions.None));
            pieces.Add(body.Substring(at + " and ".Length));
            return pieces;
        }

        /// <summary>A piece as the whole sentence it would be on its own — the join decapitalises
        /// every clause but the first and drops the stop from all of them.</summary>
        private static string Standalone(string piece) => Capitalized(piece) + ".";

        /// <summary>Raises a clause's first letter — the inverse of the renderer's decapitalisation,
        /// and a no-op on the vocabulary it spares.</summary>
        private static string Capitalized(string clause)
            => clause.Length > 0 && !char.IsUpper(clause[0])
                ? char.ToUpperInvariant(clause[0]) + clause.Substring(1)
                : clause;

        private static string? Rendered(RuleAction action, CardNameIndex names)
        {
            try { return CardTextGenerator.DescribeAction(action, names); }
            catch (Exception) { return null; }
        }

        private static bool Any(IEnumerable<RuleAction> candidates)
        {
            foreach (RuleAction _ in candidates) return true;
            return false;
        }

        private static string Quoted(string clause) => '"' + clause + '"';

        /// <summary>
        /// Every typed action worth proposing for a clause.
        ///
        /// <para><b>Over-generating is free and under-generating is the only failure.</b> A wrong
        /// candidate cannot survive the re-render in <see cref="TryAction"/>, so each verb yields
        /// every shape it could possibly be rather than deciding which one it is. That is what lets
        /// the elided target fall out for nothing: "Deal 3 damage." names no target, and the spec
        /// whose rendering omits the phrase is simply the one that matches.</para>
        ///
        /// <para><b>What is deliberately absent:</b> the four actions carrying a card id — SUMMON,
        /// ADD_TO_HAND, EQUIP, TRANSFORM. Generate-and-test needs a candidate set, and a card id has
        /// no vocabulary to draw from here: this class is handed one <c>CardDefinition</c> and never
        /// a database. They stay refused rather than guessed until an id source is decided on.</para>
        /// </summary>
        private static IEnumerable<RuleAction> Candidates(string clause, CardNameIndex names,
            CostKind cost = CostKind.None)
        {
            bool Verb(string head) => clause.StartsWith(head, StringComparison.Ordinal);

            string frost = CardTextGenerator.DescribeKeyword(Keyword.FROST) + " ";
            string silence = CardTextGenerator.DescribeKeyword(Keyword.SILENCE) + " ";
            string backdraft = CardTextGenerator.DescribeKeyword(Keyword.BACKDRAFT) + " ";
            string unearth = CardTextGenerator.UnearthLabel + " ";

            foreach (TargetSpec spec in Specs(cost))
            {
                foreach (int amount in Amounts(clause))
                {
                    if (Verb("Deal ")) yield return new DamageAction(amount, spec);
                    if (Verb("Restore ")) yield return new HealAction(amount, spec);
                }

                if (Verb("Give "))
                {
                    foreach (KeyValuePair<int, int> delta in Deltas(clause))
                        yield return new ModifyStatsAction(delta.Key, delta.Value, spec);
                    foreach (Keyword keyword in GrantableKeywords())
                        yield return new GrantKeywordAction(keyword, spec);
                }

                // "Gain +1/+1." is the same action with the target elided, because a minion buffing
                // itself gains (Sam, 2026-08-25). Proposed against EVERY spec exactly like the two
                // destroy verbs above rather than against SELF here — only the self spec re-renders
                // to this sentence, so the round trip settles it and this stays free of a second
                // opinion about which selector prints which verb. "Gain 2 Armor." reaches the same
                // head and proposes nothing, because Deltas finds no +x/+y in it.
                if (Verb("Gain "))
                {
                    foreach (KeyValuePair<int, int> delta in Deltas(clause))
                        yield return new ModifyStatsAction(delta.Key, delta.Value, spec);
                }

                // Two words, one action — CardTextGenerator.DestroyVerb picks between them off the
                // target's side, so both are proposed and the re-render settles which was printed.
                if (Verb("Destroy ") || Verb("Sacrifice ")) yield return new DestroyAction(spec);
                if (Verb("Return ")) yield return new ReturnToHandAction(spec);
                if (Verb("Take control of ")) yield return new TakeControlAction(spec);
                if (Verb(frost)) yield return new FrostAction(spec);
                if (Verb(silence)) yield return new SilenceAction(spec);
            }

            foreach (int amount in Amounts(clause))
            {
                if (Verb("Draw ")) yield return new DrawAction(amount);
                if (Verb("Gain "))
                {
                    yield return new GainArmorAction(amount);
                    foreach (ManaGainKind kind in (ManaGainKind[])Enum.GetValues(typeof(ManaGainKind)))
                        yield return new GainManaAction(amount, kind);
                }
                if (Verb(backdraft)) yield return new BackdraftAction(amount);
                if (Verb(unearth)) yield return new UnearthAction(amount);
            }

            // The verbs that name another card. Nothing here reads the noun phrase — the count and
            // the article and the plural are all the renderer's business, so every id in the pool is
            // proposed at every plausible count and the one that prints this sentence wins. That is
            // also why an id whose printed name collides with another's cannot be picked wrongly:
            // both are proposed, both render identically, and identical renderings are not a match
            // for one another -- the first simply wins, which is the collision CardNameIndex reports
            // rather than hides.
            if (!Verb("Summon ") && !Verb("Add ") && !Verb("Equip ") && !Verb("Transform ")) yield break;

            foreach (int cardId in names.AllIds())
            {
                if (Verb("Equip ")) yield return new EquipAction(cardId);

                foreach (int amount in CopyCounts(clause))
                {
                    if (Verb("Summon ")) yield return new SummonAction(cardId, amount);
                    if (Verb("Add ")) yield return new AddToHandAction(cardId, amount);
                }

                if (!Verb("Transform ")) continue;
                foreach (TargetSpec spec in Specs()) yield return new TransformAction(cardId, spec);
            }
        }

        /// <summary>
        /// Keywords a card can GRANT. Action markers are excluded because their behaviour comes from
        /// a typed action and granting them is rejected by <c>CardValidator</c> — proposing one here
        /// could only ever produce a rule the validator then refuses.
        /// </summary>
        private static IEnumerable<Keyword> GrantableKeywords()
        {
            foreach (Keyword keyword in (Keyword[])Enum.GetValues(typeof(Keyword)))
                if (!KeywordSemantics.IsActionMarker(keyword)) yield return keyword;
        }

        /// <summary>
        /// Every whole number written in a clause, plus one — because the renderer spells a count of
        /// one as an article ("Draw a card.") rather than a digit, so the number that produced a
        /// sentence is not always visible in it.
        /// </summary>
        private static IEnumerable<int> Amounts(string clause)
        {
            var found = new List<int>();
            for (int i = 0; i < clause.Length; i++)
            {
                if (!char.IsDigit(clause[i])) continue;
                int end = i;
                while (end < clause.Length && char.IsDigit(clause[end])) end++;
                if (int.TryParse(clause.Substring(i, end - i), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int value) && !found.Contains(value))
                    found.Add(value);
                i = end;
            }
            if (!found.Contains(1)) found.Add(1);
            return found;
        }

        /// <summary>
        /// Counts to try for a verb that names copies of a card.
        ///
        /// <para><b>Why this is not <see cref="Amounts"/>.</b> A copy count is spelled as a WORD —
        /// "Summon two 1/1 Wisps." — because a token's name begins with its stats and a numeral would
        /// run straight into them. So the number that produced the sentence is not in the sentence,
        /// and reading it back would mean restating the renderer's number words here, which is the
        /// one thing this parser is built never to do. Proposing the small range instead costs
        /// nothing and keeps the spelling stated once, where it is printed.</para>
        /// </summary>
        private static IEnumerable<int> CopyCounts(string clause)
        {
            var counts = new List<int>(Amounts(clause));
            for (int n = 1; n <= 10; n++) if (!counts.Contains(n)) counts.Add(n);
            return counts;
        }

        /// <summary>
        /// Every "<c>+A/+H</c>" pair a clause contains, as attack then health. A token's stats read
        /// the same way ("1/1 Wisp"), so this yields candidates rather than an answer and lets the
        /// re-render decide.
        /// </summary>
        private static IEnumerable<KeyValuePair<int, int>> Deltas(string clause)
        {
            foreach (string token in clause.TrimEnd('.').Split(' '))
            {
                int slash = token.IndexOf('/');
                if (slash <= 0) continue;
                if (TryDelta(token.Substring(0, slash), out int attack) &&
                    TryDelta(token.Substring(slash + 1), out int health))
                    yield return new KeyValuePair<int, int>(attack, health);
            }
        }

        private static bool TryDelta(string text, out int value)
            => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

        /// <summary>
        /// Every target spec worth proposing, in the order that decides ties.
        ///
        /// <para><b>The blind spot this order used to work around is GONE.</b> A unique selector
        /// once rendered identically under <c>all</c> and under <c>player: 1</c> — "your hero" either
        /// way, because "all" of one thing is still it — and the round-trip gate could not tell which
        /// of the two an author meant, on 26 rows. Since the selection collapse there is only one
        /// value to reach: for <c>FRIENDLY_HERO</c> a count of one IS the maximum, so both spellings
        /// are now literally the same <c>TargetSelection</c>. The tie is not broken here; it does not
        /// exist.</para>
        ///
        /// <para>Order still decides ties between DIFFERENT counts, so every-candidate is proposed
        /// first, then the small ascending counts, then the random ones.</para>
        ///
        /// <para><b>Neither a filter, a binding nor a distinctFrom is enumerated.</b> Each is a real
        /// part of the target vocabulary and each is simply absent, which is why a sentence naming a
        /// trait ("a damaged minion", "all friendly Constructs") refuses rather than resolving to the
        /// untraited spec that would otherwise render close to it.</para>
        /// </summary>
        private static IEnumerable<TargetSpec> Specs(CostKind cost = CostKind.None)
        {
            for (int rank = 0; rank <= LastSelectionRank; rank++)
                foreach (string selector in TargetSpec.Selectors)
                {
                    if (!TrySelectionAt(rank, selector, out TargetSelection selection)) continue;
                    foreach (TargetFilterDef? filter in Filters())
                    {
                        yield return new TargetSpec(selector, selection, filter, cost: cost);
                        yield return new TargetSpec(selector, selection, filter, binding: SharedPick,
                            cost: cost);
                    }
                }
        }

        /// <summary>
        /// The traits a target can be narrowed by. A closed and very small space — one nullable flag
        /// and one nullable tribe — so it is enumerated like every other axis rather than read out of
        /// the noun phrase. No filter is yielded first, which keeps an untraited phrase resolving to
        /// an untraited spec.
        /// </summary>
        private static IEnumerable<TargetFilterDef?> Filters()
        {
            yield return null;
            yield return new TargetFilterDef(damaged: true);
            yield return new TargetFilterDef(damaged: false);
            foreach (Tribe tribe in (Tribe[])Enum.GetValues(typeof(Tribe)))
            {
                if (tribe == Tribe.NONE) continue;
                yield return new TargetFilterDef(tribe: tribe);
            }
        }

        /// <summary>
        /// The binding a derived shared pick carries.
        ///
        /// <para><b>The name is not read from anywhere and could be any string.</b> A binding is a
        /// GROUPING — two targets are one pick or they are not — and the renderer only ever compares
        /// bindings for equality, so nothing in a sentence can distinguish one name from another. The
        /// corpus proves the names were never load-bearing: of eighteen authored bindings thirteen
        /// label nothing at all, six merely restate the action they sit on (<c>heal_target</c>,
        /// <c>stats_target</c>), and every genuinely shared one is spelled exactly this.</para>
        ///
        /// <para>A bound spec is offered alongside its bare twin rather than instead of it, because
        /// the binding is what lets <c>IsProvablyTheSameTarget</c> hold for a player-chosen pair —
        /// two separate prompts and one shared prompt are different cards, and only the bound
        /// reading elides. The bare spec is yielded first so a lone clause is unaffected.</para>
        /// </summary>
        private const string SharedPick = "target";

        /// <summary>Rank 0 is every candidate, 1-3 are small chosen counts, 4-6 the random ones.</summary>
        private const int LastSelectionRank = 6;

        /// <summary>
        /// The selection at one rank for one selector, or nothing where that rank cannot exist.
        ///
        /// <para><b>This has to be per-selector now, and that is the point.</b> "Every candidate" is
        /// no longer a value that stands on its own — it is the selector's own maximum, so the same
        /// rank means <c>1</c> for <c>FRIENDLY_HERO</c> and <c>7</c> for <c>ANY_ENEMY_MINION</c>.
        /// Ranks whose count would exceed the maximum are skipped rather than proposed and refused:
        /// a chosen count above the maximum is a second spelling of every candidate, which is exactly
        /// the ambiguity the collapse exists to delete.</para>
        ///
        /// <para>A RANDOM count is NOT capped, because random may repeat — three rolls against one
        /// candidate hit it three times, so a count above the maximum is meaningful there.</para>
        /// </summary>
        private static bool TrySelectionAt(int rank, string selector, out TargetSelection selection)
        {
            selection = default;
            int? max = TargetQueries.Get(selector).MaxCandidates;

            if (rank >= 4)
            {
                selection = TargetSelection.RandomOf(rank - 3);
                return true;
            }
            if (rank == 0)
            {
                // Nothing bounds a deck or graveyard, so "every candidate" has no integer there.
                if (!max.HasValue) return false;
                selection = TargetSelection.Of(max.Value);
                return true;
            }
            if (max.HasValue && rank > max.Value) return false;
            selection = TargetSelection.Of(rank);
            return true;
        }
    }
}
