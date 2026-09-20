using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Tests.Support;
using Emberfall.Editor;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Editor
{
    [TestFixture]
    public sealed class EditableTargetTests
    {
        [TestCase("1")]
        [TestCase("random: 2")]
        [TestCase("auto: 7")]
        public void Selection_mode_survives_the_authoring_round_trip(string term)
        {
            var original = new TargetSpec(
                "ANY_GRAVEYARD_MINION",
                TargetSelection.Parse(term));

            TargetSpec roundTripped = EditableTarget.FromDefinition(original).ToDefinition();

            Assert.That(roundTripped.Selection, Is.EqualTo(original.Selection));
        }

        [Test]
        public void Shipped_Mass_Rebirth_survives_the_full_card_authoring_round_trip()
        {
            CardDefinition original = ShippedCards.Cards.Single(card => card.Id == 209);

            CardDefinition roundTripped = EditableCard.FromDefinition(original).ToDefinition();
            var action = (ReanimateAction)roundTripped.Rules.Single().Action;

            Assert.That(action.Target.Selection, Is.EqualTo(TargetSelection.AutomaticOf(7)));
        }
    }
}
