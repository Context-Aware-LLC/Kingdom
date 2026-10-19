using Kingdom.Core.Contracts;
using Kingdom.Core.Serialization;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class MatchOpeningDealTests
    {
        private static GameState Opening(int first, int local)
        {
            var p0 = new PlayerId("p0");
            var p1 = new PlayerId("p1");
            var state = new GameState(new MatchId("toss"), new DeterministicRandom(123),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)))
            {
                Phase = TurnPhase.Mulligan,
                ActivePlayer = first == 0 ? p0 : p1,
            };
            for (int seat = 0; seat < 2; seat++)
            {
                PlayerState player = state.Players[seat];
                int count = seat == first ? 3 : 5;
                for (int i = 0; i < count; i++)
                    player.Hand.Add(new CardInstance(new InstanceId(seat == local ? 10 + i : 0),
                        seat != local ? 0 : i == 4 ? MatchOpeningDeal.SparkCardId : 100 + i, player.Id)
                    { Zone = Zone.Hand });
            }
            return state;
        }

        [TestCase(0, 0)]
        [TestCase(0, 1)]
        [TestCase(1, 0)]
        [TestCase(1, 1)]
        public void Toss_Uses_The_Server_Result_From_Either_Projection_Without_Mutating_It(int first, int local)
        {
            GameState state = Opening(first, local);
            string before = GameStateHasher.Hash(state);
            MatchOpeningDeal deal = MatchOpeningDeal.From(state, state.Players[local].Id)!;
            Assert.Multiple(() =>
            {
                Assert.That(deal, Is.Not.Null);
                Assert.That(deal.SecondPlayer, Is.EqualTo(state.Players[1 - first].Id));
                Assert.That(deal.HandSlot, Is.EqualTo(4));
                Assert.That(GameStateHasher.Hash(state), Is.EqualTo(before),
                    "The presentation must not roll the match RNG, mint a card or change a hand.");
            });
        }

        [TestCase(0)]
        [TestCase(1)]
        public void Resume_After_Our_Mulligan_Or_After_Main_Does_Not_Replay_The_Toss(int local)
        {
            GameState state = Opening(0, local);
            PlayerId viewer = state.Players[local].Id;
            if (local == 0) state.Mulligan0Done = true;
            else state.Mulligan1Done = true;
            Assert.That(MatchOpeningDeal.From(state, viewer), Is.Null);
            state.Mulligan0Done = state.Mulligan1Done = false;
            state.Phase = TurnPhase.Main;
            Assert.That(MatchOpeningDeal.IsPending(state, viewer), Is.False);
            Assert.That(MatchOpeningDeal.From(state, viewer), Is.Null);
        }

        [Test]
        public void Opponent_Confirmation_Does_Not_End_Our_Opening_But_Match_End_Does()
        {
            GameState state = Opening(1, 0);
            state.Mulligan1Done = true;
            Assert.That(MatchOpeningDeal.From(state, state.Players[0].Id), Is.Not.Null);
            state.Outcome = MatchOutcome.Player1Win;
            Assert.That(MatchOpeningDeal.IsPending(state, state.Players[0].Id), Is.False);
        }

        [Test]
        public void Missing_Spark_Unknown_Viewer_And_Unknown_First_Player_Skip_Gracefully()
        {
            GameState state = Opening(0, 1);
            state.Players[1].Hand.RemoveAt(4);
            Assert.That(MatchOpeningDeal.From(state, state.Players[1].Id), Is.Null);
            Assert.That(MatchOpeningDeal.From(state, new PlayerId("spectator")), Is.Null);
            state.ActivePlayer = new PlayerId("unknown");
            Assert.That(MatchOpeningDeal.From(state, state.Players[0].Id), Is.Null);
        }
    }
}
