using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class InFlightAttacksTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void A_Submitted_Swing_Holds_Its_Attacker_Until_The_Snapshot_Spends_It()
        {
            GameState state = State();
            CardInstance minion = Board(state, 0);
            var attacks = new InFlightAttacks();

            attacks.Mark(minion.Id, state, P0);
            attacks.Prune(state, P0);
            Assert.That(attacks.Contains(minion.Id), Is.True, "an unrelated snapshot does not answer it");

            minion.AttacksUsedThisTurn = 1;
            attacks.Prune(state, P0);
            Assert.That(attacks.Contains(minion.Id), Is.False);
        }

        [Test]
        public void A_Second_Legal_Swing_Is_Held_Only_Against_Its_Own_Count()
        {
            GameState state = State();
            CardInstance minion = Board(state, 0);
            minion.AttacksUsedThisTurn = 1;
            var attacks = new InFlightAttacks();

            attacks.Mark(minion.Id, state, P0);
            attacks.Prune(state, P0);
            Assert.That(attacks.Contains(minion.Id), Is.True);

            minion.AttacksUsedThisTurn = 2;
            attacks.Prune(state, P0);
            Assert.That(attacks.Contains(minion.Id), Is.False);
        }

        [Test]
        public void A_Refusal_Releases_The_Attacker()
        {
            GameState state = State();
            CardInstance minion = Board(state, 0);
            var attacks = new InFlightAttacks();

            attacks.Mark(minion.Id, state, P0);
            attacks.Release(minion.Id);

            Assert.That(attacks.Contains(minion.Id), Is.False);
        }

        [Test]
        public void An_Attacker_That_Died_Or_A_Turn_That_Ended_Answers_The_Swing()
        {
            GameState state = State();
            CardInstance dies = Board(state, 0);
            CardInstance waits = Board(state, 0);
            var attacks = new InFlightAttacks();
            attacks.Mark(dies.Id, state, P0);
            attacks.Mark(waits.Id, state, P0);

            state.Players[0].Board.Remove(dies);
            attacks.Prune(state, P0);
            Assert.That(attacks.Contains(dies.Id), Is.False);
            Assert.That(attacks.Contains(waits.Id), Is.True);

            state.TurnNumber++;
            attacks.Prune(state, P0);
            Assert.That(attacks.Count, Is.EqualTo(0));
        }

        [Test]
        public void The_Hero_Is_Held_On_Its_Own_Attack_Count()
        {
            GameState state = State();
            InstanceId hero = state.Players[0].Hero.Id;
            var attacks = new InFlightAttacks();

            attacks.Mark(hero, state, P0);
            attacks.Prune(state, P0);
            Assert.That(attacks.Contains(hero), Is.True);

            state.Players[0].HeroAttacksUsedThisTurn = 1;
            attacks.Prune(state, P0);
            Assert.That(attacks.Contains(hero), Is.False);
        }

        [Test]
        public void An_Attacker_The_Mirror_Cannot_Find_Is_Not_Held()
        {
            GameState state = State();
            CardInstance enemy = Board(state, 1);
            var attacks = new InFlightAttacks();

            attacks.Mark(enemy.Id, state, P0);

            Assert.That(attacks.Contains(enemy.Id), Is.False, "nothing could ever answer it");
        }

        private static GameState State()
        {
            var state = new GameState(new MatchId("in-flight-attacks"), new DeterministicRandom(1),
                new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.NIGHTVEIL, 30)),
                new PlayerState(P1, new HeroInstance(new InstanceId(2), P1, CardClass.TEMPEST, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = P0,
                Phase = TurnPhase.Main,
                TurnNumber = 3,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            return state;
        }

        private static CardInstance Board(GameState state, int seat)
        {
            var card = new CardInstance(state.NextInstanceId(), 1, state.Players[seat].Id)
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
    }
}
