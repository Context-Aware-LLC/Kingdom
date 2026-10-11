using System.Collections.Generic;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Telemetry;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// <see cref="SeatOutcome.EnemyMinionsDestroyed"/> is a count of board minions that died, and a
    /// graveyard receives far more than that: resolved spells, cards destroyed in hand, cards
    /// burned off a full hand. Each test puts one of those into a graveyard the way the engine
    /// does and asks whether it was counted.
    /// </summary>
    [TestFixture]
    public class MinionDeathCountTests
    {
        private static readonly PlayerId P0 = new PlayerId("p0");
        private static readonly PlayerId P1 = new PlayerId("p1");

        private GameState _state = null!;
        private int _nextId;

        [SetUp]
        public void SetUp()
        {
            _nextId = 10;
            _state = new GameState(
                new MatchId("m-deaths"), new DeterministicRandom(1UL),
                new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.EMBERWARD, 30)),
                new PlayerState(P1, new HeroInstance(new InstanceId(2), P1, CardClass.TIDECALLER, 30)));
        }

        [Test]
        public void A_Minion_That_Was_On_The_Board_And_Died_Is_Counted_For_Its_Owner()
        {
            CardInstance minion = Minion(P1);
            _state.Players[1].Board.Add(minion);
            var watch = MatchTelemetryRecorder.Watch(_state, new EndTurnIntent(P0));

            Die(1, _state.Players[1].Board, minion);

            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 1), Is.EqualTo(1));
            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 0), Is.Zero);
        }

        [Test]
        public void A_Resolved_Spell_Is_Not_A_Minion_Death()
        {
            CardInstance spell = Card(P0, maxHealth: 0);
            _state.Players[0].Hand.Add(spell);
            var watch = MatchTelemetryRecorder.Watch(_state, Play(spell));

            Die(0, _state.Players[0].Hand, spell);

            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 0), Is.Zero);
        }

        [Test]
        public void A_Minion_Card_Destroyed_In_Hand_Is_Not_A_Minion_Death()
        {
            CardInstance inHand = Minion(P1);
            _state.Players[1].Hand.Add(inHand);
            var watch = MatchTelemetryRecorder.Watch(_state, new EndTurnIntent(P0));

            Die(1, _state.Players[1].Hand, inHand);

            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 1), Is.Zero);
        }

        [Test]
        public void A_Minion_Card_Burned_Off_The_Deck_Is_Not_A_Minion_Death()
        {
            CardInstance top = Minion(P1);
            _state.Players[1].Deck.Add(top);
            var watch = MatchTelemetryRecorder.Watch(_state, new EndTurnIntent(P0));

            Die(1, _state.Players[1].Deck, top);

            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 1), Is.Zero);
        }

        [Test]
        public void A_Minion_Played_And_Killed_In_The_Same_Command_Is_Counted()
        {
            CardInstance played = Minion(P0);
            _state.Players[0].Hand.Add(played);
            var watch = MatchTelemetryRecorder.Watch(_state, Play(played));

            Die(0, _state.Players[0].Hand, played);

            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 0), Is.EqualTo(1));
        }

        [Test]
        public void A_Token_Summoned_And_Killed_In_The_Same_Command_Is_Counted()
        {
            var watch = MatchTelemetryRecorder.Watch(_state, new EndTurnIntent(P0));

            CardInstance token = Minion(P1);
            Die(1, new List<CardInstance> { token }, token);

            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 1), Is.EqualTo(1));
        }

        [Test]
        public void A_Death_From_An_Earlier_Command_Is_Not_Counted_Again()
        {
            CardInstance earlier = Minion(P1);
            _state.Players[1].Board.Add(earlier);
            Die(1, _state.Players[1].Board, earlier);

            CardInstance later = Minion(P1);
            _state.Players[1].Board.Add(later);
            var watch = MatchTelemetryRecorder.Watch(_state, new EndTurnIntent(P0));
            Die(1, _state.Players[1].Board, later);

            Assert.That(MatchTelemetryRecorder.MinionDeaths(_state, watch, seat: 1), Is.EqualTo(1));
        }

        private CardInstance Minion(PlayerId owner) => Card(owner, maxHealth: 3);

        private CardInstance Card(PlayerId owner, int maxHealth)
            => new CardInstance(new InstanceId(_nextId++), 1000, owner) { MaxHealth = maxHealth };

        private static PlayCardIntent Play(CardInstance card)
            => new PlayCardIntent(card.Controller, card.Id, 0, System.Array.Empty<TargetRef>());

        /// <summary>What <c>RulesEngine.EnterGraveyard</c> does, which is internal to the core.</summary>
        private void Die(int seat, List<CardInstance> from, CardInstance card)
        {
            from.Remove(card);
            card.Zone = Zone.Graveyard;
            card.DiedAtSequence = _state.NextDeathSequence++;
            _state.Players[seat].Graveyard.Add(card);
        }
    }
}
