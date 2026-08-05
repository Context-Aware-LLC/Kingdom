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

            Assert.That(CardTextGenerator.Generate(mainspring), Is.EqualTo("[Toll] Draw a card."));
            Assert.That(mainspring.Keywords, Is.Empty);
        }

        [Test]
        public void Card_Rendering_Includes_Keyword_Only_Carrier_Behaviour()
        {
            var card = new CardDefinition("keyword_only", "Keyword Only", CardType.MINION, 1,
                1, 1, null, CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.BULWARK, Keyword.LIFEDRAIN }, Array.Empty<RuleDefinition>(),
                "", "art/keyword_only", SetId.CORE, true, null, "", false);

            Assert.That(CardTextGenerator.Generate(card), Is.EqualTo("[Bulwark] [Lifedrain]"));
        }

        [Test]
        public void Card_Rendering_Derives_Trigger_Markers_Without_Duplicating_Them()
        {
            var card = new CardDefinition("herald", "Herald", CardType.MINION, 1,
                1, 1, null, CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.BULWARK }, new[] { DrawRule(new TriggeredActivation(Trigger.ON_PLAY)) },
                "", "art/herald", SetId.CORE, true, null, "", false);

            Assert.That(CardTextGenerator.Generate(card), Is.EqualTo("[Bulwark] [Herald] Draw a card."));
        }

        [Test]
        public void Strict_Json_Rejects_The_Removed_CardText_Property()
        {
            var ex = Assert.Throws<FormatException>(() => CardJson.Deserialize(
                "{\"id\":\"removed_text\",\"type\":\"SPELL\",\"cost\":0,\"cardText\":\"legacy\"}"));

            Assert.That(ex!.Message, Does.Contain("cardText"));
        }

        [TestCase(Trigger.ON_PLAY, "[Herald] Draw a card.")]
        [TestCase(Trigger.ON_DEATH, "[Toll] Draw a card.")]
        [TestCase(Trigger.ON_ATTACK, "[Attack] Draw a card.")]
        [TestCase(Trigger.ON_DAMAGED, "[Damaged] Draw a card.")]
        [TestCase(Trigger.ON_HEAL, "[Healed] Draw a card.")]
        [TestCase(Trigger.ON_TURN_START, "[Turn Start] Draw a card.")]
        [TestCase(Trigger.ON_TURN_END, "[Turn End] Draw a card.")]
        [TestCase(Trigger.ON_FRIENDLY_SUMMON, "[Friendly Summon] Draw a card.")]
        [TestCase(Trigger.ON_SPELL_CAST, "[Spell Cast] Draw a card.")]
        [TestCase(Trigger.ON_CAST, "Draw a card.")]
        public void Renders_Every_Supported_Triggered_Activation(Trigger trigger, string expected)
        {
            Assert.That(CardTextGenerator.Generate(new[] { DrawRule(new TriggeredActivation(trigger)) }),
                Is.EqualTo(expected));
        }

        [Test]
        public void Renders_Aura_Activation()
        {
            Assert.That(CardTextGenerator.Generate(new[] { DrawRule(AuraActivation.Instance) }),
                Is.EqualTo("[Aura] Draw a card."));
        }

        [TestCase(TurnOwner.CONTROLLER, "While it is your turn, Draw a card.")]
        [TestCase(TurnOwner.OPPONENT, "While it is your opponent's turn, Draw a card.")]
        [TestCase(TurnOwner.ANY, "On either player's turn, Draw a card.")]
        public void Renders_Every_Turn_Owner_Condition(TurnOwner owner, string expected)
        {
            var rule = new RuleDefinition(new RuleId("turn_owner"),
                new TriggeredActivation(Trigger.ON_TURN_START), new TurnOwnerCondition(owner),
                SourceBoundLifetime.Instance, new DrawAction(1));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo("[Turn Start] " + expected));
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

        [TestCase("SELF", "Deal 1 damage to this.")]
        [TestCase("TRIGGER_SOURCE", "Deal 1 damage to the trigger source.")]
        [TestCase("ADJACENT", "Deal 1 damage to all adjacent minions.")]
        [TestCase("FRIENDLY_WEAPON", "Deal 1 damage to your weapon.")]
        [TestCase("ENEMY_WEAPON", "Deal 1 damage to the enemy weapon.")]
        [TestCase("FRIENDLY_HAND", "Deal 1 damage to all cards in your hand.")]
        [TestCase("ENEMY_HAND", "Deal 1 damage to all cards in the enemy hand.")]
        [TestCase("ANY_HAND_CARD", "Deal 1 damage to a hand card.")]
        [TestCase("FRIENDLY_DECK", "Deal 1 damage to all cards in your deck.")]
        [TestCase("ENEMY_DECK", "Deal 1 damage to all cards in the enemy deck.")]
        [TestCase("ANY_DECK_CARD", "Deal 1 damage to a deck card.")]
        [TestCase("FRIENDLY_GRAVEYARD", "Deal 1 damage to all cards in your graveyard.")]
        [TestCase("ENEMY_GRAVEYARD", "Deal 1 damage to all cards in the enemy graveyard.")]
        [TestCase("ANY_GRAVEYARD_CARD", "Deal 1 damage to a graveyard card.")]
        public void Renders_Canonical_Selector_Families(string selector, string expected)
        {
            var rule = new RuleDefinition(new RuleId("selector_" + selector),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(1, new TargetSpec(selector)));

            Assert.That(CardTextGenerator.Generate(new[] { rule }), Is.EqualTo(expected));
        }

        [Test]
        public void Renders_Target_Count_And_Distinctness()
        {
            var rule = new RuleDefinition(new RuleId("multiple_other_minions"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new DamageAction(1, new TargetSpec("ANY_MINION", count: 2, distinctFrom: "first")));

            Assert.That(CardTextGenerator.Generate(new[] { rule }),
                Is.EqualTo("Deal 1 damage to 2 other minions."));
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
        public void Fails_Explicitly_For_An_Unsupported_Trigger_Node()
        {
            var rule = new RuleDefinition(new RuleId("unknown_trigger"),
                new TriggeredActivation((Trigger)999), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DrawAction(1));

            var ex = Assert.Throws<NotSupportedException>(() => CardTextGenerator.Generate(new[] { rule }));

            Assert.That(ex!.Message, Does.Contain("999"));
        }

        [Test]
        public void Every_Live_Card_Renders_Readably_And_Deterministically()
        {
            var cards = new List<CardDefinition>();
            foreach (string file in new[] { "M1.cards.json", "M2.cards.json", "M3.cards.json" })
            {
                string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", file);
                cards.AddRange(CardJson.DeserializeArray(File.ReadAllText(path)));
            }

            int rendered = 0;
            foreach (CardDefinition card in cards)
            {
                string first = CardTextGenerator.Generate(card);
                string second = CardTextGenerator.Generate(card);

                Assert.That(first, Is.EqualTo(second), $"{card.Id} did not render deterministically.");
                if (card.Rules.Count == 0 && card.Keywords.Count == 0)
                {
                    Assert.That(first, Is.Empty, $"{card.Id} should have no generated text.");
                    rendered++;
                    continue;
                }

                Assert.That(first, Is.Not.Empty, $"{card.Id} rendered no card text.");
                Assert.That(first, Does.Match("[A-Za-z]"), $"{card.Id} did not render readable words.");
                Assert.That(first, Does.Not.Contain("_"), $"{card.Id} leaked an identifier spelling.");
                if (card.Rules.Count > 0)
                    Assert.That(first, Does.EndWith("."), $"{card.Id} did not render a complete sentence.");
                rendered++;
            }

            Assert.That(cards.Count, Is.EqualTo(260));
            Assert.That(rendered, Is.EqualTo(260));
        }

        private static RuleDefinition DrawRule(RuleActivation activation)
            => new RuleDefinition(new RuleId("draw"), activation, AlwaysCondition.Instance,
                SourceBoundLifetime.Instance, new DrawAction(1));
    }
}
