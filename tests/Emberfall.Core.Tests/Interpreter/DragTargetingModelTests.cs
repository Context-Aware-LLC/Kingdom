using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using Emberfall.Presentation;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class DragTargetingModelTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void Choice_Plan_Collects_PerBinding_Counts_And_Emits_Bound_TargetRefs()
        {
            var body = Minion("body");
            var spell = Spell("split", new[]
            {
                TestRules.Rule("first_hit", Trigger.ON_CAST,
                    new DamageAction(1, new TargetSpec("ANY_CHARACTER", TargetChosenBy.PLAYER,
                        count: 2, binding: "first"))),
                TestRules.Rule("second_hit", Trigger.ON_CAST,
                    new DamageAction(1, new TargetSpec("ANY_CHARACTER", TargetChosenBy.PLAYER,
                        binding: "second", distinctFrom: "first"))),
            });
            var db = new FakeCardDatabase().Register(body).Register(spell);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), spell.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            CardInstance friendly = Board(state, 0, body);
            CardInstance enemy = Board(state, 1, body);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            PlayerIntent first = model.Resolve(source, PointerSubject.OfCard(enemy.Id, P1, Zone.Board));

            Assert.That(first, Is.Null);
            Assert.That(model.Phase, Is.EqualTo(DragTargetingModel.TargetingPhase.AwaitingEffectTarget));
            Assert.That((model.AwaitingBinding, model.AwaitingSelectedCount, model.AwaitingRequiredCount),
                Is.EqualTo(("first", 1, 2)));
            Assert.That(model.AwaitingTargets(), Does.Not.Contain(enemy.Id));

            PlayerIntent second = model.Resolve(DragSource.None,
                PointerSubject.OfCard(friendly.Id, P0, Zone.Board));

            Assert.That(second, Is.Null);
            Assert.That((model.AwaitingBinding, model.AwaitingSelectedCount, model.AwaitingRequiredCount),
                Is.EqualTo(("second", 0, 1)));
            Assert.That(model.AwaitingTargets(), Does.Not.Contain(enemy.Id));
            Assert.That(model.AwaitingTargets(), Does.Not.Contain(friendly.Id));

            var completed = model.Resolve(DragSource.None,
                PointerSubject.OfHero(state.Players[1].Hero.Id, P1)) as PlayCardIntent;

            Assert.That(completed, Is.Not.Null);
            Assert.That(model.Phase, Is.EqualTo(DragTargetingModel.TargetingPhase.Direct));
            Assert.That(completed!.Targets.Select(t => (t.Binding, t.Instance)), Is.EqualTo(new[]
            {
                ("first", enemy.Id),
                ("first", friendly.Id),
                ("second", state.Players[1].Hero.Id),
            }));
        }

        private static GameState State()
        {
            var state = new GameState(new MatchId("drag-target-plan"), new DeterministicRandom(1),
                new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.NIGHTVEIL, 30)),
                new PlayerState(P1, new HeroInstance(new InstanceId(2), P1, CardClass.TEMPEST, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = P0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            state.Players[0].Mana.Available = state.Players[0].Mana.Max = 10;
            return state;
        }

        private static CardInstance Board(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                CurrentAttack = 1,
                CurrentHealth = 3,
                MaxHealth = 3,
                Position = state.Players[seat].Board.Count,
            };
            state.Players[seat].Board.Add(card);
            return card;
        }

        private static CardDefinition Minion(string id)
            => new CardDefinition(id, id, CardType.MINION, 1, 1, 3, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                System.Array.Empty<RuleDefinition>(), "", "art/" + id, SetId.CORE,
                true, null, "");

        private static CardDefinition Spell(string id, RuleDefinition[] rules)
            => new CardDefinition(id, id, CardType.SPELL, 1, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(), rules, "", "art/" + id, SetId.CORE, true, null, "");
    }
}
