using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class TypedRuleValidatorTests
    {
        [Test]
        public void GainArmor_Is_A_Valid_HeroPower_Action()
        {
            CardDefinition card = Card(CardType.HERO_POWER,
                Rule("gain_armor", Trigger.ON_CAST, OneShotLifetime.Instance,
                    new GainArmorAction(4)));

            ValidationReport report = Validate(card);

            Assert.That(report.Ok, Is.True,
                string.Join("; ", report.Issues.Select(i => i.Code + ": " + i.Message)));
        }

        [Test]
        public void Rule_Ids_Must_Be_CardScoped_And_Unique()
        {
            CardDefinition card = Card(CardType.SPELL,
                Rule("Duplicate", Trigger.ON_CAST, OneShotLifetime.Instance, new DrawAction(1)),
                Rule("Duplicate", Trigger.ON_CAST, OneShotLifetime.Instance, new DrawAction(1)));

            ValidationReport report = Validate(card);

            Assert.That(report.Issues.Any(i => i.Code == "C9-RULE-ID-FORMAT"), Is.True);
            Assert.That(report.Issues.Any(i => i.Code == "C9-RULE-ID-DUPLICATE"), Is.True);
        }

        [Test]
        public void Default_RuleId_Is_A_Validation_Error_Not_An_Exception()
        {
            var rule = new RuleDefinition(default, new TriggeredActivation(Trigger.ON_CAST),
                AlwaysCondition.Instance, OneShotLifetime.Instance, new DrawAction(1));
            CardDefinition card = Card(CardType.SPELL, rule);

            ValidationReport report = Validate(card);

            Assert.That(report.Issues.Any(i => i.Code == "C9-RULE-ID-FORMAT"), Is.True);
        }

        [TestCase(Trigger.ON_CAST, true)]
        [TestCase(Trigger.ON_PLAY, true)]
        [TestCase(Trigger.ON_DEATH, false)]
        [TestCase(Trigger.ON_TURN_END, false)]
        public void Triggered_Lifetime_Must_Match_Activation_Shape(Trigger trigger, bool oneShot)
        {
            RuleLifetime wrong = oneShot
                ? (RuleLifetime)SourceBoundLifetime.Instance
                : OneShotLifetime.Instance;
            CardType type = trigger == Trigger.ON_CAST ? CardType.SPELL : CardType.MINION;
            CardDefinition card = Card(type, Rule("wrong_lifetime", trigger, wrong, new DrawAction(1)));

            ValidationReport report = Validate(card);

            Assert.That(report.Issues.Any(i => i.Code == "C23-LIFETIME-ACTIVATION"), Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Ongoing_Lifetimes_Are_Rejected_Until_Their_Runtime_Slice_Lands(bool finite)
        {
            RuleLifetime lifetime = finite
                ? (RuleLifetime)new FiniteLifetime(2)
                : PermanentLifetime.Instance;
            CardDefinition card = Card(CardType.MINION,
                Rule("ongoing", Trigger.ON_DEATH, lifetime, new DrawAction(1)));

            ValidationReport report = Validate(card);

            Assert.That(report.Issues.Any(i => i.Code == "C23-LIFETIME-UNSUPPORTED"), Is.True);
        }

        [TestCase("ANY_MINION", false)]
        [TestCase("ENEMY_WEAPON", false)]
        [TestCase("ENEMY_HERO", true)]
        public void TakeControl_Targets_Exactly_Minions_And_Weapons(string selector, bool invalid)
        {
            CardDefinition card = Card(CardType.SPELL,
                Rule("take_control", Trigger.ON_CAST, OneShotLifetime.Instance,
                    new TakeControlAction(new TargetSpec(selector, TargetChosenBy.PLAYER))));

            ValidationReport report = Validate(card);

            Assert.That(report.Issues.Any(i => i.Code == "C14F-ACTION-TARGET-INCOMPATIBLE"),
                Is.EqualTo(invalid));
        }

        private static ValidationReport Validate(CardDefinition card)
            => new CardValidator().Validate(card, new JsonCardDatabaseTestDouble(new[] { card }));

        private static RuleDefinition Rule(string id, Trigger trigger, RuleLifetime lifetime,
            RuleAction action)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(trigger),
                trigger == Trigger.ON_TURN_START
                    ? (RuleCondition)new TurnOwnerCondition(TurnOwner.ANY)
                    : AlwaysCondition.Instance,
                lifetime, action);

        private static CardDefinition Card(CardType type, params RuleDefinition[] rules)
            => new CardDefinition(
                id: "typed_validator", name: "Typed Validator", type: type, cost: 1,
                attack: type == CardType.MINION || type == CardType.WEAPON ? 1 : (int?)null,
                health: type == CardType.MINION ? 1 : (int?)null,
                durability: type == CardType.WEAPON ? 1 : (int?)null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: rules,
                cardText: "", flavorText: "", artId: "art/typed_validator", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);
    }
}
