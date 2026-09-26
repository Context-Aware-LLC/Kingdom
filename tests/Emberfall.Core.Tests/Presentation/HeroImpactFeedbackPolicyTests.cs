using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class HeroImpactFeedbackPolicyTests
    {
        private static readonly PlayerId Local = new PlayerId("local");
        private static readonly PlayerId Remote = new PlayerId("remote");

        [Test]
        public void Local_hero_damage_requests_one_haptic_per_positive_damage_event()
        {
            GameState state = CreateState();
            var events = new List<GameEvent>
            {
                Event(GameEventKind.DamageApplied, state.Players[0].Hero.Id, 3),
                Event(GameEventKind.FatigueDamage, state.Players[0].Hero.Id, 2),
            };

            HeroImpactFeedbackDecision decision =
                HeroImpactFeedbackPolicy.Evaluate(state, Local, events, false);

            Assert.That(decision.DamagePulseCount, Is.EqualTo(2));
            Assert.That(decision.ShakeForHeroDeath, Is.False);
        }

        [Test]
        public void Remote_minion_and_zero_damage_do_not_vibrate_the_local_player()
        {
            GameState state = CreateState();
            var events = new List<GameEvent>
            {
                Event(GameEventKind.DamageApplied, state.Players[1].Hero.Id, 4),
                Event(GameEventKind.DamageApplied, new InstanceId(99), 4),
                Event(GameEventKind.DamageApplied, state.Players[0].Hero.Id, 0),
            };

            HeroImpactFeedbackDecision decision =
                HeroImpactFeedbackPolicy.Evaluate(state, Local, events, false);

            Assert.That(decision.DamagePulseCount, Is.Zero);
        }

        [Test]
        public void Lethal_match_end_requests_one_screen_shake_for_either_hero()
        {
            GameState state = CreateState();
            state.Players[1].Hero.CurrentHealth = 0;
            var events = new[]
            {
                Event(GameEventKind.MatchEnded, null, null),
            };

            HeroImpactFeedbackDecision decision =
                HeroImpactFeedbackPolicy.Evaluate(state, Local, events, false);

            Assert.That(decision.ShakeForHeroDeath, Is.True);
        }

        [Test]
        public void Concede_and_already_played_death_do_not_request_another_shake()
        {
            GameState conceded = CreateState();
            var events = new[] { Event(GameEventKind.MatchEnded, null, null) };

            HeroImpactFeedbackDecision concedeDecision =
                HeroImpactFeedbackPolicy.Evaluate(conceded, Local, events, false);
            conceded.Players[0].Hero.CurrentHealth = -1;
            HeroImpactFeedbackDecision repeatedDecision =
                HeroImpactFeedbackPolicy.Evaluate(conceded, Local, events, true);

            Assert.That(concedeDecision.ShakeForHeroDeath, Is.False);
            Assert.That(repeatedDecision.ShakeForHeroDeath, Is.False);
        }

        private static GameState CreateState()
        {
            var localHero = new HeroInstance(new InstanceId(1), Local, CardClass.EMBERWARD, 30);
            var remoteHero = new HeroInstance(new InstanceId(2), Remote, CardClass.TIDECALLER, 30);
            return new GameState(new MatchId("impact-feedback"), new DeterministicRandom(7),
                new PlayerState(Local, localHero), new PlayerState(Remote, remoteHero));
        }

        private static GameEvent Event(GameEventKind kind, InstanceId? target, int? amount)
            => new GameEvent(kind, 1, null, target, amount, EventFlags.None);
    }
}
