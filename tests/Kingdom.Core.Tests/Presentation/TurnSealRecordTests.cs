using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class TurnSealRecordTests
    {
        private static GameState State(int turn, string active = "local")
            => new GameState(new MatchId("seal"), new DeterministicRandom(1),
                new PlayerState(new PlayerId("local"), new HeroInstance(new InstanceId(1), new PlayerId("local"), CardClass.EMBERWARD, 30)),
                new PlayerState(new PlayerId("opponent"), new HeroInstance(new InstanceId(2), new PlayerId("opponent"), CardClass.TIDECALLER, 30)))
            {
                Phase = TurnPhase.Main,
                Outcome = MatchOutcome.InProgress,
                TurnNumber = turn,
                ActivePlayer = new PlayerId(active),
            };

        private static GameEvent End(ulong sequence = 10, bool timedOut = true)
            => new GameEvent(GameEventKind.TurnEnded, sequence, null, null, null,
                EventFlags.None, timedOut: timedOut);

        [TestCase("local")]
        [TestCase("opponent")]
        public void Receipt_Survives_All_Updates_In_The_Next_Turn_Then_Clears(string nextPlayer)
        {
            var receipt = new TurnSealRecord();
            receipt.OnStateUpdated(State(2, nextPlayer)); // playback delivers snapshot before events
            Assert.That(receipt.OnEvent(End()), Is.True);
            ulong animation = receipt.Revision;
            receipt.OnStateUpdated(State(2, nextPlayer)); // draw, play, or reconnect refresh
            Assert.That(receipt.ApprovedByDefault, Is.True);
            Assert.That(receipt.Revision, Is.EqualTo(animation));
            receipt.OnStateUpdated(State(3));
            Assert.That(receipt.ApprovedByDefault, Is.False);
            Assert.That(receipt.Revision, Is.Not.EqualTo(animation), "an unfinished stamp must be cancelled");
        }

        [Test]
        public void Manual_End_And_Reconnect_Snapshot_Do_Not_Invent_A_Receipt()
        {
            var receipt = new TurnSealRecord();
            receipt.OnStateUpdated(State(12, "opponent"));
            Assert.That(receipt.OnEvent(End(timedOut: false)), Is.False);
            Assert.That(receipt.ApprovedByDefault, Is.False);
        }

        [Test]
        public void Repeated_And_Older_Timeouts_Do_Not_Replay_Or_Revive_The_Stamp()
        {
            var receipt = new TurnSealRecord();
            receipt.OnStateUpdated(State(2));
            Assert.That(receipt.OnEvent(End(10)), Is.True);
            ulong animation = receipt.Revision;
            Assert.That(receipt.OnEvent(End(10)), Is.False);
            Assert.That(receipt.OnEvent(End(9)), Is.False);
            Assert.That(receipt.Revision, Is.EqualTo(animation));
            receipt.OnStateUpdated(State(3));
            Assert.That(receipt.OnEvent(End(10)), Is.False);
            Assert.That(receipt.ApprovedByDefault, Is.False);
            Assert.That(receipt.OnEvent(End(20)), Is.True);
        }

        [Test]
        public void Terminal_State_Clears_Receipt_And_Suppresses_Timeout_Animation()
        {
            var receipt = new TurnSealRecord();
            receipt.OnStateUpdated(State(2));
            receipt.OnEvent(End());
            var ended = State(2);
            ended.Outcome = MatchOutcome.Player0Win;
            receipt.OnStateUpdated(ended);
            Assert.That(receipt.ApprovedByDefault, Is.False);
            Assert.That(receipt.OnEvent(End(11)), Is.False);
        }
    }
}
