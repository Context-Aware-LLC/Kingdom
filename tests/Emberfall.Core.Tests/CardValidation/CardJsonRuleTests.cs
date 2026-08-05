using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class CardJsonRuleTests
    {
        [Test]
        public void Parses_Normalized_Targeted_Rule_Without_Flat_Payload_Sentinels()
        {
            const string json = """
                {
                  "id":"typed_damage","type":"SPELL",
                  "rules":[{
                    "id":"opening_bolt",
                    "activation":{"type":"TRIGGERED","trigger":"ON_CAST"},
                    "condition":{"type":"ALWAYS"},
                    "lifetime":{"type":"ONE_SHOT"},
                    "action":{"type":"DAMAGE","amount":3,"target":{
                      "selector":"ANY_CHARACTER","chosenBy":"PLAYER","count":1,
                      "filter":{"damaged":true},"binding":"victim"
                    }}
                  }]
                }
                """;

            CardDefinition card = CardJson.Deserialize(json);

            Assert.That(card.Rules, Has.Count.EqualTo(1));
            RuleDefinition rule = card.Rules[0];
            Assert.That(rule.Id.Value, Is.EqualTo("opening_bolt"));
            Assert.That(((TriggeredActivation)rule.Activation).Trigger, Is.EqualTo(Trigger.ON_CAST));
            Assert.That(rule.Condition, Is.SameAs(AlwaysCondition.Instance));
            Assert.That(rule.Lifetime, Is.SameAs(OneShotLifetime.Instance));
            var damage = (DamageAction)rule.Action;
            Assert.That(damage.Amount, Is.EqualTo(3));
            Assert.That(damage.Target.Selector, Is.EqualTo("ANY_CHARACTER"));
            Assert.That(damage.Target.Filter!.Damaged, Is.True);
            Assert.That(damage.Target.Binding, Is.EqualTo("victim"));
        }

        [Test]
        public void Parses_Atomic_Condition_Lifetime_And_NoTarget_Action_Nodes()
        {
            const string json = """
                {
                  "id":"typed_turn","type":"MINION","attack":1,"health":1,
                  "rules":[{
                    "id":"incoming_turn",
                    "activation":{"type":"TRIGGERED","trigger":"ON_TURN_START"},
                    "condition":{"type":"TURN_OWNER","owner":"OPPONENT"},
                    "lifetime":{"type":"SOURCE_BOUND"},
                    "action":{"type":"BACKDRAFT","amount":2}
                  }]
                }
                """;

            RuleDefinition rule = CardJson.Deserialize(json).Rules[0];

            Assert.That(((TurnOwnerCondition)rule.Condition).Owner, Is.EqualTo(TurnOwner.OPPONENT));
            Assert.That(rule.Lifetime, Is.SameAs(SourceBoundLifetime.Instance));
            Assert.That(((BackdraftAction)rule.Action).Amount, Is.EqualTo(2));
        }

        [TestCase("activation", "{\"type\":\"TRIGGERED\",\"trigger\":\"ON_CAST\",\"amount\":1}")]
        [TestCase("condition", "{\"type\":\"ALWAYS\",\"owner\":\"ANY\"}")]
        [TestCase("lifetime", "{\"type\":\"ONE_SHOT\",\"uses\":2}")]
        [TestCase("action", "{\"type\":\"DRAW\",\"target\":{\"selector\":\"ANY_MINION\"}}")]
        public void Rejects_Fields_That_Belong_To_A_Different_Typed_Node(string node, string value)
        {
            string json = "{\"id\":\"bad_rule\",\"type\":\"SPELL\",\"rules\":[{" +
                "\"id\":\"rule_1\"," +
                "\"activation\":{\"type\":\"TRIGGERED\",\"trigger\":\"ON_CAST\"}," +
                "\"condition\":{\"type\":\"ALWAYS\"}," +
                "\"lifetime\":{\"type\":\"ONE_SHOT\"}," +
                "\"action\":{\"type\":\"DRAW\",\"count\":1}," +
                $"\"{node}\":{value}" +
                "}]}";

            Assert.Throws<System.FormatException>(() => CardJson.Deserialize(json));
        }

        [Test]
        public void Rejects_Removed_Effects_Property()
        {
            const string json = """
                {"id":"legacy","type":"SPELL",
                 "effects":[{"trigger":"ON_CAST","action":"DRAW","amount":1}]}
                """;

            var ex = Assert.Throws<System.FormatException>(() => CardJson.Deserialize(json));

            Assert.That(ex!.Message, Does.Contain("Unknown card JSON property 'effects'"));
        }
    }
}
