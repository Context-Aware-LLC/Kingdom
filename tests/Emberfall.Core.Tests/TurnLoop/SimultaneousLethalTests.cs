using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.TurnLoop
{
    [TestFixture]
    [Category("Unit")]
    public sealed class SimultaneousLethalTests
    {
        [TestCase(-1, 0, MatchOutcome.Player1Win)]
        [TestCase(0, -1, MatchOutcome.Player0Win)]
        [TestCase(0, 0, MatchOutcome.Draw)]
        [TestCase(-2, -2, MatchOutcome.Draw)]
        public void CheckWin_Uses_Final_Life_Total_When_Both_Heroes_Are_Dead(
            int player0Health,
            int player1Health,
            MatchOutcome expectedOutcome)
        {
            var state = CanonicalStateFixtures.BuildSeedState();
            state.Players[0].Hero.CurrentHealth = player0Health;
            state.Players[1].Hero.CurrentHealth = player1Health;
            var events = new EventBuffer(state);

            RulesEngine.CheckWin(state, events);

            Assert.Multiple(() =>
            {
                Assert.That(state.Outcome, Is.EqualTo(expectedOutcome));
                Assert.That(state.Phase, Is.EqualTo(TurnPhase.GameOver));
                Assert.That(events.Events[0].Kind, Is.EqualTo(GameEventKind.MatchEnded));
                Assert.That(
                    events.Events.Count,
                    Is.EqualTo(expectedOutcome == MatchOutcome.Draw ? 1 : 2));
            });
        }

        [TestCase(0, 1, MatchOutcome.Player1Win)]
        [TestCase(1, 0, MatchOutcome.Player0Win)]
        public void CheckWin_Preserves_OneSided_Lethal(
            int player0Health,
            int player1Health,
            MatchOutcome expectedOutcome)
        {
            var state = CanonicalStateFixtures.BuildSeedState();
            state.Players[0].Hero.CurrentHealth = player0Health;
            state.Players[1].Hero.CurrentHealth = player1Health;
            var events = new EventBuffer(state);

            RulesEngine.CheckWin(state, events);

            Assert.That(state.Outcome, Is.EqualTo(expectedOutcome));
            Assert.That(events.Events, Has.Count.EqualTo(2));
            Assert.That(events.Events[1].Kind, Is.EqualTo(GameEventKind.MatchWon));
        }
    }
}
