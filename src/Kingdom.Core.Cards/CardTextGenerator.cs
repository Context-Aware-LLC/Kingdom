using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>The ways a card's authored text can disagree with the rules printed under it.</summary>
    public enum CardTextDivergence
    {
        /// <summary>The authored sentence is exactly what these rules read as.</summary>
        None,

        /// <summary>The rules read as a sentence and the row carries none.</summary>
        Missing,

        /// <summary>The row carries a sentence and the rules read as nothing — text with no card behind it.</summary>
        Orphaned,

        /// <summary>Both exist and differ. Either the rules moved and the prose did not, or the reverse.</summary>
        Stale,

        /// <summary>The rules are a shape this renderer has no phrasing for, so nothing can be checked.</summary>
        Unrenderable,
    }

    /// <summary>
    /// Deterministically renders the normalized rules a card owns into the player-facing text those
    /// rules read as. Carrier keywords and normalized typed rules are the only inputs: trigger labels
    /// are derived from rule activation, never duplicated as keyword data.
    ///
    /// <para><b>This is a CHECKER, not the display path (Sam, 2026-08-16).</b> What a card says is
    /// <see cref="CardDefinition.Text"/> — authored on the row, and what every reader renders. This
    /// type exists to answer one question about that field: does the sentence still describe the
    /// rules? <see cref="Check"/> is the entry point the validator calls, and <c>C26</c> is where a
    /// disagreement surfaces.</para>
    ///
    /// <para><b>Why the direction was reversed.</b> Generating the text failed silently — it always
    /// emitted something, slightly wrong, and it shipped. Checking it fails loudly, at authoring
    /// time, on the one card that is wrong. The blast radius is the whole argument: re-wording a
    /// sentence here is a global policy binding on every card sharing the code path, which is why
    /// the dated rulings below read as legislation. Under authored text they are preferences again,
    /// and this file's job shrinks to holding the line that prose and rules agree.</para>
    ///
    /// <para>RETIREMENT TRIGGER: the parser. The reversal's end state is that rules are parsed FROM
    /// the authored sentence, at which point round-tripping replaces this comparison, exact-match
    /// stops being the definition of agreement, and this renderer's grammar becomes the parser's.
    /// <b>NARROWED, not retired (Sam, 2026-08-25).</b> This paragraph used to say whoever lands the
    /// parser retires <see cref="Check"/> and the C26 family with it. The ruling is that the parser
    /// takes the FIRST swing and <see cref="Check"/> survives behind it: parse the sentence, compare
    /// the derived rules to the authored ones, and on a match C26 does not run at all — it could
    /// only reject a legitimate re-wording, having compared strings where the parser compared
    /// meaning. C26 runs when the parser REFUSES, because an unreadable sentence has no other
    /// evidence behind it and strictness is right there. <b>The fall-through is on <i>refused</i>,
    /// never on <i>disagreed</i>:</b> a sentence that parses to rules other than the authored ones
    /// is a defect, and letting an exact string match rescue it would hide the one failure this
    /// pipeline exists to catch. So whoever lands the parser rewires <see cref="Check"/> into that
    /// second position; C26 then shrinks as parser coverage grows instead of dying on a flag day.
    /// Coverage is <c>CardTextParserCoverage</c>'s printed number — 200/200 rule-carrying cards as
    /// of 2026-08-25, so the refusal path currently guards nothing.</para>
    ///
    /// <para><b>THE GRAMMAR IS ASYMMETRIC, and this is the first requirement recorded against it
    /// (Sam, 2026-08-18): the parser accepts "next to" AND "beside" for adjacency.</b> Rendering
    /// stays single-valued — one rule shape, one sentence, which is what makes the round trip an
    /// oracle — while parsing is many-to-one and normalizes synonyms onto it. So a synonym costs the
    /// parser an alternation and costs this renderer nothing; do NOT add a second output form here
    /// to serve one. The canonical rendering remains "an adjacent minion" until Sam moves it.</para>
    ///
    /// <para><b>Do not generalise this into an alternatives policy (Sam, 2026-08-18, reason restated
    /// 08-19). The reason is MERGE COST, not correctness</b> — "even though it probably wouldn't hurt
    /// anything". This fork exists to land the effect model in the main project, so the target is the
    /// simplest working version of the grammar, and every extra accepted phrase is surface a reviewer
    /// over there has to take on. Adjacency's pair is what was asked for and is what ships; a scheme
    /// for "alternatives per parameter" is not wanted before a parameter needs one.</para>
    ///
    /// <para><b>And note what that request WAS.</b> C26's comment names its own forcing function:
    /// "the first author who genuinely needs to re-word a card is the forcing function for the
    /// parser rather than someone waiving a warning." Asking for a second accepted wording is that
    /// author arriving. Nothing is due today — no card needs the synonym yet — but the trigger has
    /// fired once, and the next time it fires the answer is the parser, not a widened exact-match.</para>
    /// </summary>
    public static class CardTextGenerator
    {
        /// <summary>
        /// Checks a card's authored text against the sentence its rules read as, handing back the
        /// expected rendering so a caller can show the author both halves of the disagreement.
        /// </summary>
        /// <remarks>
        /// <see cref="Generate"/> throws on a rule shape it has no phrasing for — deliberately, since
        /// silently rendering an unknown shape is the failure this whole direction exists to stop.
        /// That is a finding about the card, not a crash the validator should propagate, so it is
        /// caught here and reported as <see cref="CardTextDivergence.Unrenderable"/> against the row
        /// that caused it. Such a row is already failing a C-series rule; this names it a second time
        /// rather than taking the run down with a stack trace that does not say which card.
        /// </remarks>
        public static CardTextDivergence Check(CardDefinition card, ICardDatabase pool,
            out string expected)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            try
            {
                expected = Generate(card, pool);
            }
            catch (NotSupportedException)
            {
                expected = "";
                return CardTextDivergence.Unrenderable;
            }

            if (string.Equals(card.Text, expected, StringComparison.Ordinal))
                return CardTextDivergence.None;
            if (card.Text.Length == 0) return CardTextDivergence.Missing;
            if (expected.Length == 0) return CardTextDivergence.Orphaned;
            return CardTextDivergence.Stale;
        }

        /// <summary>Renders the card face these carrier keywords and typed rules read as.</summary>
        /// <remarks>
        /// <b>Not a display path.</b> A reader showing a card to a player reads
        /// <see cref="CardDefinition.Text"/>; calling this instead re-derives the face at read time
        /// and puts the pipeline back the way round the reversal took it out of.
        /// </remarks>
        /// <param name="pool">
        /// Where a card NAMED by another card's sentence is looked up. A sentence naming a card
        /// prints that card's own <c>Name</c> and stats, so the renderer cannot speak it from the
        /// row in hand. Passing <see cref="NoPool.Instance"/> is legitimate and simply makes such a
        /// sentence unrenderable rather than guessed — the same fail-closed shape
        /// <see cref="CardNameIndex.Empty"/> gives the parser.
        /// </param>
        public static string Generate(CardDefinition card, ICardDatabase pool)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            return Generate(card, card.Rules, pool);
        }

        /// <summary>
        /// Player-facing text after applying the controller's live Arcane Surge bonus. Authored
        /// copy remains authoritative when no bonus applies; a boosted spell is regenerated from
        /// its typed rules so every damage action receives the same amount the engine will use.
        /// </summary>
        public static string GenerateEffective(CardDefinition card, ICardDatabase pool,
            int spellDamageBonus)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));
            if (spellDamageBonus < 0)
                throw new ArgumentOutOfRangeException(nameof(spellDamageBonus));
            if (spellDamageBonus == 0 || card.Type != CardType.SPELL)
                return card.Text;

            var effectiveRules = new List<RuleDefinition>(card.Rules.Count);
            bool changed = false;
            foreach (RuleDefinition rule in card.Rules)
            {
                if (rule.Action is DamageAction damage)
                {
                    effectiveRules.Add(new RuleDefinition(rule.Id, rule.Activation, rule.Condition,
                        rule.Lifetime, damage.WithAmounts(damage.Amount + spellDamageBonus,
                            damage.InsteadAmount + spellDamageBonus)));
                    changed = true;
                }
                else
                {
                    effectiveRules.Add(rule);
                }
            }

            return changed ? Generate(card, effectiveRules, pool) : card.Text;
        }

        /// <summary>
        /// The card's text with every value a translation must supply marked as a slot —
        /// <c>Deal {dmg:3} damage to a {tribe:beast}.</c> See <see cref="CardTextTemplate"/>.
        /// </summary>
        /// <remarks>
        /// <b>The same renderer in a second voice, never a second grammar.</b> Every word is chosen
        /// exactly as <see cref="Generate(CardDefinition, ICardDatabase)"/> chooses it — an article
        /// against the real name, a plural by its real spelling — and only the value itself is
        /// printed as its slot, so <see cref="CardTextTemplate.FillEnglish"/> gives back the
        /// generated text byte for byte. The English words left around the slots are the source a
        /// translator works from; the slots are the part their translation must keep.
        /// </remarks>
        public static string Template(CardDefinition card, ICardDatabase pool)
        {
            if (card == null) throw new ArgumentNullException(nameof(card));

            Dictionary<string, string>? outer = _slots;
            _slots = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                return Generate(card, card.Rules, pool);
            }
            finally
            {
                _slots = outer;
            }
        }

        /// <summary>Each slot printed so far and the English it stands for, while a
        /// <see cref="Template"/> is being written; null otherwise. Per thread, because the
        /// renderer is static and the parser calls into it from tests running in parallel.</summary>
        [ThreadStatic] private static Dictionary<string, string>? _slots;

        /// <summary>Prints <paramref name="english"/>, or its slot while writing a template.</summary>
        private static string Slot(CardTextSlotKind kind, string id, string english, bool plural = false)
        {
            if (_slots == null) return english;
            string marker = new CardTextSlot(kind, id, plural).ToString();
            _slots[marker] = english;
            return marker;
        }

        /// <summary>
        /// The English a phrase reads as, slots and all. Every spelling decision — an article, a
        /// plural, an "-s" agreement — is made on this and never on the slot, so a template takes
        /// exactly the words the text does.
        /// </summary>
        private static string English(string phrase)
        {
            Dictionary<string, string>? slots = _slots;
            return slots == null || phrase.IndexOf('{') < 0
                ? phrase
                : CardTextTemplate.Fill(phrase, slot => slots[slot.ToString()]);
        }

        private static string Damage(int amount)
            => Slot(CardTextSlotKind.Damage, amount.ToString(CultureInfo.InvariantCulture),
                amount.ToString(CultureInfo.InvariantCulture));

        private static string KeywordWord(Keyword keyword) => KeywordWord(keyword, Name(keyword));

        private static string KeywordWord(Keyword keyword, string english)
            => Slot(CardTextSlotKind.Keyword, Id(keyword.ToString()), english);

        private static string LabelWord(string label, bool plural = false)
            => Slot(CardTextSlotKind.Label, Id(label), plural ? label + "s" : label, plural);

        private static string TribeWord(Tribe tribe)
            => Slot(CardTextSlotKind.Tribe, Id(tribe.ToString()), DescribeTribe(tribe));

        /// <summary>A label's or enum's slot id: lowercased, spaces as underscores — "Turn End" is
        /// <c>turn_end</c>, matching the glossary's <c>label_turn_end</c>.</summary>
        private static string Id(string name) => name.ToLowerInvariant().Replace(' ', '_');

        /// <summary>The English label a <see cref="CardTextSlotKind.Label"/> slot id stands for.</summary>
        internal static string LabelFor(string id)
        {
            foreach (string word in EmphasisVocabulary())
                if (Id(word) == id) return word;
            throw Unsupported("label", id);
        }

        /// <summary>How a tribe is spoken.</summary>
        internal static string DescribeTribe(Tribe tribe) => Name(tribe.ToString());

        /// <summary>The plural of a name, by the rules a card face uses.</summary>
        internal static string DescribePlural(string name) => Plural(name);

        private static string Generate(CardDefinition card, IReadOnlyList<RuleDefinition> rules,
            ICardDatabase pool)
        {

            string band = CarrierBand(card);
            var parts = new List<string>(1 + rules.Count);
            // Comma-separated and closed with a stop. Two separate jobs: the commas stop "Bulwark
            // Lifedrain" reading as one two-word keyword, and the stop separates the band from the
            // rules text that follows it. The trigger marker's colon used to do that second job —
            // "Bulwark Herald: Draw a card." — and ON_PLAY prints no marker any more, which would
            // have left "Bulwark Draw a card." Presentation emphasises each name on top of this.
            if (band.Length > 0) parts.Add(band);
            parts.AddRange(RenderRuleGroups(rules, pool ?? NoPool.Instance));
            return string.Join(" ", parts);
        }

        /// <summary>A pool that resolves nothing, so every card-naming sentence is unrenderable.</summary>
        /// <remarks>
        /// Not a convenience default on <see cref="Generate(CardDefinition, ICardDatabase)"/>: a
        /// caller with no pool has to say so, because the failure it buys — a card silently losing
        /// the clause that names another card — is exactly the kind this direction exists to stop.
        /// </remarks>
        public sealed class NoPool : ICardDatabase
        {
            public static readonly NoPool Instance = new NoPool();
            private NoPool() { }
            public bool TryGet(int cardId, out CardDefinition def) { def = null!; return false; }
            public CardDefinition Get(int cardId)
                => throw new KeyNotFoundException($"no card '{cardId}' — this renderer was given no pool.");
            public IReadOnlyCollection<CardDefinition> AllCollectible() => Array.Empty<CardDefinition>();
        }

        /// <summary>
        /// The leading carrier-keyword band, or "" when a card has none.
        ///
        /// <para>Internal because <see cref="CardTextParser"/> has to STRIP exactly what this
        /// PRINTS. It was briefly reimplemented over there and got it wrong on 38 cards — reading
        /// <c>KeywordSemantics.AuthoredName</c>, which is null for every keyword but FROST, instead
        /// of the title-cased fallback <see cref="Name(Keyword)"/> applies. One implementation, so
        /// the two halves cannot disagree again.</para>
        /// </summary>
        internal static string CarrierBand(CardDefinition card)
        {
            var carriers = new List<string>(card.Keywords.Count);
            foreach (Keyword keyword in card.Keywords)
            {
                // An action marker is rendered from its typed action instead. Printing it here as
                // well would print it twice on any row that still carries the legacy keyword.
                if (KeywordSemantics.IsActionMarker(keyword)) continue;
                carriers.Add(KeywordWord(keyword));
            }
            // Comma-separated and closed with a stop. Two separate jobs: the commas stop "Bulwark
            // Lifedrain" reading as one two-word keyword, and the stop separates the band from the
            // rules text that follows it.
            return carriers.Count == 0 ? "" : string.Join(", ", carriers) + ".";
        }

        /// <summary>How a keyword is spoken, for a parser that must match what was printed.</summary>
        internal static string DescribeKeyword(Keyword keyword) => Name(keyword);

        /// <summary>
        /// How a card is spoken when another card's sentence names it — "1/1 Wisp" for the row
        /// named Wisp. Exposed so <see cref="CardNameIndex"/> keys on exactly what gets printed.
        ///
        /// <para><b>This used to title-case the card's ID SLUG, and that was the defect</b> (fixed
        /// 2026-08-22). It made ids load-bearing for card text — renaming one forced the authored
        /// sentence to change with it, and it blocked the numeric immutable ids. Measured before
        /// the change: the reach was 20 rows naming 8 distinct cards, not the 261 the migration
        /// register estimated.</para>
        ///
        /// <para><b>A summoned minion prints its stats</b> (Sam, 2026-08-22) so the sentence says
        /// what the player gets. Only a minion: a weapon is equipped and a spell goes to hand, and
        /// neither reads as a body. Under the slug this fell out of whether whoever minted the id
        /// happened to encode the stats — 4 of the 8 named cards printed them and 4 did not, for no
        /// reason a reader could see. Now it is a rule.</para>
        /// </summary>
        internal static string DescribeCardName(CardDefinition named)
        {
            if (named == null) throw new ArgumentNullException(nameof(named));
            if (string.IsNullOrWhiteSpace(named.Name))
                throw Requires($"a Name on the card '{named.Id}' a sentence names");

            return named.Type == CardType.MINION && named.Attack.HasValue && named.Health.HasValue
                ? named.Attack.Value.ToString(CultureInfo.InvariantCulture) + "/" +
                  named.Health.Value.ToString(CultureInfo.InvariantCulture) + " " + named.Name
                : named.Name;
        }

        /// <summary>Resolves a named card through the pool, refusing rather than inventing a name.</summary>
        /// <remarks>
        /// An id the pool does not hold makes the sentence UNRENDERABLE, which C26 reports against
        /// the row that names it. The alternative — falling back to the id slug — is the behaviour
        /// this change removed, and it would come back silently on exactly the rows nobody checked.
        /// </remarks>
        private static string NamedCard(int cardId, ICardDatabase pool)
        {
            if (cardId <= 0) throw Requires("a card id to name");
            if (pool == null || !pool.TryGet(cardId, out CardDefinition named))
                throw Unsupported("naming the card",
                    cardId + " — no such row in the pool given to the renderer");
            return Slot(CardTextSlotKind.Card, cardId.ToString(CultureInfo.InvariantCulture),
                DescribeCardName(named));
        }

        /// <summary>Renders the rule half of a card face on its own, without any carrier keywords.</summary>
        /// <remarks>
        /// A test seam, not card-face API: every shipping caller renders a whole card. Kept internal
        /// so a rule list rendered without its carrier keywords cannot become a second, half-right
        /// way to print a card face. The friend declaration is in AssemblyInfo.cs — in code, because
        /// Unity never reads the .csproj.
        /// </remarks>
        internal static string Generate(IReadOnlyList<RuleDefinition> rules, ICardDatabase pool)
        {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            return string.Join(" ", RenderRuleGroups(rules, pool ?? NoPool.Instance));
        }

        /// <summary>Renders the rules as card-face lines, one per run of rules sharing a prefix.</summary>
        /// <remarks>
        /// One line per RUN of consecutive rules sharing a trigger/cost/condition/lifetime prefix,
        /// not one line per rule — a Toll with three effects is one "Toll:", not "Toll:" printed
        /// three times. Only adjacent rules merge, so an interleaved trigger (on-play, Toll,
        /// on-play) still prints the marker each time it actually changes.
        ///
        /// <para><b>A COST is a fourth prefix component and needed no new grouping machinery</b>
        /// (Sam, 2026-08-22) — it is the same device as <c>Toll:</c>, a clause and a colon scoping
        /// what follows. The cost rule prints no line of its own: its clause IS the prefix, and the
        /// run beneath it is exactly the rules whose condition says the price was paid.</para>
        /// </remarks>
        private static List<string> RenderRuleGroups(IReadOnlyList<RuleDefinition> rules, ICardDatabase pool)
        {
            var lines = new List<string>();
            var groupActions = new List<RuleAction>();
            // The payoff clauses riding the open run's choice — see RiderClause.
            var groupRiders = new List<string>();
            string groupPrefix = "";
            string costPrefix = "";
            // A run only ever groups rules that share a prefix, and the trigger marker is part of
            // that prefix -- so every action in a group was raised by the same trigger and the
            // pronoun it licenses is a property of the run, not of the action.
            string groupPronoun = "";

            void Close()
            {
                if (groupActions.Count == 0) return;
                lines.Add(Line(groupPrefix, RunSentence(groupActions, groupRiders, pool, groupPronoun)));
                groupActions.Clear();
                groupRiders.Clear();
            }

            IReadOnlyList<RuleDefinition> ordered = RidersBehindTheirChooser(rules);
            for (int i = 0; i < ordered.Count; i++)
            {
                RuleDefinition rule = ordered[i];
                if (rule == null) throw new ArgumentNullException(nameof(rules));

                if (RuleSemantics.IsRider(rule))
                {
                    // Directly behind the rule whose choice it reads, which opened a run of its
                    // own below — so the open run is that one rule and nothing else. A COST that
                    // chooses opens no run at all, and a payoff cannot ride a price.
                    if (groupActions.Count != 1)
                        throw Unsupported("condition", "TARGET_IS_FRIENDLY riding a cost");
                    groupRiders.Add(RiderClause(rule, pool, groupPronoun));
                    continue;
                }

                if (CostOf(rule) != CostKind.None)
                {
                    // The cost opens a new clause, so whatever run was open is finished.
                    Close();
                    costPrefix = CostPrefix(rule.Action, pool);
                    continue;
                }

                string prefix = TriggerMarker(rule.Activation) +
                    CostScope(rule.Condition, costPrefix) +
                    ConditionPrefix(rule.Condition) + LifetimePrefix(rule.Lifetime, rule.Activation);
                // A rule with riders behind it is a sentence to itself, on both sides: the
                // semicolon attaches the payoff to ONE subject, so nothing joins it from in front
                // and nothing joins after it.
                bool headsRiders = i + 1 < ordered.Count && RuleSemantics.IsRider(ordered[i + 1]);
                if (prefix != groupPrefix || headsRiders || groupRiders.Count > 0) Close();

                groupPrefix = prefix;
                groupPronoun = SourcePronoun(rule.Activation);
                groupActions.Add(rule.Action);
            }

            Close();
            return lines;
        }

        /// <summary>
        /// The rules in the order the face speaks them: every rider directly behind the rule whose
        /// choice it reads.
        ///
        /// <para><b>The face and the resolver want opposite orders, and the rule list serves the
        /// resolver.</b> A minion rider is authored AHEAD of its damage so the buff lands first and
        /// a kindled body survives; the sentence still has to open with what the card does to
        /// whatever you point it at — "Deal 2 damage to a character; a friendly minion gains
        /// +2/+1." Same ruling as <see cref="VocabularyFirst"/>: the canonical rendering decides
        /// the word order, so the rules are free to say what the engine needs.</para>
        ///
        /// <para>Refuses a rider with no choosing rule on its trigger, or with two: either way the
        /// count below comes out wrong, and a rider that reads no choice can never hold.
        /// <c>CardValidator</c> C32 rejects the same rows first.</para>
        /// </summary>
        private static IReadOnlyList<RuleDefinition> RidersBehindTheirChooser(IReadOnlyList<RuleDefinition> rules)
        {
            bool any = false;
            foreach (RuleDefinition rule in rules)
                if (rule != null && RuleSemantics.IsRider(rule)) { any = true; break; }
            if (!any) return rules;

            var ordered = new List<RuleDefinition>(rules.Count);
            foreach (RuleDefinition rule in rules)
            {
                if (rule == null) throw new ArgumentNullException(nameof(rules));
                if (RuleSemantics.IsRider(rule)) continue;
                ordered.Add(rule);
                if (!RuleSemantics.Chooses(rule)) continue;
                Trigger trigger = RuleSemantics.TriggerOf(rule);
                foreach (RuleDefinition rider in rules)
                    if (RuleSemantics.IsRider(rider) && RuleSemantics.TriggerOf(rider) == trigger)
                        ordered.Add(rider);
            }
            if (ordered.Count != rules.Count)
                throw Unsupported("condition", "TARGET_IS_FRIENDLY without exactly one choosing rule on its trigger");
            return ordered;
        }

        /// <summary>One run as a sentence: its actions, the riders on its choice, and an instead-clause.</summary>
        private static string RunSentence(IReadOnlyList<RuleAction> actions, IReadOnlyList<string> riders,
            ICardDatabase pool, string sourcePronoun)
        {
            string sentence = riders.Count == 0
                ? JoinActions(actions, pool, sourcePronoun)
                : ChooserClause(actions[0], pool, sourcePronoun) + "; " + string.Join("; ", riders) + ".";

            foreach (RuleAction action in actions)
            {
                if (!(action is DamageAction damage) || !damage.InsteadAmount.HasValue) continue;
                // "Deals 2 if…" has to have one damage to stand in for. In a joined run it would
                // read against whichever clause happened to come last.
                if (actions.Count != 1) throw Unsupported("action", "DAMAGE with an instead-amount in a joined run");
                sentence += " " + InsteadSentence(damage);
            }
            return sentence;
        }

        /// <summary>
        /// The choosing rule's clause when a rider follows it — target NAMED, no full stop.
        ///
        /// <para>An unrestricted pick normally prints no target at all ("Deal 2 damage.", see
        /// <see cref="NarrowsNothing"/>), because naming it tells the reader nothing. Beside a rider
        /// it tells him the one thing the card turns on: that "a friendly minion" is among the
        /// characters he may aim at. So here, and only here, it is spoken.</para>
        /// </summary>
        private static string ChooserClause(RuleAction action, ICardDatabase pool, string sourcePronoun)
            => action is DamageAction damage && NarrowsNothing(damage.Target)
                ? $"Deal {Damage(damage.Amount)} damage to {Target(damage.Target, sourcePronoun)}"
                : Undot(RenderAction(action, pool, sourcePronoun));

        /// <summary>
        /// "Give a friendly minion +1/+1, or give your hero +1 Attack this turn and 1 Armor." — a stat
        /// change aimed at <c>ANY_FRIENDLY</c> (Empower, Sam 2026-11-04). On a hero the Attack lasts the
        /// turn and the Health is Armor, so the hero half is spelled out rather than left to read as
        /// "Give a friendly character +1/+1". <c>CardValidator</c> C36 admits it with gains only.
        /// "Attack" takes the ON_ATTACK label's slot, the game's one word for it.
        /// </summary>
        private static string MinionOrYourHero(ModifyStatsAction a, string sourcePronoun)
        {
            TargetSpec t = a.Target;
            var minion = new TargetSpec("ANY_FRIENDLY_MINION", t.Selection, t.Filter, t.Binding);
            var gifts = new List<string>(2);
            if (a.AttackDelta > 0) gifts.Add($"{Delta(a.AttackDelta)} {LabelWord("Attack")} this turn");
            if (a.HealthDelta > 0) gifts.Add($"{a.HealthDelta} Armor");
            if (gifts.Count == 0) throw Unsupported("action", "MODIFY_STATS on ANY_FRIENDLY with no gain");
            return $"Give {Target(minion, sourcePronoun)} {Delta(a.AttackDelta)}/{Delta(a.HealthDelta)}, " +
                   $"or give your hero {string.Join(" and ", gifts)}.";
        }

        /// <summary>
        /// A rider as the clause behind the semicolon: what the chosen target gains if it was your
        /// minion, or what you get if it was your hero.
        ///
        /// <para>The minion form names its subject — "a friendly minion gains +2/+1" — because the
        /// subject IS the condition: no "if" is needed to say the payoff belongs to your own. The
        /// hero form has no such subject to lean on, the payoff being yours rather than the
        /// target's, so it says the condition outright.</para>
        /// </summary>
        private static string RiderClause(RuleDefinition rule, ICardDatabase pool, string sourcePronoun)
        {
            var condition = (TargetIsFriendlyCondition)rule.Condition;
            if (condition.Kind == FriendlyTargetKind.HERO)
            {
                if (RuleSemantics.TargetOf(rule.Action) != null)
                    throw Unsupported("action", "a targeted action under TARGET_IS_FRIENDLY HERO");
                return "if it was your hero, " +
                       Decapitalize(Undot(RenderAction(rule.Action, pool, sourcePronoun)));
            }

            switch (rule.Action)
            {
                case ModifyStatsAction stats:
                    return $"a friendly minion gains {Delta(stats.AttackDelta)}/{Delta(stats.HealthDelta)}";
                case GrantKeywordAction grant:
                    return $"a friendly minion gains {KeywordWord(grant.Keyword)}";
                default:
                    throw Unsupported("action", rule.Action.GetType().Name + " under TARGET_IS_FRIENDLY MINION");
            }
        }

        /// <summary>"Deals 2 if your hero has taken damage this turn." No "instead" (Sam, 2026-11-03).</summary>
        /// <remarks>The condition is spoken by <see cref="ConditionPrefix"/> and only re-cased here, so
        /// a condition reads the same whether it gates a rule or switches an amount.</remarks>
        private static string InsteadSentence(DamageAction damage)
        {
            string condition = ConditionPrefix(damage.InsteadCondition!).TrimEnd(' ', ',');
            return $"Deals {Damage(damage.InsteadAmount!.Value)} {Decapitalize(condition)}.";
        }

        /// <summary>How a rider is spoken, for a parser that must match what was printed.</summary>
        internal static string DescribeRider(RuleDefinition rule, ICardDatabase pool)
            => RiderClause(rule, pool, SourcePronoun(rule.Activation));

        /// <summary>How an instead-clause is spoken, for the same parser.</summary>
        internal static string DescribeInstead(DamageAction damage) => InsteadSentence(damage);

        /// <summary>Joins a run's sentence onto its prefix.</summary>
        /// <remarks>
        /// <b>A prefix ending in a COMMA has not finished its sentence.</b> A trigger marker and a
        /// cost both end in a colon, after which a capital is right; a condition or a lifetime clause
        /// ends "While it is your turn, " and the effect continues that same sentence, so it takes
        /// the lowercase treatment <see cref="JoinActions"/> already gives every clause but the
        /// first — game vocabulary spared, so "…, Freeze a minion." keeps its capital.
        ///
        /// <para>Surfaced by the corpus's first ON_TURN_START card, which printed "While it is your
        /// turn, Give this +1/+1." <c>LifetimePrefix</c>'s PERMANENT and FINITE clauses carry the
        /// same comma and were wrong in the same way, unexercised because no card uses them.</para>
        /// </remarks>
        private static string Line(string prefix, string sentence)
            => prefix.EndsWith(", ", StringComparison.Ordinal)
                ? prefix + Decapitalize(sentence)
                : prefix + sentence;

        /// <summary>The cost a rule's own target declares, or <see cref="CostKind.None"/>.</summary>
        private static CostKind CostOf(RuleDefinition rule)
            => RuleSemantics.TargetOf(rule.Action)?.Cost ?? CostKind.None;

        /// <summary>
        /// The cost clause a gated rule sits beneath — <c>"Sacrifice a friendly minion: "</c>.
        ///
        /// <para>Refuses rather than printing nothing when a rule waits on a price no earlier rule
        /// charges. The card would otherwise read as an unconditional effect while the runtime gate
        /// held it shut forever, which is precisely the text-versus-behaviour divergence this
        /// direction exists to delete. <c>CardValidator</c> C28 rejects the same shape first; this
        /// is the renderer refusing to have a second opinion about it.</para>
        /// </summary>
        private static string CostScope(RuleCondition condition, string costPrefix)
        {
            if (!(condition is CostPaidCondition)) return "";
            if (costPrefix.Length == 0)
                throw Unsupported("condition", "COST_PAID with no cost rule before it");
            return costPrefix;
        }

        /// <summary>
        /// Renders a cost rule as the prefix scoping what follows it.
        ///
        /// <para><b>"You may" is the whole of the optional spelling</b> (Sam, 2026-08-22) and the
        /// only thing separating a price you must pay from one you may decline. Everything else — the
        /// verb, the noun phrase, the colon — is identical, which is what makes the pair read as one
        /// grammatical device rather than two frames.</para>
        /// </summary>
        private static string CostPrefix(RuleAction action, ICardDatabase pool)
        {
            CostKind cost = RuleSemantics.TargetOf(action)?.Cost ?? CostKind.None;
            if (cost == CostKind.None) throw Requires("a cost-bearing target to render a cost prefix");
            string clause = Undot(RenderAction(action, pool, sourcePronoun: ""));
            return cost == CostKind.Optional
                ? "You may " + Decapitalize(clause) + ": "
                : clause + ": ";
        }

        /// <summary>How a cost prefix is spoken, for a parser that must match what was printed.</summary>
        /// <remarks>
        /// The same seam as <see cref="DescribeAction"/> and for the same reason: reimplementing
        /// "clause, colon, and 'You may' when declinable" on the parser side would be the one fact
        /// stated twice, and the pair would drift the first time either half moved.
        /// </remarks>
        internal static string DescribeCostPrefix(RuleAction action, ICardDatabase pool)
            => CostPrefix(action, pool);

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
        ///
        /// <para><b>A standalone keyword clause opens the sentence</b> (Sam, 2026-08-23), whatever
        /// order the rules are authored in — see <see cref="VocabularyFirst"/>.</para>
        /// </remarks>
        private static string JoinActions(IReadOnlyList<RuleAction> group, ICardDatabase pool,
            string sourcePronoun)
        {
            if (group.Count == 0) throw Requires("at least one action to join");

            IReadOnlyList<(string Text, TargetSpec? Target)> actions =
                RenderClauses(VocabularyFirst(group, pool, sourcePronoun), pool, sourcePronoun);
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
        /// Moves a run's standalone keyword clauses to the front of it — "Backdraft 1 and deal 3
        /// damage to a minion", never "Deal 3 damage to a minion and Backdraft 1".
        ///
        /// <para><b>The highlighted word leads (Sam, 2026-08-23.)</b> The engine used to slave the
        /// sentence to the order the rules happened to be authored in, so putting the keyword first
        /// on the face meant re-authoring the rules to match — which is the tail wagging the dog,
        /// since a next-turn crystal lock and an immediate damage roll do not interact and nothing
        /// about the card decided that order in the first place. The canonical rendering decides it
        /// now, and the rule list is free to say whatever the resolver needs.</para>
        ///
        /// <para><b>Only a clause that names no target moves</b>, which today is Backdraft and
        /// Unearth. A keyword that takes one — Freeze, Silence — is part of the target grammar:
        /// <see cref="DropSharedOpening"/> elides a repeated noun phrase by shared opening words, so
        /// hoisting "Freeze a minion" out of "Deal 2 damage to a minion and Freeze it" would print
        /// the minion twice to buy the capital letter. Reordering is stable and this returns
        /// <paramref name="group"/> untouched when there is nothing to hoist, so a run that is
        /// already keyword-first — or has no keyword clause at all — renders exactly as before.</para>
        /// </summary>
        private static IReadOnlyList<RuleAction> VocabularyFirst(IReadOnlyList<RuleAction> group,
            ICardDatabase pool, string sourcePronoun)
        {
            if (group.Count < 2) return group;

            var led = new List<RuleAction>(group.Count);
            var rest = new List<RuleAction>(group.Count);
            foreach (RuleAction action in group)
            {
                // Rendered rather than type-tested: EmphasisVocabulary is derived from the enums, so
                // a keyword or a label added there leads a sentence without anyone editing a list
                // here. A shape this renderer cannot print throws in the same place it would have
                // thrown below, which Check reports as Unrenderable against the row.
                if (!(action is TargetedRuleAction) && OpensWithVocabulary(RenderAction(action, pool, sourcePronoun)))
                    led.Add(action);
                else
                    rest.Add(action);
            }

            if (led.Count == 0 || rest.Count == 0) return group;
            led.AddRange(rest);
            return led;
        }

        /// <summary>Whether a clause's opening word is one the game capitalises.</summary>
        private static bool OpensWithVocabulary(string clause)
        {
            clause = English(clause);
            int end = clause.IndexOf(' ');
            string first = Undot(end < 0 ? clause : clause.Substring(0, end));
            foreach (string word in EmphasisVocabulary())
                if (string.Equals(word, first, StringComparison.Ordinal)) return true;
            return false;
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
        private static IReadOnlyList<(string Text, TargetSpec? Target)> RenderClauses(IReadOnlyList<RuleAction> group,
            ICardDatabase pool, string sourcePronoun)
        {
            var clauses = new List<(string, TargetSpec?)>(group.Count);

            for (int i = 0; i < group.Count; i++)
            {
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
                        spread.Append(Damage(amounts[0]))
                            .Append(" damage ").Append(Times(amounts.Count))
                            .Append(" to ").Append(RandomPluralTarget(spec, sourcePronoun)).Append('.');
                    }
                    else
                    {
                        for (int j = 0; j < amounts.Count; j++)
                        {
                            if (j > 0) spread.Append(j == amounts.Count - 1 ? " and " : ", ");
                            spread.Append(Damage(amounts[j]));
                        }
                        spread.Append(" damage each to ").Append(Target(spec, sourcePronoun)).Append('.');
                    }

                    // No target attached: this clause has already named its own, and it must never
                    // be folded again by the same-target elision, which would drop the "each".
                    clauses.Add((spread.ToString(), null));
                    i = run;
                    continue;
                }

                clauses.Add((RenderAction(group[i], pool, sourcePronoun),
                    group[i] is TargetedRuleAction targeted ? targeted.Target : null));
            }

            return clauses;
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
                x.Selection == y.Selection && SameFilter(x.Filter, y.Filter);
        }

        /// <summary>How many times one repeated hit lands.</summary>
        private static string Times(int count) => count switch
        {
            2 => "twice",
            _ => NumberWord(count) + " times",
        };

        /// <summary>The plural of a randomly chosen target — "random enemy characters".</summary>
        private static string RandomPluralTarget(TargetSpec spec, string sourcePronoun)
        {
            string plural = PhrasingFor(spec.Selector, sourcePronoun).Plural;
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
                first.Selection == second.Selection && SameFilter(first.Filter, second.Filter);
        }

        /// <summary>Compares filters by value — TargetFilterDef is a plain class, so the default
        /// reference comparison would call two identical authored filters different.</summary>
        private static bool SameFilter(TargetFilterDef? first, TargetFilterDef? second)
        {
            if (first == null || second == null) return first == null && second == null;
            return first.Damaged == second.Damaged &&
                first.Tribe == second.Tribe && first.NegateTribe == second.NegateTribe &&
                first.MaxCost == second.MaxCost && first.NegateCost == second.NegateCost &&
                first.Keyword == second.Keyword && first.NegateKeyword == second.NegateKeyword;
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
            => OpensWithVocabulary(clause)
                ? clause
                : char.ToLowerInvariant(clause[0]) + clause.Substring(1);

        /// <summary>The label an aura's rules are printed under.</summary>
        public const string AuraLabel = "Aura";

        /// <summary>The label the UNEARTH action prints. Not a <see cref="Keyword"/> — no card carries
        /// it — so it would be missing from any vocabulary derived from the enum alone.</summary>
        public const string UnearthLabel = "Unearth";

        /// <summary>
        /// The whole clause UNEARTH prints, without its terminator.
        ///
        /// <para><b>It takes a noun, and that is what lets it compose</b> (Sam, 2026-09-04). The bare
        /// label was tried first and reads fine alone — "Unearth." — but a card that unearths AND
        /// does something else has to join two clauses, and "Unearth and draw a card." is broken
        /// English. Gear Cache is exactly that card. So the object is printed even though the
        /// mechanic implies it.</para>
        ///
        /// <para>Public, and read by <c>CardTextParser</c>, for the same reason
        /// <see cref="FrostVerb"/> is: the two halves of the round trip spell it once.</para>
        /// </summary>
        public const string UnearthClause = UnearthLabel + " a card";

        /// <summary>
        /// The verb the FROST <b>action</b> prints — "Freeze a minion." The keyword prints "Frost".
        ///
        /// <para><b>Two words for two mechanics, deliberately</b> (Sam, 2026-08-25). The action
        /// picks a target and freezes it as the card resolves; the keyword freezes whatever its
        /// carrier damages. They were the same string until this split, so converting a card from
        /// one to the other left the face reading the same word and the player with no way to tell
        /// which one they were holding.</para>
        ///
        /// <para>Public for the same reason <see cref="UnearthLabel"/> is: the parser proposes a
        /// <c>FrostAction</c> by looking for this verb at the head of a clause, and reading it from
        /// here is what keeps the two halves of the round trip spelled once.</para>
        /// </summary>
        public const string FrostVerb = "Freeze";

        /// <summary>
        /// The verb the FROST action prints with its negate switch set — "Unfreeze a minion."
        /// (Sam, 2026-10-06). Public for the same reason <see cref="FrostVerb"/> is: the parser
        /// proposes the negated <c>FrostAction</c> by looking for this verb at the head of a clause.
        /// </summary>
        public const string ThawVerb = "Unfreeze";

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
            // FrostVerb is seeded like the two labels below it, and for the same reason: it is game
            // vocabulary that is not a keyword NAME, so the loop over the enum cannot reach it.
            // Without this line "Freeze a minion." would be the one effect verb on any card that
            // renders unemphasised, because every other one — Silence, Backdraft — happens to be
            // spelled exactly like the keyword it belongs to.
            var words = new List<string> { AuraLabel, UnearthLabel, FrostVerb, ThawVerb };

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
            // UNMARKED, and the only trigger other than "you played it" that is (Sam, 2026-08-25).
            // The rule above — every timing that is not "when you played it" carries a label — holds
            // because a label is the ONLY thing that would state the timing. ON_TURN_START is the one
            // trigger where that is false: CardValidator requires a TURN_OWNER condition on it and
            // rejects that condition everywhere else, so the condition prefix ALWAYS speaks the
            // timing in prose ("At the start of your turn, "). Keeping the label printed it twice —
            // "Turn Start: While it is your turn, …" — and the doubling is what made
            // clock_mechanical_essence read as a standing buff rather than a per-turn tick.
            Trigger.ON_TURN_START => "",
            Trigger.ON_TURN_END => "Turn End",
            // UNLABELLED, and it speaks in prose instead — see TriggerPrefix (Sam, 2026-08-25).
            Trigger.ON_FRIENDLY_SUMMON => "",
            Trigger.ON_SPELL_CAST => "Spell Cast",
            Trigger.ON_CAST => "",
            _ => throw Unsupported("trigger", trigger),
        };

        /// <summary>
        /// The prose clause a LABELLESS trigger speaks its timing with, or "" for a trigger that
        /// prints a label or nothing at all. The second of the two ways a timing can be stated, and
        /// exclusive with <see cref="TriggerLabel"/>: a trigger prints one or the other, never both.
        ///
        /// <para><b>Why ON_FRIENDLY_SUMMON is not a label.</b> A label is a styled piece of the
        /// game's vocabulary — a word the player learns once and then recognises, like Toll or
        /// Attack. "Friendly Summon" was never that: it is the enum id with the underscore taken
        /// out, and the qualifier it leads with does the opposite of what a reader expects. Sam
        /// asked outright, on 2026-08-25, what the word "Friendly" was supposed to indicate on
        /// Cogsworth — it marks the SUMMONED minion as yours, not the effect, not the target, and
        /// nothing in "Friendly Summon: Give all friendly minions +1/+1." says so. A label that has
        /// to be explained is not doing a label's job, and inventing a shorter word for it only
        /// moves the problem. Prose states the same timing with nothing left to infer.</para>
        ///
        /// <para><b>The "you" in this clause was false for six weeks, and the ENGINE was corrected
        /// to match it rather than the other way round</b> (#272, Sam, 2026-09-04). The trigger
        /// fired on both players' summons because <c>OrderedTriggerSources.Snapshot</c> appended
        /// both sides unconditionally and this trigger inherited that walk without ever being given
        /// a controller filter. It now has one — <c>OrderedTriggerSources.IsControllerScoped</c> —
        /// so the clause is true and so is 1159 Cogsworth's face. The alternative on the table was
        /// re-wording this to "Whenever a minion is summoned"; it lost because every piece of
        /// authored intent (the enum name, both card texts, the schema doc) said friendly-only and
        /// only the implementation disagreed.</para>
        ///
        /// <para>Bearing on the arm below: this clause is what licenses <c>TRIGGER_SOURCE</c> to
        /// print as the pronoun "it", and until #272 that pronoun could refer to the OPPONENT'S
        /// minion. If this trigger ever goes back to both sides, the pronoun goes with it.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER:</b> a second labelless-but-spoken trigger. This is a switch
        /// with one live arm on purpose — it is a table, not a special case, so the next one adds a
        /// line here and the parser reads it back with no change at all. If this ever grows a second
        /// arm whose clause is not a plain prefix, revisit <c>TryTrigger</c>, which assumes a
        /// marker is a literal head it can take off the front.</para>
        /// </summary>
        public static string TriggerPrefix(Trigger trigger) => trigger switch
        {
            Trigger.ON_FRIENDLY_SUMMON => "Whenever you summon a minion, ",
            _ => "",
        };

        /// <summary>
        /// The pronoun <c>TRIGGER_SOURCE</c> is spoken with under one trigger, or "" where it has
        /// no antecedent and must name itself.
        ///
        /// <para><b>A pronoun needs something to refer back to, and the trigger is the only thing
        /// that can supply one.</b> <see cref="TriggerPrefix"/> is where a trigger states its timing
        /// in prose, and "Whenever you summon a minion, " is the only clause in the vocabulary that
        /// introduces a NOUN while doing so — so it is the only place a later "it" is licensed.
        /// Every other trigger states its timing with a label (<c>Toll:</c>, <c>Healed:</c>) or with
        /// nothing at all, and a bare "it" under one of those refers to nothing on the card.</para>
        ///
        /// <para>That is why this is keyed on the TRIGGER and not on the selector.
        /// <c>TRIGGER_SOURCE</c> is legal on six triggers (see
        /// <c>RuleActionCapabilities.TriggerSourceShape</c>) and on five of them the sentence has
        /// not named the thing, so it keeps the long form and reads as the clumsy phrase it is —
        /// which is the correct outcome, not a gap: those five are shapes nobody has asked to
        /// print, and inventing a pronoun for them would ship a dangling one.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER:</b> this table and <see cref="TriggerPrefix"/> are one fact
        /// in two switches — a trigger that speaks a noun in prose and a trigger whose source can be
        /// pronominalised are the same trigger. A second arm added to either belongs in both, and if
        /// a third case ever appears they collapse into one table returning the pair.</para>
        /// </summary>
        public static string TriggerSourcePronoun(Trigger trigger) => trigger switch
        {
            Trigger.ON_FRIENDLY_SUMMON => "it",
            _ => "",
        };

        /// <summary>The pronoun an activation licenses for <c>TRIGGER_SOURCE</c>, or "".</summary>
        /// <remarks>
        /// An <c>AURA</c> is a continuous recompute with no triggering event, so it never has a
        /// trigger source to speak of and licenses nothing.
        /// </remarks>
        private static string SourcePronoun(RuleActivation activation)
            => activation is TriggeredActivation triggered
                ? TriggerSourcePronoun(triggered.Trigger)
                : "";

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
            if (activation is AuraActivation) return LabelWord(AuraLabel) + ": ";
            if (activation is TriggeredActivation triggered) return TriggerMarker(triggered.Trigger);
            throw UnsupportedNode("activation", activation);
        }

        /// <summary>Maps one trigger to its marker; playing the card is deliberately unmarked.</summary>
        private static string TriggerMarker(Trigger trigger)
        {
            string label = TriggerLabel(trigger);
            return label.Length == 0 ? TriggerPrefix(trigger) : LabelWord(label) + ": ";
        }

        /// <summary>The exact head a trigger prints, label form or prose form — the one place that
        /// decides it, so the parser can take a marker off the front without knowing which kind it
        /// is. "" for a trigger that prints nothing. Same seam as
        /// <see cref="DescribeConditionPrefix"/>, for the same reason.</summary>
        internal static string DescribeTriggerMarker(Trigger trigger) => TriggerMarker(trigger);

        /// <summary>Renders the clause qualifying when a rule applies, ahead of its effects.</summary>
        private static string ConditionPrefix(RuleCondition condition)
        {
            if (condition is AlwaysCondition) return "";
            // The cost clause itself is the prefix, and RenderRuleGroups is the only place that can
            // build it — this condition names no cost, so on its own it has nothing to say.
            if (condition is CostPaidCondition) return "";
            if (condition is TurnOwnerCondition turnOwner) return TurnOwnerPrefix(turnOwner.Owner);
            if (condition is ControlsCondition controls) return ControlsPrefix(controls);
            if (condition is HeroDamagedThisTurnCondition) return "If your hero has taken damage this turn, ";
            // TARGET_IS_FRIENDLY never reaches here: a rider prints behind its chooser's semicolon
            // (RiderClause), not as a prefix of its own.
            throw UnsupportedNode("condition", condition);
        }

        /// <summary>How a condition is spoken, for a parser that must match what was printed.</summary>
        /// <remarks>The same seam as <see cref="DescribeCostPrefix"/> and for the same reason: the
        /// parser reads a condition back by rendering every candidate through this and keeping the
        /// one that prints the clause it is looking at, so the two halves cannot drift.</remarks>
        internal static string DescribeConditionPrefix(RuleCondition condition) => ConditionPrefix(condition);

        /// <summary>
        /// Maps a board condition to the clause naming what you must control —
        /// <c>"If you control another Construct, "</c>.
        ///
        /// <para>The determiner is the whole of the difference between a card that counts itself and
        /// one that cannot: see <see cref="ControlsCondition.ExcludesSource"/>, which carries why the
        /// flag is authored on the condition rather than read off the carrier here.</para>
        /// </summary>
        private static string ControlsPrefix(ControlsCondition controls)
        {
            if (controls.Tribe == Tribe.NONE) throw Unsupported("tribe", controls.Tribe);
            string tribe = TribeWord(controls.Tribe);
            string determiner = controls.ExcludesSource ? "another" : Article(tribe);
            return "If you control " + determiner + " " + tribe + ", ";
        }

        /// <summary>Maps a turn-owner condition to the clause naming whose turn it must be.</summary>
        private static string TurnOwnerPrefix(TurnOwner owner) => owner switch
        {
            // These name a MOMENT, not a span (Sam, 2026-08-25). TURN_OWNER is legal only on
            // ON_TURN_START, so the condition can only ever mean "which turn-starts does this fire
            // on" — never "while it is your turn". The old wording said the latter, describing a
            // standing conditional buff the engine cannot express at all, and an author read it that
            // way. Since ON_TURN_START now prints no label, this clause carries the whole timing.
            TurnOwner.CONTROLLER => "At the start of your turn, ",
            TurnOwner.OPPONENT => "At the start of your opponent's turn, ",
            TurnOwner.ANY => "At the start of each turn, ",
            _ => throw Unsupported("turn owner", owner),
        };

        /// <summary>Renders the clause bounding how long a rule applies, ahead of its effects.</summary>
        /// <remarks>
        /// <b>The same lifetime node reads differently on an aura, because it counts a different
        /// thing.</b> On a triggered rule <c>uses</c> is FIRINGS — the count falls when the rule
        /// resolves. An aura never resolves; it is aged one turn at a time by its controller's turn
        /// end (<c>CommandProcessor.ExpireControllerAuras</c>). One field, two clocks, and the text
        /// has to name the right one: "For its next 3 uses" on a continuous effect would promise a
        /// player three firings of something that never fires.
        ///
        /// <para>PERMANENT prints nothing on an aura. "Aura: " has already told the player it is
        /// continuous, and a permanent aura simply has no end to announce — the same empty prefix a
        /// minion's SOURCE_BOUND aura gets, which is right, because to a player they read the same:
        /// it is on until something takes it away.</para>
        /// </remarks>
        private static string LifetimePrefix(RuleLifetime lifetime, RuleActivation activation)
        {
            if (activation is AuraActivation) return AuraLifetimePrefix(lifetime);
            if (lifetime is SourceBoundLifetime || lifetime is OneShotLifetime) return "";
            if (lifetime is PermanentLifetime) return "Permanently, ";
            if (lifetime is FiniteLifetime finite)
            {
                if (finite.Uses <= 0) throw Requires("a positive finite lifetime");
                return finite.Uses == 1 ? "For its next use, " : $"For its next {finite.Uses} uses, ";
            }
            throw UnsupportedNode("lifetime", lifetime);
        }

        /// <summary>The duration clause for a continuous effect, counted in TURNS.</summary>
        private static string AuraLifetimePrefix(RuleLifetime lifetime)
        {
            // A minion's aura, and a spell aura with no declared end. Neither has a duration to
            // print: the first ends when the minion leaves, the second does not end.
            if (lifetime is SourceBoundLifetime || lifetime is PermanentLifetime) return "";
            // One tick, taken at the caster's own turn end — so it lasts exactly the turn it was
            // cast on, which is what a player means by "this turn".
            if (lifetime is OneShotLifetime) return "This turn, ";
            if (lifetime is FiniteLifetime finite)
            {
                if (finite.Uses <= 0) throw Requires("a positive finite lifetime");
                return finite.Uses == 1 ? "This turn, " : $"For {finite.Uses} turns, ";
            }
            throw UnsupportedNode("lifetime", lifetime);
        }

        /// <summary>
        /// Whether a target names the source itself — asked as a SHAPE, never as the string "SELF",
        /// for the reason <see cref="AuraCapabilities.IsAutomaticMinionSet"/> spells out: a name
        /// listed in one place and not another is a divergence with nothing checking it.
        /// </summary>
        private static bool IsSelf(TargetSpec target)
            => TargetQueries.TryGet(target.Selector, out TargetQuery query)
               && query.Relation == SourceRelation.Self;

        /// <summary>Renders one typed action as a complete effect sentence.</summary>
        private static string RenderAction(RuleAction action, ICardDatabase pool,
            string sourcePronoun) => action switch
            {
                DamageAction a => $"Deal {Damage(a.Amount)} damage{To(a.Target, sourcePronoun)}.",
                HealAction a => $"Restore {a.Amount} Health{To(a.Target, sourcePronoun)}.",
                PreventLethalDamageAction _ =>
                    "The first other friendly minion dealt lethal damage each turn survives at 1 Health.",
                RepeatNextMinionPlayAction _ =>
                    "The next minion you play this turn triggers its play effect twice.",
                // Prints the stats rather than a marker word, unlike its sibling action markers below.
                // A flat +1/+1 has a definition shorter than any word for it, needs no glossary entry,
                // and cannot be misread as the on-play marker it would sit beside.
                //
                // The selector is the ONLY thing on this line carrying allegiance, so a card that means
                // friendly must say so through it. Sam, 2026-08-20: "Buffing the enemy board is never
                // the intent, but it should still be possible... unless the card text specifically says
                // 'a friendly minion'." The words ARE the restriction; C26 keeps them true to the
                // selector underneath.
                // A minion buffing ITSELF gains; "Give this +1/+1" is the same fact in the third person
                // about the speaker (Sam, 2026-08-25). The allegiance argument above does not apply —
                // SELF names one entity and carries no side to get wrong — so this is purely how the
                // sentence reads. Every other selector keeps "Give", because there the words ARE the
                // restriction.
                ModifyStatsAction a when IsSelf(a.Target)
                    => $"Gain {Delta(a.AttackDelta)}/{Delta(a.HealthDelta)}.",
                ModifyStatsAction a when a.Target.Selector == "ANY_FRIENDLY"
                    => MinionOrYourHero(a, sourcePronoun),
                ModifyStatsAction a => $"Give {Target(a.Target, sourcePronoun)} {Delta(a.AttackDelta)}/{Delta(a.HealthDelta)}.",
                GrantKeywordAction a => $"Give {Target(a.Target, sourcePronoun)} {KeywordWord(a.Keyword)}.",
                DestroyAction a => $"{DestroyVerb(a.Target)} {Target(a.Target, sourcePronoun)}.",
                // "to hand" is jargon — a rules-text shorthand that assumes the reader already knows
                // whose hand. Naming the owner works for one minion and for a board of them alike.
                ReturnToHandAction a => $"Return {Target(a.Target, sourcePronoun)} to {BounceDestination(a.Target)}.",
                ReanimateAction a => $"Reanimate {Target(a.Target, sourcePronoun)}.",
                SilenceAction a => $"{KeywordWord(Keyword.SILENCE)} {Target(a.Target, sourcePronoun)}.",
                TransformAction a => $"Transform {Target(a.Target, sourcePronoun)} into {NamedCard(a.CardId, pool)}.",
                // NOT named through the keyword — see FrostVerb. This was `Name(Keyword.FROST)` until
                // 2026-08-25, which is what made the action and the carrier print the same word.
                FrostAction a => $"{LabelWord(a.Negate ? ThawVerb : FrostVerb)} {Target(a.Target, sourcePronoun)}.",
                DrownAction a => $"{KeywordWord(Keyword.DROWN)} {Target(a.Target, sourcePronoun)}.",
                CopyAction a => $"Summon an exact copy of {Target(a.Target, sourcePronoun)}.",
                TakeControlAction a => $"Take control of {Target(a.Target, sourcePronoun)}.",
                // The trigger's own label is the noun — "Toll" comes from TriggerLabel, not from a
                // second spelling here, so a card face and a timing marker cannot drift apart. Reads
                // "Trigger a random friendly minion's Toll." — repetitive under a Toll: prefix, and
                // deliberately so: the second word is the effect, and any synonym would be a word the
                // glossary does not carry.
                TriggerTollAction a => $"Trigger {TollOf(a.Target, sourcePronoun)}.",
                DrawAction a => $"Draw {Cards(a.Count)}.",
                SummonAction a => $"Summon {Copies(a.Count, NamedCard(a.CardId, pool))}.",
                AddToHandAction a => $"Add {Copies(a.Count, NamedCard(a.CardId, pool))} to your hand.",
                EquipAction a => $"Equip {NamedCard(a.CardId, pool)}.",
                GainManaAction a => Mana(a),
                // No parentheses — trialling the bare number to see how it reads (Sam, 2026-08-11).
                BackdraftAction a => $"{KeywordWord(Keyword.BACKDRAFT)} {a.Amount}.",
                GainArmorAction a => $"Gain {a.Amount} Armor.",
                // No number: the offer size is fixed by UnearthAction.Options, so printing it would
                // be restating a constant as though the card chose it.
                UnearthAction _ => $"{LabelWord(UnearthLabel)}{UnearthClause.Substring(UnearthLabel.Length)}.",
                _ => throw UnsupportedNode("action", action),
            };

        /// <summary>
        /// "Sacrifice" for your own minions, "Destroy" for anything else. One action either way —
        /// the engine has a single <see cref="DestroyAction"/> and the word is chosen at print time.
        ///
        /// <para><b>Sam, 2026-08-22:</b> <i>"sacrifice is a choice, and thus can only apply to your
        /// own minions. An enemy minion can only be destroyed."</i> So the word is keyed on the
        /// target's SIDE, which is where volition lives — not on whether the pick is a cost. Those
        /// two happen to coincide across the whole corpus today (every friendly destroy is a cost,
        /// every unrestricted one is not), and keying on side is the rule that stays true if a card
        /// ever separates them.</para>
        ///
        /// <para><c>ANY_MINION</c> reaches both boards, so it keeps "Destroy" — the player may well
        /// pick an enemy, and a word promising otherwise would be a restriction the selector does
        /// not carry.</para>
        /// </summary>
        private static string DestroyVerb(TargetSpec target)
            => TargetQueries.TryGet(target.Selector, out TargetQuery query) &&
               query.Side == TargetSide.Friendly
                ? "Sacrifice"
                : "Destroy";

        /// <summary>
        /// The <c>" to X"</c> of an effect whose target is a prepositional phrase — or nothing at
        /// all, when naming the target would state a restriction that does not exist.
        ///
        /// <para><b>Sam, 2026-08-20:</b> <i>"a minion would not say 'deal two damage to a character'.
        /// It would say 'deal two damage.' To me, there's no target restriction there. That damage
        /// can be dealt to anything, including the minion itself, and if a player wants to do that,
        /// I see no reason to prevent it."</i> The phrase was not describing a restriction, it was
        /// describing the absence of one, at the cost of four words and a noun ("character") that
        /// exists nowhere in the game's vocabulary except this sentence.</para>
        ///
        /// <para><b>Why it elides here and could not elide for a direct object.</b> The target of
        /// DAMAGE and HEAL is the object of a preposition, and English drops an uninformative
        /// prepositional phrase cleanly — "Deal 3 damage." is a whole sentence. A direct object
        /// cannot go the same way: "Destroy." is not a sentence, so <c>DESTROY</c> keeps its noun
        /// however unrestricted it is. That is a fact about the grammar, not a special case, and it
        /// is why this helper is reachable from exactly the two arms that read prepositionally.</para>
        ///
        /// <para><b>The single pick is load-bearing.</b> Only an unrestricted pick of ONE says
        /// nothing by naming its target; <c>all</c> over the same selector means hit everything, and
        /// dropping its phrase would render the two as the same sentence. A filter or a
        /// <c>distinctFrom</c> is a restriction by definition and keeps the phrase too.</para>
        ///
        /// <para>DISCHARGED 2026-08-22: this used to test the selector by NAME. It now reads the
        /// query, so any selector that happens to be unrestricted qualifies without being listed —
        /// which is what the criteria work was for.</para>
        /// </summary>
        private static string To(TargetSpec target, string sourcePronoun)
            => NarrowsNothing(target) ? "" : " to " + Target(target, sourcePronoun);

        /// <summary>Whose hand a bounced card lands in.</summary>
        /// <remarks>
        /// <para><b>Read off the target's SIDE axis, not written per card.</b> The handler puts the
        /// card in <c>Players[t.Seat]</c> — the CONTROLLER — so where it lands is already decided by
        /// the same axis that decided which minions were reachable. One phrase per side, and the
        /// number only matters where both sides are in play.</para>
        ///
        /// <para><b>Every card used to say "their owner's hand", and it was wrong twice.</b> The
        /// engine has no owner: <c>CardInstance</c> carries only <c>Controller</c>, so a stolen
        /// minion returns to whoever holds it now rather than to whoever built the deck. And
        /// "their" agreed with nothing — "a minion" is singular. Sam ruled the phrase out on
        /// 2026-08-22.</para>
        /// </remarks>
        private static string BounceDestination(TargetSpec target)
        {
            if (!TargetQueries.TryGet(target.Selector, out TargetQuery query))
                throw Unsupported("target selector", target.Selector);
            if (query.Side == TargetSide.Friendly) return "your hand";

            // A sweep names no owner at all (Sam, 2026-08-22). Every card lands in the hand of
            // whoever controls it, so on a sweep the possessive adds a word and settles nothing --
            // and the both-sides plural it forced, "their owners' hands", was simply ugly.
            if (query.Side == TargetSide.Enemy) return "hand";
            return target.Selection.Count == 1 ? "its owner's hand" : "hand";
        }

        /// <summary>Whether naming this target would tell the reader anything they do not already know.</summary>
        /// <remarks>
        /// Derived from the QUERY, not from a selector name: a target narrows nothing when it admits
        /// both sides, the whole board, and every kind of character on it, with no relation to the
        /// source. Today that is <c>ANY_CHARACTER</c> and only <c>ANY_CHARACTER</c>, but nothing here
        /// says so — a future unrestricted selector qualifies on its own merits.
        ///
        /// <para>The single pick stays load-bearing. Only an unrestricted pick of ONE says nothing by
        /// naming its target; every candidate over the same selector means hit everything, and
        /// dropping its phrase would render the two as the same sentence.</para>
        /// </remarks>
        private static bool NarrowsNothing(TargetSpec target)
        {
            if (target.Filter != null || target.DistinctFrom.Length != 0) return false;
            if (!TargetSpec.IsPlayerChosen(target) || target.Selection.Count != 1) return false;
            if (!TargetQueries.TryGet(target.Selector, out TargetQuery query)) return false;
            return query.Side == TargetSide.Both
                   && query.Zones == TargetZones.Board
                   && query.Types == (TargetTypes.Hero | TargetTypes.Minion)
                   && query.Relation == SourceRelation.Any;
        }

        /// <summary>
        /// The noun phrase for a target spec, exposed so <see cref="CardTextParser"/> can invert the
        /// renderer by GENERATING candidates and testing them against a sentence, rather than
        /// hand-writing a second grammar that could drift from this one.
        ///
        /// <para>That is the whole reason this seam exists: an inverse table would be a second
        /// statement of how a target is spoken, and the pair could disagree with nothing checking.
        /// Generate-and-test cannot disagree, because there is only one renderer and the parser
        /// calls it.</para>
        /// </summary>
        internal static string DescribeTarget(TargetSpec target, Trigger? trigger = null)
            => Target(target, trigger.HasValue ? TriggerSourcePronoun(trigger.Value) : "");

        /// <summary>
        /// One typed action as its effect sentence, exposed for the same reason as
        /// <see cref="DescribeTarget"/> — so <see cref="CardTextParser"/> can confirm a candidate it
        /// built renders back to the sentence it was read from.
        ///
        /// <para><b>This is what makes the parser structurally unable to be wrong.</b> It does not
        /// interpret a frame and trust its own reading; it proposes an action and asks the renderer
        /// whether that action would print this sentence. A frame written carelessly therefore
        /// produces a REFUSAL rather than a wrong rule, because the only thing that can turn a
        /// candidate into a parse is this method agreeing byte for byte.</para>
        /// </summary>
        internal static string DescribeAction(RuleAction action, ICardDatabase pool,
            Trigger? trigger = null)
            => RenderAction(action, pool, trigger.HasValue ? TriggerSourcePronoun(trigger.Value) : "");

        /// <summary>
        /// A whole marker's worth of actions as the single sentence they print as — the conjunction,
        /// the commas, the decapitalisation and the elided shared subject all included.
        ///
        /// <para>Exposed for the same reason as <see cref="DescribeAction"/>, and it is what lets the
        /// parser read a conjunction without a theory of one: it proposes a LIST of actions and asks
        /// whether that list would print this sentence. The elision is the hard part of the join and
        /// this is the only thing that knows how it works, so asking it is the one way to invert it
        /// that cannot drift.</para>
        /// </summary>
        internal static string DescribeActions(IReadOnlyList<RuleAction> group, ICardDatabase pool,
            Trigger? trigger = null)
            => JoinActions(group, pool, trigger.HasValue ? TriggerSourcePronoun(trigger.Value) : "");

        /// <summary>Renders a target spec as the noun phrase an effect sentence reads it as.</summary>
        private static string Target(TargetSpec target, string sourcePronoun)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            // No count check any more: a non-positive count has no spelling in TargetSelection, so
            // nothing that parses can arrive here holding one.
            bool random = IsRandomlyChosen(target);

            string description = Quantity(target, PhrasingFor(target.Selector, sourcePronoun));
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
            if (target.Filter != null)
            {
                // Refused for the same reason distinctFrom is refused above, and checked in the same
                // place: a pronoun has no head noun, so every axis Filter splices onto one would
                // land against a word that is not there -- "it with Shroud", "it that costs 4 or
                // less". The narrowing is real and unsayable, so the ROW is what fails
                // (C26-TEXT-UNRENDERABLE) rather than the sentence quietly coming out mangled.
                if (description == sourcePronoun && sourcePronoun.Length > 0)
                    throw Unsupported("filter on the pronoun form of", target.Selector);
                description = Filter(description, target.Filter);
            }
            // Unconditional: a random selection is never "every candidate", so a description
            // beginning "all " cannot reach Randomize() and needs no guard here.
            if (random) description = Randomize(description);
            return description;
        }

        /// <summary>Whether a target is chosen at random.</summary>
        /// <remarks>
        /// This was a three-armed switch over a selection MODE, with a throwing default for an arm
        /// that had no phrasing. There is nothing left to be exhaustive over: the marker is the
        /// question, and its absence is answered by the count instead.
        /// </remarks>
        private static bool IsRandomlyChosen(TargetSpec target) => target.Selection.IsRandom;

        /// <summary>How one selector is spoken, and which form an unspecified count means.</summary>
        private readonly struct SelectorPhrasing
        {
            public readonly string Singular;
            public readonly string Plural;

            /// <summary>Binds the two spoken forms of a selector.</summary>
            public SelectorPhrasing(string singular, string plural)
            {
                Singular = singular;
                Plural = plural;
            }
        }

        /// <summary>
        /// Whether a selector names a SET of things rather than one thing, so that "all" of it reads
        /// as the plural. <b>Derived from the criteria, not declared per name</b> — it is exactly
        /// "can this query ever produce more than one candidate", which is
        /// <see cref="TargetQuery.IsUnique"/> negated.
        ///
        /// <para>It was a hand-kept flag on every phrasing until 2026-08-20, and it was WRONG on
        /// seven of them — <c>ANY_MINION</c> was marked as naming one thing while resolving to every
        /// minion on the board. Nothing caught it because each name of a redundant pair was only
        /// ever authored with the selection mode its flag happened to suit, so the wrong branch had
        /// no row to render. Collapsing the pairs is what made one name serve both modes and forced
        /// the flag to mean what it says.</para>
        /// </summary>
        private static bool NamesASet(string selector) => !RuleActionCapabilities.IsUniqueSelector(selector);

        /// <summary>Phrases a selector.</summary>
        private static SelectorPhrasing Say(string singular, string plural)
            => new SelectorPhrasing(singular, plural);

        /// <summary>Maps a target selector to the words it is spoken with.</summary>
        /// <param name="sourcePronoun">
        /// The word <c>TRIGGER_SOURCE</c> is spoken with when the sentence has already named it, or
        /// "" when nothing has. See <see cref="TriggerSourcePronoun"/> for which trigger licenses
        /// one and why it is the trigger that decides.
        /// </param>
        private static SelectorPhrasing PhrasingFor(string selector, string sourcePronoun) => selector switch
        {
            "SELF" => Say("this", "these"),
            // Unique, so the plural is unreachable either way; both forms are the pronoun so that
            // nothing downstream has to know which one it took.
            "TRIGGER_SOURCE" when sourcePronoun.Length > 0 => Say(sourcePronoun, sourcePronoun),
            "TRIGGER_SOURCE" => Say("the trigger source", "trigger sources"),
            "ADJACENT" => Say("an adjacent minion", "adjacent minions"),
            "ANY_CHARACTER" => Say("a character", "characters"),
            // "enemies", not "enemy characters" (Sam, 2026-08-13). This name absorbed
            // ENEMY_CHARACTERS on 2026-08-20 and took its wording with it, which is what the ruling
            // asked for: the short word is the one that fits the frame.
            "ANY_ENEMY" => Say("an enemy", "enemies"),
            "ANY_FRIENDLY" => Say("a friendly character", "friendly characters"),
            "ANY_FRIENDLY_MINION" => Say("a friendly minion", "friendly minions"),
            "ANY_MINION" => Say("a minion", "minions"),
            "ENEMY_HERO" => Say("the enemy hero", "enemy heroes"),
            "ANY_ENEMY_MINION" => Say("an enemy minion", "enemy minions"),
            "FRIENDLY_HERO" => Say("your hero", "friendly heroes"),
            "OTHER_FRIENDLY_MINIONS" => Say("another friendly minion", "other friendly minions"),
            "FRIENDLY_WEAPON" => Say("your weapon", "friendly weapons"),
            "ENEMY_WEAPON" => Say("the enemy weapon", "enemy weapons"),
            "FRIENDLY_HAND" => Say("a card in your hand", "cards in your hand"),
            "ENEMY_HAND" => Say("a card in the enemy hand", "cards in the enemy hand"),
            "ANY_HAND_CARD" => Say("a hand card", "hand cards"),
            "FRIENDLY_DECK" => Say("a card in your deck", "cards in your deck"),
            "ENEMY_DECK" => Say("a card in the enemy deck", "cards in the enemy deck"),
            "ANY_DECK_CARD" => Say("a deck card", "deck cards"),
            "FRIENDLY_GRAVEYARD" => Say("a card in your graveyard", "cards in your graveyard"),
            "ENEMY_GRAVEYARD" => Say("a card in the enemy graveyard", "cards in the enemy graveyard"),
            "ANY_GRAVEYARD_CARD" => Say("a graveyard card", "graveyard cards"),
            "FRIENDLY_GRAVEYARD_MINION" => Say("a minion in your graveyard", "minions in your graveyard"),
            "ENEMY_GRAVEYARD_MINION" => Say("a minion in the enemy graveyard", "minions in the enemy graveyard"),
            "ANY_GRAVEYARD_MINION" => Say("a graveyard minion", "graveyard minions"),
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
        private static string Quantity(TargetSpec target, SelectorPhrasing phrasing)
        {
            if (target.IsEveryCandidate)
                return NamesASet(target.Selector) ? "all " + phrasing.Plural : phrasing.Singular;
            int count = target.Selection.Count;
            return count == 1 ? phrasing.Singular : count + " " + phrasing.Plural;
        }

        /// <summary>
        /// The possessive noun phrase a <c>TRIGGER_TOLL</c> reads as — "a random friendly minion's
        /// Toll", "all friendly minions' Tolls".
        ///
        /// <para><b>Number comes from the spec, the apostrophe from the spelling.</b> Those are two
        /// different questions and answering both off the string was the trap: "a Beast" is singular
        /// and does not end in s today, but a tribe whose name does would have read as a plural. So
        /// the LABEL is pluralised from <see cref="IsPlural"/>, which is the same derivation
        /// <see cref="Quantity"/> already uses, and only the apostrophe form — <c>s'</c> against
        /// <c>'s</c> — is decided by the final letter, which is the one thing English really does
        /// key on the spelling.</para>
        /// </summary>
        private static string TollOf(TargetSpec target, string sourcePronoun)
        {
            string phrase = Target(target, sourcePronoun);
            string label = LabelWord(TriggerLabel(Trigger.ON_DEATH), IsPlural(target));
            string possessive = English(phrase).EndsWith("s", StringComparison.Ordinal) ? "'" : "'s";
            return phrase + possessive + " " + label;
        }

        /// <summary>Whether a target's noun phrase is spoken in the plural — the same question
        /// <see cref="Quantity"/> answers when it picks between a selector's two spoken forms.</summary>
        private static bool IsPlural(TargetSpec target)
            => target.IsEveryCandidate
                ? NamesASet(target.Selector)
                : target.Selection.Count != 1;

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
            // A keyword QUALIFIES the head noun and sits against it: "all friendly Shroud minions".
            // It cannot use SpliceAfterDeterminer like an adjective does -- that inserts after "all "
            // and would say "all Shroud friendly minions" -- and it runs before the tribe so that
            // both together read "all friendly Shroud Constructs", the tribe still replacing the noun
            // this has just qualified.
            // A negated KEYWORD reuses its positive machinery unchanged and only swaps the word
            // that goes into it for a "non-" compound -- "non-Shroud minions" -- so the
            // plural/article handling InsertBeforeHeadNoun already carries needs no
            // negation-aware branch of its own (Sam, 2026-09-05).
            if (filter.Keyword.HasValue)
            {
                string keywordWord = KeywordWord(filter.Keyword.Value, Name(filter.Keyword.Value.ToString()));
                if (filter.NegateKeyword) keywordWord = "non-" + keywordWord;
                description = InsertBeforeHeadNoun(description, keywordWord);
            }

            // The tribe REPLACES the noun rather than qualifying it (Sam, 2026-08-11): "all friendly
            // Constructs", not "all friendly Construct minions". Splicing it after the determiner
            // produced "all Construct friendly minions", which is not English — a classifying noun
            // has to sit against the head noun, and once it does it has already said "minion".
            //
            // A negated tribe does NOT reuse that compound -- "non-Undead" tried to and could not
            // even pluralize (Sam, 2026-09-06: "reanimate 7 graveyard minions... except undead").
            // It trails as its own clause instead, the same shape MaxCost's "or less"/"or more"
            // already uses below, and always in the tribe's plural/class form regardless of the
            // spec's own count -- "a minion other than Constructs" excludes the category, not one
            // member of it.
            if (filter.Tribe.HasValue)
            {
                if (filter.NegateTribe)
                    description += " other than " + Plural(TribeWord(filter.Tribe.Value));
                else
                    description = ReplaceHeadNoun(description, TribeWord(filter.Tribe.Value));
            }

            // "damaged" is an ordinary adjective and keeps its place after the determiner.
            if (filter.Damaged.HasValue)
                description = SpliceAfterDeterminer(description, filter.Damaged.Value ? "damaged " : "undamaged ");

            // A cost is the one axis that is NOT an adjective. English has no pre-nominal form of
            // it -- "a 4-or-less enemy minion" is not a phrase -- so it trails the noun as a
            // relative clause and must therefore be spliced LAST: everything above inserts between
            // the determiner and the head noun, and a clause already hanging off the end would put
            // the head noun out of reach of ReplaceHeadNoun. Bare numeral, no brackets, matching
            // the rest of the vocabulary (Sam, 2026-08-11).
            if (filter.MaxCost.HasValue)
                description += " that " + CostVerb(description) + " " +
                    filter.MaxCost.Value.ToString(CultureInfo.InvariantCulture) +
                    (filter.NegateCost ? " or more" : " or less");

            return description;
        }

        /// <summary>Agrees the cost clause's verb with the phrase it hangs off.</summary>
        /// <remarks>
        /// The number of the head noun is not recoverable from the filter -- the same
        /// <see cref="TargetFilterDef"/> renders onto "an enemy minion" and onto "all enemy
        /// minions" -- so it is read off the description, which is the only thing that knows.
        /// </remarks>
        private static string CostVerb(string description)
            => English(description).EndsWith("s", StringComparison.Ordinal) ? "cost" : "costs";

        /// <summary>Sets a qualifier against a minion phrase's head noun, keeping its number.</summary>
        /// <remarks>
        /// Falls back to a trailing "with X" on a phrase that names no minion — "an enemy with
        /// Shroud". That shape is unauthored today and the fallback exists so it reads as English
        /// rather than as a mangled determiner if it ever is.
        /// </remarks>
        private static string InsertBeforeHeadNoun(string description, string qualifier)
        {
            if (description.EndsWith(" minions", StringComparison.Ordinal))
                return description.Substring(0, description.Length - " minions".Length) + " " + qualifier + " minions";
            if (description.EndsWith(" minion", StringComparison.Ordinal))
                return description.Substring(0, description.Length - " minion".Length) + " " + qualifier + " minion";
            if (string.Equals(description, "minions", StringComparison.Ordinal)) return qualifier + " minions";
            if (string.Equals(description, "minion", StringComparison.Ordinal)) return qualifier + " minion";
            return description + " with " + qualifier;
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
        // "all other " is the only two-word entry, and it is what makes the longest-first ordering
        // load-bearing: "all " would otherwise match first and word-order the adjective wrong.
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
            string quantity = char.IsDigit(English(cardName)[0]) ? NumberWord(count) : count.ToString(CultureInfo.InvariantCulture);
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
            // A whole slot pluralises as its plural slot, spelled from the English it stands for.
            if (_slots != null && _slots.TryGetValue(cardName, out string? english))
            {
                CardTextSlot one = CardTextTemplate.Slots(cardName)[0];
                return Slot(one.Kind, one.Id, Plural(english), plural: true);
            }

            int start = cardName.LastIndexOf(' ') + 1;   // 0 when the name is a single word
            string head = cardName.Substring(0, start);
            string noun = cardName.Substring(start);

            // Undead is its own plural, the way "sheep" and "deer" are -- the tribe this game ships
            // whose ordinary "+s" spelling rule is simply wrong for it, not merely a class the rules
            // below don't cover. Caught when Mass Rebirth's "non-Undeads" (Sam, 2026-09-06) turned
            // out to be the second card, not the first, to print the same wrong word.
            if (string.Equals(noun, "Undead", StringComparison.Ordinal)) return head + noun;

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
            phrase = English(phrase);
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
        /// <summary>Title-cases an underscored ENUM name — "ARCANE_SURGE" to "Arcane Surge".</summary>
        /// <remarks>
        /// It used to double as the card-id speaker, which is why it carried namespace-word skipping
        /// and an attack/health-pair rule. <see cref="DescribeCardName"/> reads the row instead as of
        /// 2026-08-22, and that machinery went with it — an enum name has neither.
        /// </remarks>
        private static string Name(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) throw Requires("a non-empty identifier");

            string[] parts = identifier.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);

            string name = TitleCase(parts, 0, parts.Length);
            if (name.Length == 0) throw Requires("a readable identifier");
            return name;
        }

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
