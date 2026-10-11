using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class UnearthChoiceModelTests
    {
        private static readonly PlayerId Me = new PlayerId("me");
        private static readonly PlayerId Opponent = new PlayerId("opponent");

        [Test]
        public void Chooser_Sees_Offer_In_Authoritative_Order_With_Its_Sequence()
        {
            var state = StateWithChoice(Me, 909, 30, 10, 20);

            UnearthChoicePresentation choice = UnearthChoiceModel.ForViewer(state, Me);

            Assert.Multiple(() =>
            {
                Assert.That(choice.Audience, Is.EqualTo(UnearthChoiceAudience.Chooser));
                Assert.That(choice.Options, Is.EqualTo(new[] { 30, 10, 20 }),
                    "the UI must render deterministic offer order without sorting");
                Assert.That(choice.ChoiceSequence, Is.EqualTo(909UL));
                Assert.That(choice.BlocksGameplayInput, Is.True);
            });

            state.PendingChoice!.Options[0] = 99;
            Assert.That(choice.Options, Is.EqualTo(new[] { 30, 10, 20 }),
                "the rendered offer should be a stable snapshot, not an alias of mutable state");
        }

        [Test]
        public void Opponent_Sees_Only_A_Passive_Unearth_State()
        {
            UnearthChoicePresentation choice =
                UnearthChoiceModel.ForViewer(StateWithChoice(Opponent, 909, 30, 10, 20), Me);

            Assert.Multiple(() =>
            {
                Assert.That(choice.Audience, Is.EqualTo(UnearthChoiceAudience.Opponent));
                Assert.That(choice.Options, Is.Empty,
                    "redacted Options.Count is zero by design and must not become a displayed count");
                Assert.That(choice.ChoiceSequence, Is.Zero);
                Assert.That(choice.BlocksGameplayInput, Is.True);
            });
        }

        [Test]
        public void No_Pending_Choice_Hides_The_Surface_And_Unblocks_Gameplay()
        {
            UnearthChoicePresentation choice = UnearthChoiceModel.ForViewer(NewState(), Me);

            Assert.Multiple(() =>
            {
                Assert.That(choice.Audience, Is.EqualTo(UnearthChoiceAudience.None));
                Assert.That(choice.Options, Is.Empty);
                Assert.That(choice.ChoiceSequence, Is.Zero);
                Assert.That(choice.BlocksGameplayInput, Is.False);
            });
        }

        [Test]
        public void Selection_Echoes_The_Offer_Sequence_And_Rejects_Stale_Or_Unoffered_Clicks()
        {
            GameState state = StateWithChoice(Me, 909, 30, 10, 20);

            UnearthChoiceIntent? valid = UnearthChoiceModel.IntentForSelection(state, Me, 10, 909);

            Assert.Multiple(() =>
            {
                Assert.That(valid, Is.EqualTo(new UnearthChoiceIntent(Me, 10, 909)));
                Assert.That(UnearthChoiceModel.IntentForSelection(state, Me, 10, 908), Is.Null,
                    "a click from a resolved/replaced choice must not be submitted");
                Assert.That(UnearthChoiceModel.IntentForSelection(state, Me, 99, 909), Is.Null,
                    "definition ids outside the offered list must not be submitted");
                Assert.That(UnearthChoiceModel.IntentForSelection(state, Opponent, 10, 909), Is.Null,
                    "the passive opponent surface can never manufacture a choice intent");
            });
        }

        private static GameState StateWithChoice(PlayerId chooser, ulong sequence, params int[] options)
        {
            GameState state = NewState();
            state.PendingChoice = new PendingChoice
            {
                Chooser = chooser,
                Kind = PendingChoiceKind.Unearth,
                Source = new InstanceId(123),
                ChoiceSequence = sequence,
            };
            foreach (int option in options) state.PendingChoice!.Options.Add(option);
            return state;
        }

        private static GameState NewState()
        {
            var p0 = new PlayerState(Me,
                new HeroInstance(new InstanceId(1), Me, CardClass.EMBERWARD, 30));
            var p1 = new PlayerState(Opponent,
                new HeroInstance(new InstanceId(2), Opponent, CardClass.TIDECALLER, 30));
            return new GameState(new MatchId("unearth-choice-ui"), new DeterministicRandom(1), p0, p1);
        }
    }
}
