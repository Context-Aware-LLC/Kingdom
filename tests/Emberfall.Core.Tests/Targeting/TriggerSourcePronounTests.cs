using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Targeting
{
    /// <summary>
    /// How <c>TRIGGER_SOURCE</c> is spoken, and the coupling that decides it.
    ///
    /// <para><b>The gap this closes (Sam, 2026-08-27).</b> The selector resolved and validated
    /// perfectly and printed <i>"Whenever you summon a minion, give the trigger source +1/+1."</i>,
    /// which is not a sentence a card can carry — so C26 blocked every card in the
    /// react-to-what-you-just-summoned family and the corpus contained no row using the selector at
    /// all. The engine was never the blocker; the grammar was.</para>
    ///
    /// <para><b>Why the pronoun is keyed on the TRIGGER and not the selector.</b> A pronoun needs an
    /// antecedent. <c>TRIGGER_SOURCE</c> is legal on six triggers and only one of them —
    /// <c>ON_FRIENDLY_SUMMON</c> — states its timing in prose that NAMES a thing. Under
    /// <c>Toll:</c> or <c>Healed:</c> a bare "it" refers to nothing on the card, so those keep the
    /// long form. That makes the two tables in <c>CardTextGenerator</c> one fact:
    /// <c>TriggerPrefix</c> supplies the antecedent and <c>TriggerSourcePronoun</c> spends it, and
    /// the last assertion here is the only thing that would notice them drifting apart.</para>
    ///
    /// <para><b>Nothing in the shipped corpus exercises any of this today</b> — that is exactly why
    /// it is a fixture. A green CardCi run says the 293 authored rows are consistent; it says
    /// nothing about a selector none of them uses.</para>
    /// </summary>
    [TestFixture]
    public sealed class TriggerSourcePronounTests
    {
        // ── What the card face says ──────────────────────────────────────────

        /// <summary>
        /// The sentence the gap was blocking. Pinned literally: the whole point is the exact bytes,
        /// because C26 compares the authored face against this renderer character for character.
        /// </summary>
        [Test]
        public void The_trigger_that_names_a_minion_lets_the_effect_say_it()
            => Assert.That(Face(Trigger.ON_FRIENDLY_SUMMON, Grow()),
                Is.EqualTo("Whenever you summon a minion, give it +1/+1."));

        /// <summary>
        /// The other five triggers keep the long form, and that is the CORRECT outcome rather than
        /// a second gap: "Healed: Give it +1/+1." is a dangling pronoun, and inventing one for a
        /// shape nobody has asked to print is how a card ends up saying something it does not mean.
        /// Clumsy and honest beats fluent and wrong.
        /// </summary>
        [TestCase(Trigger.ON_HEAL, "Healed: Give the trigger source +1/+1.")]
        [TestCase(Trigger.ON_DAMAGED, "Damaged: Give the trigger source +1/+1.")]
        public void A_trigger_that_names_nothing_keeps_the_long_form(Trigger trigger, string expected)
            => Assert.That(Face(trigger, Grow()), Is.EqualTo(expected));

        /// <summary>
        /// The pronoun replaces the noun phrase wherever one would have gone, not only after
        /// "Give" — so an effect that reaches its target through "to" reads the same way.
        /// </summary>
        [Test]
        public void The_pronoun_serves_every_frame_a_noun_phrase_would()
            => Assert.That(
                Face(Trigger.ON_FRIENDLY_SUMMON, new HealAction(2, Source())),
                Is.EqualTo("Whenever you summon a minion, restore 2 Health to it."));

        /// <summary>
        /// A run under one marker is one sentence, and the pronoun is a property of that run rather
        /// than of any single effect — so the second clause speaks it too.
        /// </summary>
        [Test]
        public void Every_effect_in_the_run_shares_the_antecedent()
            => Assert.That(
                Face(Trigger.ON_FRIENDLY_SUMMON, Grow(), new GrantKeywordAction(Keyword.BULWARK, Source())),
                Is.EqualTo("Whenever you summon a minion, give it +1/+1 and Bulwark."));

        // ── What may not be said ─────────────────────────────────────────────

        /// <summary>
        /// A pronoun has no head noun, so every axis <c>Filter</c> splices onto one lands against a
        /// word that is not there — "it with Bulwark", "it that costs 4 or less". The narrowing is
        /// real and unsayable, so the ROW is what fails rather than the sentence quietly coming out
        /// mangled. Same treatment, and the same place, as <c>distinctFrom</c> on a unique selector.
        /// </summary>
        [Test]
        public void A_narrowed_pronoun_refuses_to_print_rather_than_mangling()
        {
            IReadOnlyList<RuleDefinition> rules = Rules(Trigger.ON_FRIENDLY_SUMMON,
                new ModifyStatsAction(1, 1, new TargetSpec("TRIGGER_SOURCE", TargetSelection.Of(1),
                    new TargetFilterDef(tribe: Tribe.BEAST))));

            Assert.That(() => CardTextGenerator.Generate(rules, CardTextGenerator.NoPool.Instance),
                Throws.Exception,
                "an unrenderable shape must reach C26 as UNRENDERABLE — a card is never shipped " +
                "carrying a sentence the renderer had to improvise");
        }

        // ── Reading it back ──────────────────────────────────────────────────

        /// <summary>
        /// The parser re-renders candidates to test them, so it has to re-render under the SAME
        /// trigger the sentence was read under — otherwise it compares "give it" against
        /// "give the trigger source" and refuses a card it just wrote.
        /// </summary>
        [Test]
        public void The_pronoun_round_trips_through_the_parser()
        {
            CardDefinition card = Card(Rules(Trigger.ON_FRIENDLY_SUMMON, Grow()));

            Assert.That(CardTextParser.TryParse(card, out IReadOnlyList<RuleDefinition> rules,
                out string reason), Is.True, $"refused: {reason}");
            Assert.That(CardTextGenerator.Generate(rules, CardTextGenerator.NoPool.Instance),
                Is.EqualTo(card.Text));
            Assert.That(RuleSemantics.TargetOf(rules[0].Action)?.Selector, Is.EqualTo("TRIGGER_SOURCE"),
                "reading 'it' back as some other selector that happens to render the same way is " +
                "the failure the round trip exists to catch");
        }

        // ── The coupling ─────────────────────────────────────────────────────

        /// <summary>
        /// <b>The guard that matters.</b> Everything above is one card shape; this is the invariant
        /// that keeps the next one honest. A trigger licenses a pronoun exactly when it speaks a
        /// noun in prose, so the two tables must agree member for member — and they are twenty lines
        /// apart in the same file, which is precisely the distance at which one gets a new arm and
        /// the other does not. Adding a prose trigger without a pronoun leaves the new card family
        /// blocked for the same reason this fixture exists; adding a pronoun without prose ships a
        /// dangling one.
        /// </summary>
        [Test]
        public void A_pronoun_exists_exactly_where_prose_supplies_its_antecedent()
        {
            foreach (Trigger trigger in (Trigger[])Enum.GetValues(typeof(Trigger)))
                Assert.That(CardTextGenerator.TriggerSourcePronoun(trigger).Length > 0,
                    Is.EqualTo(CardTextGenerator.TriggerPrefix(trigger).Length > 0),
                    $"{trigger}: a pronoun and the prose clause that introduces its antecedent are " +
                    "one fact stored in two switches — they move together or not at all");
        }

        // ── Fixture ──────────────────────────────────────────────────────────

        private static TargetSpec Source()
            => new TargetSpec("TRIGGER_SOURCE", TargetSelection.Of(1));

        private static ModifyStatsAction Grow() => new ModifyStatsAction(1, 1, Source());

        /// <summary>
        /// An observer's rules. Every trigger here is an ordinary board-minion observer, which is
        /// <c>SOURCE_BOUND</c> — the lifetime <c>CardValidator</c> rule C23 requires of one.
        /// </summary>
        private static IReadOnlyList<RuleDefinition> Rules(Trigger trigger, params RuleAction[] actions)
        {
            var rules = new List<RuleDefinition>(actions.Length);
            foreach (RuleAction action in actions)
                rules.Add(new RuleDefinition(
                    new RuleId("rule_" + (rules.Count + 1)),
                    new TriggeredActivation(trigger),
                    AlwaysCondition.Instance,
                    SourceBoundLifetime.Instance,
                    action));
            return rules;
        }

        private static string Face(Trigger trigger, params RuleAction[] actions)
            => CardTextGenerator.Generate(Rules(trigger, actions), CardTextGenerator.NoPool.Instance);

        private static CardDefinition Card(IReadOnlyList<RuleDefinition> rules)
            => TestCards.Authored(new CardDefinition(
                id: FixtureCard.Id("t_trigger_source"), handle: "t_trigger_source",
                name: "Trigger Source Card", type: CardType.MINION, cost: 4, attack: 3, health: 4,
                durability: null, @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), rules: rules, flavorText: "",
                artId: "t_trigger_source", setId: SetId.CORE, collectible: false,
                tribe: null, factionId: ""));
    }
}
