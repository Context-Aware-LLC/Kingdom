using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using NUnit.Framework;
using Verb = Emberfall.Core.Contracts.Action;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class RuleModelAdapterTests
    {
        [Test]
        public void Legacy_Effect_Maps_To_Typed_Nodes_And_RoundTrips_Without_Behavior_Loss()
        {
            var target = new TargetSpec("ANY_FRIENDLY_MINION", TargetChosenBy.RANDOM, count: 2,
                filter: new TargetFilterDef(damaged: true, tribe: Tribe.CONSTRUCT),
                binding: "reinforced", distinctFrom: "first");
            var effect = new EffectDef(Trigger.ON_TURN_START, Verb.BUFF, amount: 2, amount2: 3,
                tokenId: null, grantedKeyword: null,
                condition: new EffectConditionDef(ConditionType.TURN_OWNER, TurnOwner.CONTROLLER),
                target: target);

            RuleDefinition rule = RuleRuntimeAdapter.FromLegacy(new RuleId("opening_repair"), effect);

            Assert.That(rule.Id.Value, Is.EqualTo("opening_repair"));
            Assert.That(((TriggeredActivation)rule.Activation).Trigger, Is.EqualTo(Trigger.ON_TURN_START));
            Assert.That(((TurnOwnerCondition)rule.Condition).Owner, Is.EqualTo(TurnOwner.CONTROLLER));
            Assert.That(rule.Lifetime, Is.TypeOf<SourceBoundLifetime>());
            var action = (ModifyStatsAction)rule.Action;
            Assert.That((action.AttackDelta, action.HealthDelta, action.Target),
                Is.EqualTo((2, 3, target)));
            EffectDef roundTrip = RuleRuntimeAdapter.ToLegacy(rule);

            Assert.That((roundTrip.Trigger, roundTrip.Action, roundTrip.Amount, roundTrip.Amount2),
                Is.EqualTo((effect.Trigger, effect.Action, effect.Amount, effect.Amount2)));
            Assert.That(roundTrip.Condition!.TurnOwner, Is.EqualTo(TurnOwner.CONTROLLER));
            Assert.That(roundTrip.Target, Is.SameAs(target));
        }

        [Test]
        public void Typed_Card_Constructor_Exposes_Rules_And_Projects_A_Legacy_Runtime_View()
        {
            var rules = new RuleDefinition[]
            {
                new RuleDefinition(new RuleId("cast_damage"),
                    new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                    OneShotLifetime.Instance,
                    new DamageAction(3, new TargetSpec("ANY_CHARACTER"))),
                new RuleDefinition(new RuleId("draw_followup"),
                    new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                    OneShotLifetime.Instance, new DrawAction(1)),
            };

            CardDefinition card = Card(rules);

            Assert.That(card.Rules, Is.SameAs(rules));
            Assert.That(card.Rules.Select(r => r.Id.Value),
                Is.EqualTo(new[] { "cast_damage", "draw_followup" }));
            Assert.That(card.Effects.Select(e => e.Action),
                Is.EqualTo(new[] { Verb.DEAL_DAMAGE, Verb.DRAW }));
            Assert.That(card.Effects[0].Amount, Is.EqualTo(3));
            Assert.That(card.Effects[0].Selector, Is.EqualTo("ANY_CHARACTER"));
        }

        [Test]
        public void Legacy_Card_Constructor_Assigns_Deterministic_Scoped_Rule_Ids()
        {
            var effects = new[]
            {
                new EffectDef(Trigger.ON_CAST, Verb.DRAW, 1, null, null, null, null),
                new EffectDef(Trigger.ON_CAST, Verb.BACKDRAFT, 2, null, null, null, null),
            };

            var card = new CardDefinition("legacy", "Legacy", CardType.SPELL, 1,
                null, null, null, CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), effects, "", "", "art/legacy", SetId.CORE,
                true, null, "", false);

            Assert.That(card.Rules.Select(r => r.Id.Value),
                Is.EqualTo(new[] { "rule_1", "rule_2" }));
            Assert.That(card.Rules[0].Action, Is.TypeOf<DrawAction>());
            Assert.That(card.Rules[1].Action, Is.TypeOf<BackdraftAction>());
        }

        [Test]
        public void Take_Control_Is_Typed_But_Fails_Closed_At_The_Legacy_Runtime_Boundary()
        {
            var rule = new RuleDefinition(new RuleId("seize_weapon"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance,
                new TakeControlAction(new TargetSpec("ENEMY_WEAPON")));

            var ex = Assert.Throws<System.InvalidOperationException>(
                () => RuleRuntimeAdapter.ToLegacy(rule));

            Assert.That(ex!.Message, Does.Contain(nameof(TakeControlAction)));
        }

        [Test]
        public void Normalized_Rule_Surface_Has_No_Presentation_Or_Vfx_Node()
        {
            Assert.Multiple(() =>
            {
                Assert.That(typeof(RuleDefinition).GetProperty("Vfx"), Is.Null);
                Assert.That(typeof(RuleDefinition).Assembly.GetType("Emberfall.Core.Cards.RuleVfx"), Is.Null);
            });
        }

        private static CardDefinition Card(RuleDefinition[] rules)
            => new CardDefinition("typed", "Typed", CardType.SPELL, 1,
                null, null, null, CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "", "art/typed", SetId.CORE,
                true, null, "", false);
    }
}
