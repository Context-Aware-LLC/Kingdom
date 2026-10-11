using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Targeting
{
    /// <summary>
    /// The negate switch on FROST (Sam, 2026-10-06) prints its own verb — "Unfreeze" — and the parser
    /// recovers the exact flag from it, the same contract <see cref="NegatedFilterTests"/> holds the
    /// filter negate bits to. The JSON side carries it as an optional <c>negate</c> boolean, absent
    /// meaning the positive Freeze every shipped row already has.
    /// </summary>
    [TestFixture]
    public sealed class UnfreezeTextTests
    {
        [TestCase(false, "Freeze a minion.")]
        [TestCase(true, "Unfreeze a minion.")]
        public void Negated_Frost_Prints_Unfreeze_And_Round_Trips(bool negate, string expectedText)
        {
            CardDefinition card = Card(new FrostAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)), negate: negate));

            Assert.That(card.Text, Is.EqualTo(expectedText));
            Assert.That(CardTextParser.TryParse(card, out IReadOnlyList<RuleDefinition> rules, out string reason),
                Is.True, $"refused: {reason}");
            Assert.That(CardTextGenerator.Generate(rules, CardTextGenerator.NoPool.Instance), Is.EqualTo(card.Text));
            Assert.That(((FrostAction)rules[0].Action).Negate, Is.EqualTo(negate));
        }

        [Test]
        public void Unfreeze_Is_Emphasised_Game_Vocabulary_Like_Freeze()
        {
            IReadOnlyList<string> words = CardTextGenerator.EmphasisVocabulary();

            Assert.That(words, Does.Contain(CardTextGenerator.FrostVerb));
            Assert.That(words, Does.Contain(CardTextGenerator.ThawVerb));
        }

        [TestCase(@"""negate"":true,", true)]
        [TestCase("", false)]
        public void Json_Negate_Is_An_Optional_Boolean_On_FROST(string negateField, bool expected)
        {
            string json = @"{""id"":1000155,""handle"":""t_unfreeze"",""name"":""Thaw"",""type"":""SPELL"",""cost"":1,"
                + @"""class"":""TIDECALLER"",""rarity"":""COMMON"",""keywords"":[],""setId"":""PENDINGHAM"",""collectible"":true,"
                + @"""artId"":""t_unfreeze"",""text"":""Unfreeze a character."",""rules"":[{""id"":""rule_1"","
                + @"""activation"":{""type"":""TRIGGERED"",""trigger"":""ON_CAST""},""condition"":{""type"":""ALWAYS""},"
                + @"""lifetime"":{""type"":""ONE_SHOT""},""action"":{""type"":""FROST""," + negateField
                + @"""target"":{""selector"":""ANY_CHARACTER"",""selection"":""1""}}}]}";

            CardDefinition card = CardJson.Deserialize(json);

            var frost = (FrostAction)card.Rules[0].Action;
            Assert.That(frost.Negate, Is.EqualTo(expected));
            Assert.That(frost.Target.Selector, Is.EqualTo("ANY_CHARACTER"));
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
                id: FixtureCard.Id("t_unfreeze_text"), handle: "t_unfreeze_text", name: "Unfreeze Text",
                type: CardType.SPELL, cost: 1, attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: rules,
                flavorText: "", artId: "t_unfreeze_text", setId: SetId.CORE, collectible: false,
                tribe: null, factionId: ""));
        }
    }
}
