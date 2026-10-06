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

        /// <summary>The human-facing variant spends removal on the biggest threat, and the frozen
        /// reference still takes the leftmost minion — the second half is what keeps the RL
        /// league's yardstick from moving under it.</summary>
        [Test]
        public void HumanFacing_Removal_Takes_The_Biggest_Threat_And_The_Reference_Does_Not()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var validator = new CommandProcessor(db);
            GameState state = ChoiceOpen(new List<int>(), manaMax: 5);
            state.PendingChoice = null;
            // 178 Death Mark: "Destroy a minion."
            var removal = new CardInstance(state.NextInstanceId(), 178, Me) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(removal);
            CardInstance small = EnemyMinion(state, attack: 1, health: 1);
            CardInstance big = EnemyMinion(state, attack: 6, health: 6);

            var served = (PlayCardIntent)GreedyAi.Choose(state, Me, validator, db, humanFacing: true)!;
            var reference = (PlayCardIntent)GreedyAi.Choose(state, Me, validator, db)!;

            Assert.That(served.Targets[0].Instance, Is.EqualTo(big.Id));
            Assert.That(reference.Targets[0].Instance, Is.EqualTo(small.Id));
        }

        /// <summary>The human-facing variant trades into what the attacker kills before what it
        /// only dents.</summary>
        [Test]
        public void HumanFacing_Attack_Prefers_A_Defender_It_Kills()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var validator = new CommandProcessor(db);
            GameState state = ChoiceOpen(new List<int>(), manaMax: 0);
            state.PendingChoice = null;
            var attacker = new CardInstance(state.NextInstanceId(), 1061, Me)
            {
                Zone = Zone.Board, CurrentAttack = 3, CurrentHealth = 3, MaxHealth = 3,
            };
            state.Players[0].Board.Add(attacker);
            EnemyMinion(state, attack: 2, health: 8);
            CardInstance killable = EnemyMinion(state, attack: 4, health: 3);

            var attack = (AttackIntent)GreedyAi.Choose(state, Me, validator, db, humanFacing: true)!;

            Assert.That(attack.Defender.Instance, Is.EqualTo(killable.Id));
        }

        /// <summary>With lethal on the board the human-facing variant goes face instead of
        /// trading.</summary>
        [Test]
        public void HumanFacing_Takes_Lethal_Over_A_Trade()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var validator = new CommandProcessor(db);
            GameState state = ChoiceOpen(new List<int>(), manaMax: 0);
            state.PendingChoice = null;
            state.Players[0].Board.Add(new CardInstance(state.NextInstanceId(), 1061, Me)
            {
                Zone = Zone.Board, CurrentAttack = 3, CurrentHealth = 3, MaxHealth = 3,
            });
            state.Players[1].Hero.CurrentHealth = 3;
            EnemyMinion(state, attack: 4, health: 3);

            var attack = (AttackIntent)GreedyAi.Choose(state, Me, validator, db, humanFacing: true)!;

            Assert.That(attack.Defender.Instance, Is.EqualTo(state.Players[1].Hero.Id));
        }

        private static CardInstance EnemyMinion(GameState state, int attack, int health)
        {
            // 1061 Gear Imp, a keywordless body; the live stats are what the driver reads.
            var minion = new CardInstance(state.NextInstanceId(), 1061, Foe)
            {
                Zone = Zone.Board,
                CurrentAttack = attack,
                CurrentHealth = health,
                MaxHealth = health,
                Position = state.Players[1].Board.Count,
            };
            state.Players[1].Board.Add(minion);
            return minion;
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
