using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Session;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class HeroPowerBeatTests
    {
        [TestCase(0)]
        [TestCase(1)]
        public void Blueprint_keeps_deck_delivery_without_damage_targets(int seat)
        {
            GameState state = State(CardClass.CLOCKWRIGHT);
            InstanceId hero = state.Players[seat].Hero.Id;
            var events = new[] { Used(hero) };
            var holds = new HeroPowerHolds(0.35f, 0.45f, clockwrightBlueprint: 1.05f);

            HeroPowerBeat? result = HeroPowerBeatClassifier.Classify(state, events, holds);

            Assert.That(result.HasValue, Is.True);
            Assert.That(result!.Value.Delivery, Is.EqualTo(HeroPowerDelivery.ClockwrightBlueprint));
            Assert.That(result.Value.Hero, Is.EqualTo(hero));
            Assert.That(result.Value.Targets, Is.Empty);
            Assert.That(result.Value.HoldSeconds, Is.EqualTo(1.05f));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void Quicken_at_full_health_still_plays_its_portrait_cast(int seat)
        {
            GameState state = State(CardClass.VERDANT);
            InstanceId hero = state.Players[seat].Hero.Id;
            HeroPowerBeat? result = HeroPowerBeatClassifier.Classify(state,
                new[] { Used(hero) }, new HeroPowerHolds(0.35f, 0.45f));

            Assert.That(result.HasValue, Is.True);
            Assert.That(result!.Value.Delivery, Is.EqualTo(HeroPowerDelivery.None));
            Assert.That(result.Value.Hero, Is.EqualTo(hero));
            Assert.That(result.Value.HoldSeconds, Is.EqualTo(0.35f));
            Assert.That(state.Players[seat].Hero.CurrentHealth, Is.EqualTo(30));
        }

        [Test]
        public void Ordinary_card_draw_does_not_trigger_a_blueprint_cast()
        {
            GameState state = State(CardClass.CLOCKWRIGHT);
            var draw = new GameEvent(GameEventKind.CardDrawn, 1, state.Players[0].Hero.Id,
                new InstanceId(3), null, EventFlags.None);
            Assert.That(HeroPowerBeatClassifier.Classify(state, new[] { draw },
                new HeroPowerHolds(0.35f, 0.45f, clockwrightBlueprint: 1.05f)), Is.Null);
        }

        private static GameEvent Used(InstanceId hero)
            => new GameEvent(GameEventKind.HeroPowerUsed, 1, hero, null, null, EventFlags.None);

        private static GameState State(CardClass heroClass)
        {
            var p0 = new PlayerId("p0");
            var p1 = new PlayerId("p1");
            return new GameState(new MatchId("hero-power-beats"), new DeterministicRandom(7),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, heroClass, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, heroClass, 30)));
        }
    }
}
