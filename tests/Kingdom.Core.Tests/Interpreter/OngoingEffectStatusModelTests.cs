using System;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class OngoingEffectStatusModelTests
    {
        [Test]
        public void Status_Rows_Show_Source_Name_Owner_And_Remaining_Lifetime_To_Both_Seats()
        {
            var p0 = new PlayerId("p0");
            var p1 = new PlayerId("p1");
            var state = new GameState(new MatchId("status"), new DeterministicRandom(1),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)));
            state.OngoingEffects.Add(OngoingEffectInstance.CreateFinite(new InstanceId(3), p0,
                FixtureCard.Id("finite_source"), "private_rule", 2));
            state.OngoingEffects.Add(OngoingEffectInstance.CreatePermanent(new InstanceId(4), p1,
                FixtureCard.Id("permanent_source"), "private_rule"));
            var db = new FakeCardDatabase()
                .Register(Spell(FixtureCard.Id("finite_source"), "Lingering Flame"))
                .Register(Spell(FixtureCard.Id("permanent_source"), "Endless Rain"));

            var forP0 = OngoingEffectStatusModel.Build(state, db, p0);
            var forP1 = OngoingEffectStatusModel.Build(state, db, p1);

            Assert.Multiple(() =>
            {
                Assert.That((forP0[0].SourceName, forP0[0].Owner, forP0[0].Remaining),
                    Is.EqualTo(("Lingering Flame", "You", "2 triggers remaining")));
                Assert.That((forP0[1].SourceName, forP0[1].Owner, forP0[1].Remaining),
                    Is.EqualTo(("Endless Rain", "Opponent", "Permanent")));
                Assert.That(forP1[0].Owner, Is.EqualTo("Opponent"));
                Assert.That(forP1[1].Owner, Is.EqualTo("You"));
            });
        }

        private static CardDefinition Spell(int id, string name)
            => new CardDefinition(id, FixtureCard.Handle(id), name, CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE,
                false, null, "");
    }
}
