using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using Emberfall.Editor;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class CardAuthoringRuleRoundTripTests
    {
        [Test]
        public void Every_Corpus_Row_RoundTrips_Through_Editable_Typed_Rules()
        {
            string[] files =
            {
                Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", "M1.cards.json"),
                Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", "M2.cards.json"),
                Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData", "M3.cards.json"),
            };
            var cards = files.SelectMany(path => CardJson.DeserializeArray(File.ReadAllText(path))).ToList();

            Assert.That(cards, Has.Count.EqualTo(260));
            foreach (var card in cards)
            {
                string before = CardJsonRowIO.Serialize(card);
                string after = CardJsonRowIO.Serialize(EditableCard.FromDefinition(card).ToDefinition());
                Assert.That(after, Is.EqualTo(before), card.Id);
                Assert.That(after, Does.Not.Contain("\"effects\""), card.Id);
                Assert.DoesNotThrow(() => CardJson.Deserialize(after), card.Id);
            }
        }

        [Test]
        public void Writer_Covers_Every_Typed_Action_And_Rule_Node()
        {
            var target = new TargetSpec("ANY_MINION", TargetChosenBy.RANDOM, 2,
                new TargetFilterDef(true, Tribe.CONSTRUCT), "chosen", "prior");
            var actions = new RuleAction[]
            {
                new DamageAction(2, target), new HealAction(2, target),
                new ModifyStatsAction(1, 2, target), new ReinforceAction(target),
                new GrantKeywordAction(Keyword.BULWARK, target), new DestroyAction(target),
                new ReturnToHandAction(target), new SilenceAction(target),
                new TransformAction("token", target), new FrostAction(target),
                new TakeControlAction(target), new DrawAction(2), new SummonAction("token", 2),
                new AddToHandAction("token", 2), new EquipAction("weapon"),
                new GainManaAction(2, ManaGainKind.PERMANENT_EMPTY), new BackdraftAction(2),
                new UnearthAction(2), new GainArmorAction(2),
            };
            var rules = new List<RuleDefinition>();
            for (int i = 0; i < actions.Length; i++)
                rules.Add(new RuleDefinition(new RuleId($"rule_{i + 1}"),
                    i == 0 ? (RuleActivation)AuraActivation.Instance : new TriggeredActivation(Trigger.ON_CAST),
                    i == 1 ? (RuleCondition)new TurnOwnerCondition(TurnOwner.ANY) : AlwaysCondition.Instance,
                    i == 2 ? (RuleLifetime)new FiniteLifetime(3)
                        : i == 3 ? PermanentLifetime.Instance
                        : i == 0 ? SourceBoundLifetime.Instance
                        : OneShotLifetime.Instance,
                    actions[i]));
            var card = new CardDefinition("all_nodes", "All Nodes", CardType.SPELL, 1,
                null, null, null, CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "art/all_nodes", SetId.CORE,
                false, null, "");

            string serialized = CardJsonRowIO.Serialize(EditableCard.FromDefinition(card).ToDefinition());
            CardDefinition reparsed = CardJson.Deserialize(serialized);

            Assert.That(reparsed.Rules, Has.Count.EqualTo(actions.Length));
            Assert.That(CardJsonRowIO.Serialize(reparsed), Is.EqualTo(serialized));
            Assert.That(reparsed.Rules.Select(rule => rule.Action.GetType()),
                Is.EqualTo(actions.Select(action => action.GetType())));
        }
    }
}
