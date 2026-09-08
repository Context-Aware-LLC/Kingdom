using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Targeting
{
    /// <summary>
    /// <c>TargetSelection.AutomaticOf</c> — the third disposition alongside a player-chosen count and
    /// <c>RandomOf</c>: nobody picks, nothing is rolled, the engine takes candidates deterministically.
    /// It exists because a deck or a graveyard has no <c>MaxCandidates</c>, so a fixed count there can
    /// never become "every candidate" and was therefore always a player choice -- exactly the gap that
    /// let Mass Rebirth's "reanimate 7" explode into 19-choose-7 ordered permutations in
    /// <c>LegalActionEnumerator</c> (Sam, 2026-09-08).
    /// </summary>
    [TestFixture]
    public sealed class AutomaticSelectionTests
    {
        [Test]
        public void Auto_Term_Round_Trips_And_Is_Not_Random()
        {
            Assert.That(TargetSelection.TryParse("auto: 7", out TargetSelection selection), Is.True);
            Assert.That(selection.Count, Is.EqualTo(7));
            Assert.That(selection.IsAutomatic, Is.True);
            Assert.That(selection.IsRandom, Is.False);
            Assert.That(selection.ToString(), Is.EqualTo("auto: 7"));
            Assert.That(selection, Is.EqualTo(TargetSelection.AutomaticOf(7)));
        }

        [Test]
        public void Automatic_Selection_Is_Never_Player_Chosen()
        {
            var spec = new TargetSpec("ANY_GRAVEYARD_MINION", TargetSelection.AutomaticOf(7));
            Assert.That(TargetSpec.IsPlayerChosen(spec), Is.False);
        }

        [Test]
        public void C31_Refuses_Automatic_On_A_Bounded_Selector()
        {
            var def = Card(new ReanimateAction(new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.AutomaticOf(2))));
            var report = new CardValidator().Validate(def, new FakeCardDatabase().Register(def));

            Assert.That(report.Issues.Any(i => i.Code == "C31-AUTOMATIC-ON-BOUNDED-SELECTOR"), Is.True);
        }

        [Test]
        public void C31_Allows_Automatic_On_An_Unbounded_Selector()
        {
            var def = Card(new ReanimateAction(new TargetSpec("ANY_GRAVEYARD_MINION", TargetSelection.AutomaticOf(7))));
            var report = new CardValidator().Validate(def, new FakeCardDatabase().Register(def));

            Assert.That(report.Issues.Any(i => i.Code == "C31-AUTOMATIC-ON-BOUNDED-SELECTOR"), Is.False);
        }

        private static CardDefinition Card(RuleAction action)
        {
            var rules = new[]
            {
                new RuleDefinition(new RuleId("rule_1"), new TriggeredActivation(Trigger.ON_CAST),
                    AlwaysCondition.Instance, OneShotLifetime.Instance, action),
            };
            return new CardDefinition(
                id: FixtureCard.Id("t_automatic_selection"), handle: "t_automatic_selection",
                name: "Automatic Selection", type: CardType.SPELL, cost: 3, attack: null, health: null,
                durability: null, @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), rules: rules,
                flavorText: "", artId: "t_automatic_selection", setId: SetId.CORE, collectible: false,
                tribe: null, factionId: "");
        }
    }
}
