using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using Emberfall.Net.Server.Bots;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Bots
{
    /// <summary>
    /// The one thing about <see cref="GreedyAi"/> that is not "it misplays": a state it cannot answer
    /// at all.
    ///
    /// <para><b>Why this is the failure mode worth a test.</b> Every other weakness in the driver
    /// costs the bot a game. A <c>PendingChoice</c> it has no intent for costs it the SEAT:
    /// <c>CommandProcessor.CanExecute</c> refuses every intent with <c>ChoicePending</c> while one is
    /// open — <c>EndTurnIntent</c> included — and <c>BotSeatRunner</c> only re-decides when a new
    /// snapshot arrives, which cannot happen while the turn cannot end. The bot goes silent for the
    /// rest of the match, and the only visible symptom is a human opponent watching a turn clock run
    /// out. Card 230 Gear Cache is the only collectible that opens one today, so the hole is one
    /// roster edit away from shipping at any time.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class GreedyAiTests
    {
        private static readonly PlayerId Me = new PlayerId("P0");
        private static readonly PlayerId Foe = new PlayerId("P1");

        [Test]
        public void Answers_A_Pending_Unearth_With_An_Intent_The_Validator_Accepts()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var validator = new CommandProcessor(db);
            // Three real, distinct collectible ids spanning the curve — Gear Imp 1m, Assembly Drone
            // 3m, The Great Mechanism 8m. The seat's ceiling is 5, so the dearest AFFORDABLE one is
            // the 3m; the 8m must not be taken (a card the greedy loop would step over forever).
            GameState state = ChoiceOpen(new List<int> { 1061, 1064, 1069 }, manaMax: 5);

            PlayerIntent? intent = GreedyAi.Choose(state, Me, validator, db);

            Assert.That(intent, Is.TypeOf<UnearthChoiceIntent>(),
                "a pending choice must be answered, or the seat can never act again");
            var choice = (UnearthChoiceIntent)intent!;
            Assert.That(choice.ChosenCardId, Is.EqualTo(1064));
            Assert.That(choice.ChoiceSequence, Is.EqualTo(state.PendingChoice!.ChoiceSequence));
            Assert.That(validator.CanExecute(state, choice, out _), Is.True);
        }

        [Test]
        public void Takes_The_Cheapest_Option_When_Nothing_Offered_Is_Affordable()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var validator = new CommandProcessor(db);
            GameState state = ChoiceOpen(new List<int> { 1069, 1064 }, manaMax: 1);

            var choice = (UnearthChoiceIntent)GreedyAi.Choose(state, Me, validator, db)!;

            Assert.That(choice.ChosenCardId, Is.EqualTo(1064));
            Assert.That(validator.CanExecute(state, choice, out _), Is.True);
        }

        /// <summary>The opponent's pending choice is not ours to answer. A choice is always opened by
        /// the seat whose turn it is, so this is the shape that actually occurs: the foe is choosing
        /// on the foe's turn, and the driver must stay silent rather than submit a NotChoiceOwner
        /// reject.</summary>
        [Test]
        public void Leaves_The_Opponents_Choice_Alone()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var validator = new CommandProcessor(db);
            GameState state = ChoiceOpen(new List<int> { 1061 }, manaMax: 5);
            state.PendingChoice!.Chooser = Foe;
            state.ActivePlayer = Foe;

            Assert.That(GreedyAi.Choose(state, Me, validator, db), Is.Null);
        }

        /// <summary>A Main-phase state on <see cref="Me"/>'s turn with an open Unearth choice and an
        /// empty hand, so nothing but the choice can produce an intent.</summary>
        private static GameState ChoiceOpen(List<int> options, int manaMax)
        {
            var state = new GameState(new MatchId("greedy-ai"), new DeterministicRandom(1UL),
                new PlayerState(Me, new HeroInstance(new InstanceId(1), Me, CardClass.CLOCKWRIGHT, 30)),
                new PlayerState(Foe, new HeroInstance(new InstanceId(2), Foe, CardClass.EMBERWARD, 30)))
            {
                ActivePlayer = Me,
                Phase = TurnPhase.Main,
                TurnNumber = 5,
                Mulligan0Done = true,
                Mulligan1Done = true,
                NextInstanceCounter = 3,
            };
            state.Players[0].Mana.Max = manaMax;
            state.Players[0].Mana.Available = manaMax;
            state.PendingChoice = new PendingChoice
            {
                Chooser = Me,
                Kind = PendingChoiceKind.Unearth,
                Source = new InstanceId(1),
                Options = options,
                ChoiceSequence = 7UL,
            };
            return state;
        }
    }
}
