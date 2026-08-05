using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class CardTextGeneratorTests
    {
        [Test]
        public void Derives_Toll_Marker_From_Death_Trigger_Without_A_Keyword()
        {
            var rule = new RuleDefinition(new RuleId("break_draw"),
                new TriggeredActivation(Trigger.ON_DEATH), AlwaysCondition.Instance,
                SourceBoundLifetime.Instance, new DrawAction(1));

            string text = CardTextGenerator.Generate(new[] { rule });

            Assert.That(text, Is.EqualTo("[Toll] Draw a card."));
        }

        [Test]
        public void Mainspring_Halberd_Rules_Render_A_Derived_Toll_Marker()
        {
            string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", "M3.cards.json");
            CardDefinition mainspring = CardJson.DeserializeArray(File.ReadAllText(path))
                .Single(card => card.Id == "rekind_clock_mainspring_halberd");

            Assert.That(CardTextGenerator.Generate(mainspring.Rules), Is.EqualTo("[Toll] Draw a card."));
        }

        [Test]
        public void Renders_Target_Policy_And_Filter_As_Readable_Text()
        {
            var rule = new RuleDefinition(new RuleId("random_damaged"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new DamageAction(2, new TargetSpec("ANY_MINION", TargetChosenBy.RANDOM,
                    count: 1, filter: new TargetFilterDef(damaged: true))));

            Assert.That(CardTextGenerator.Generate(new[] { rule }),
                Is.EqualTo("Deal 2 damage to a random damaged minion."));
        }

        [Test]
        public void Fails_Explicitly_For_An_Unsupported_Target_Node()
        {
            var rule = new RuleDefinition(new RuleId("unknown_target"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(1, new TargetSpec("NOT_A_SELECTOR")));

            var ex = Assert.Throws<NotSupportedException>(() => CardTextGenerator.Generate(new[] { rule }));

            Assert.That(ex!.Message, Does.Contain("NOT_A_SELECTOR"));
        }

        [Test]
        public void Every_Live_Normalized_Rule_Renders_Readably_And_Deterministically()
        {
            var cards = new List<CardDefinition>();
            foreach (string file in new[] { "M1.cards.json", "M2.cards.json", "M3.cards.json" })
            {
                string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", file);
                cards.AddRange(CardJson.DeserializeArray(File.ReadAllText(path)));
            }

            int rendered = 0;
            foreach (CardDefinition card in cards.Where(card => card.Rules.Count > 0))
            {
                string first = CardTextGenerator.Generate(card.Rules);
                string second = CardTextGenerator.Generate(card.Rules);

                Assert.That(first, Is.EqualTo(second), $"{card.Id} did not render deterministically.");
                Assert.That(first, Is.Not.Empty, $"{card.Id} rendered no rule text.");
                Assert.That(first, Does.Match("[A-Za-z]"), $"{card.Id} did not render readable words.");
                Assert.That(first, Does.Not.Contain("_"), $"{card.Id} leaked an identifier spelling.");
                Assert.That(first, Does.EndWith("."), $"{card.Id} did not render a complete sentence.");
                rendered++;
            }

            Assert.That(cards.Count, Is.EqualTo(260));
            Assert.That(rendered, Is.GreaterThan(0));
        }
    }
}
