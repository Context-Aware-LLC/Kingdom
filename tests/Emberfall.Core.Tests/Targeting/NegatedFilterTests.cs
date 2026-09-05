using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Targeting
{
    /// <summary>
    /// The negate bit on Tribe/MaxCost/Keyword (Sam, 2026-09-05) reuses the positive axis's own
    /// English and round-trip machinery — see the "non-" compound note on
    /// <c>CardTextGenerator.Filter</c> — so what needs proving here is narrow: the generated face
    /// reads as English, and the parser recovers the exact flag rather than just noticing a filter
    /// was present.
    /// </summary>
    [TestFixture]
    public sealed class NegatedFilterTests
    {
        [TestCase(false, "Destroy a Construct.")]
        [TestCase(true, "Destroy a non-Construct.")]
        public void NegateTribe_Reads_As_A_Non_Compound_And_Round_Trips(bool negate, string expectedText)
        {
            CardDefinition card = Card(new DestroyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1),
                filter: new TargetFilterDef(tribe: Tribe.CONSTRUCT, negateTribe: negate))));

            Assert.That(card.Text, Is.EqualTo(expectedText));
            Assert.That(CardTextParser.TryParse(card, out IReadOnlyList<RuleDefinition> rules, out string reason),
                Is.True, $"refused: {reason}");
            Assert.That(CardTextGenerator.Generate(rules, CardTextGenerator.NoPool.Instance), Is.EqualTo(card.Text));

            TargetFilterDef? filter = RuleSemantics.TargetOf(rules[0].Action)?.Filter;
            Assert.That(filter?.Tribe, Is.EqualTo(Tribe.CONSTRUCT));
            Assert.That(filter?.NegateTribe, Is.EqualTo(negate));
        }

        [TestCase(false, "or less")]
        [TestCase(true, "or more")]
        public void NegateCost_Flips_The_Trailing_Clause_And_Round_Trips(bool negate, string expectedTail)
        {
            CardDefinition card = Card(new DestroyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1),
                filter: new TargetFilterDef(maxCost: 4, negateCost: negate))));

            Assert.That(card.Text, Does.EndWith($"costs 4 {expectedTail}."));
            Assert.That(CardTextParser.TryParse(card, out IReadOnlyList<RuleDefinition> rules, out string reason),
                Is.True, $"refused: {reason}");
            Assert.That(CardTextGenerator.Generate(rules, CardTextGenerator.NoPool.Instance), Is.EqualTo(card.Text));

            TargetFilterDef? filter = RuleSemantics.TargetOf(rules[0].Action)?.Filter;
            Assert.That(filter?.MaxCost, Is.EqualTo(4));
            Assert.That(filter?.NegateCost, Is.EqualTo(negate));
        }

        [TestCase(false, "Destroy a Shroud minion.")]
        [TestCase(true, "Destroy a non-Shroud minion.")]
        public void NegateKeyword_Reads_As_A_Non_Compound_And_Round_Trips(bool negate, string expectedText)
        {
            CardDefinition card = Card(new DestroyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1),
                filter: new TargetFilterDef(keyword: Keyword.SHROUD, negateKeyword: negate))));

            Assert.That(card.Text, Is.EqualTo(expectedText));
            Assert.That(CardTextParser.TryParse(card, out IReadOnlyList<RuleDefinition> rules, out string reason),
                Is.True, $"refused: {reason}");
            Assert.That(CardTextGenerator.Generate(rules, CardTextGenerator.NoPool.Instance), Is.EqualTo(card.Text));

            TargetFilterDef? filter = RuleSemantics.TargetOf(rules[0].Action)?.Filter;
            Assert.That(filter?.Keyword, Is.EqualTo(Keyword.SHROUD));
            Assert.That(filter?.NegateKeyword, Is.EqualTo(negate));
        }

        // ── Fixture plumbing ─────────────────────────────────────────────────

        private static CardDefinition Card(RuleAction action)
        {
            var rules = new[]
            {
                new RuleDefinition(new RuleId("rule_1"), new TriggeredActivation(Trigger.ON_CAST),
                    AlwaysCondition.Instance, OneShotLifetime.Instance, action),
            };
            return TestCards.Authored(new CardDefinition(
                id: FixtureCard.Id("t_negated_filter"), handle: "t_negated_filter", name: "Negated Filter",
                type: CardType.SPELL, cost: 3, attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: rules,
                flavorText: "", artId: "t_negated_filter", setId: SetId.CORE, collectible: false,
                tribe: null, factionId: ""));
        }
    }
}
