using System;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Effects
{
    [TestFixture]
    [Category("Unit")]
    public sealed class ArcaneSurgeTextTests
    {
        [Test]
        public void Effective_spell_text_adds_the_live_bonus_to_every_damage_action()
        {
            CardDefinition spell = DamageCard(CardType.SPELL,
                new DamageAction(3, Target("ANY_MINION")),
                new DamageAction(2, Target("FRIENDLY_HERO")));

            string text = CardTextGenerator.GenerateEffective(spell,
                CardTextGenerator.NoPool.Instance, spellDamageBonus: 2);

            Assert.That(text, Is.EqualTo(
                "Deal 5 damage to a minion and deal 4 damage to your hero."));
        }

        [Test]
        public void Effective_text_keeps_authored_copy_without_an_active_spell_bonus()
        {
            CardDefinition spell = DamageCard(CardType.SPELL,
                new DamageAction(3, Target("ANY_MINION")));

            Assert.That(CardTextGenerator.GenerateEffective(spell,
                CardTextGenerator.NoPool.Instance, spellDamageBonus: 0),
                Is.EqualTo(spell.Text));
        }

        [Test]
        public void Effective_text_never_applies_arcane_surge_to_a_non_spell()
        {
            CardDefinition minion = DamageCard(CardType.MINION,
                new DamageAction(3, Target("ENEMY_HERO")));

            Assert.That(CardTextGenerator.GenerateEffective(minion,
                CardTextGenerator.NoPool.Instance, spellDamageBonus: 2),
                Is.EqualTo(minion.Text));
        }

        private static CardDefinition DamageCard(CardType type, params RuleAction[] actions)
        {
            var rules = new RuleDefinition[actions.Length];
            for (int i = 0; i < actions.Length; i++)
                rules[i] = new RuleDefinition(new RuleId("damage_" + i),
                    new TriggeredActivation(type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY),
                    AlwaysCondition.Instance, OneShotLifetime.Instance, actions[i]);

            int id = FixtureCard.Id("arcane_surge_text_" + type);
            return new CardDefinition(id, FixtureCard.Handle(id), "Arcane Surge Text", type, 1,
                type == CardType.MINION ? 1 : null,
                type == CardType.MINION ? 1 : null,
                null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules,
                "", "art/arcane_surge_text", SetId.CORE, true, null, "",
                text: "AUTHORED TEXT");
        }

        private static TargetSpec Target(string selector)
            => new TargetSpec(selector, TargetSelection.Of(1));
    }
}
