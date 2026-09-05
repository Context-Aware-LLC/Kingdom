using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Tests.Support;
using Emberfall.Tools.NetSoak;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Bots
{
    [TestFixture]
    [Category("Unit")]
    public sealed class CheckpointSeatPolicyTests
    {
        private static readonly PlayerId P0 = new PlayerId("p0");
        private static readonly PlayerId P1 = new PlayerId("p1");

        [TestCase("off-turn")]
        [TestCase("mulligan-done")]
        [TestCase("opponent-choice")]
        [TestCase("finished")]
        public async Task Idle_Seat_Does_Not_Score_Or_Concede(string scenario)
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = ProjectOpeningState(db, setup);
            var scorer = new FakeScorer(count => new double[count]);
            var policy = new CheckpointSeatPolicy(scorer, db, new CommandProcessor(db),
                setup.P0Deck, CheckpointSelection.Argmax, new string('a', 64));
            _ = await policy.ChooseAsync(state, P0);
            int calls = scorer.Calls;
            switch (scenario)
            {
                case "off-turn":
                    state.Phase = TurnPhase.Main;
                    state.ActivePlayer = P1;
                    break;
                case "mulligan-done": state.Mulligan0Done = true; break;
                case "opponent-choice":
                    state.PendingChoice = new PendingChoice { Chooser = P1 };
                    break;
                case "finished": state.Outcome = MatchOutcome.Player1Win; break;
            }

            Assert.That(await policy.ChooseAsync(state, P0), Is.Null);
            Assert.That(scorer.Calls, Is.EqualTo(calls));
            Assert.That(policy.LastDecision, Is.Null);
        }

        [Test]
        public async Task Own_Pending_Choice_Is_Answered_Off_Turn()
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = ProjectOpeningState(db, setup);
            state.Phase = TurnPhase.Main;
            state.ActivePlayer = P1;
            state.PendingChoice = new PendingChoice
            {
                Chooser = P0,
                ChoiceSequence = 51,
                Options = { setup.P0Deck[0] },
            };
            var scorer = new FakeScorer(count =>
            {
                var logits = new double[count];
                logits[count - 1] = 10;
                return logits;
            });
            var policy = new CheckpointSeatPolicy(scorer, db, new CommandProcessor(db),
                setup.P0Deck, CheckpointSelection.Argmax, new string('a', 64));
            Assert.That(await policy.ChooseAsync(state, P0), Is.TypeOf<UnearthChoiceIntent>());
            Assert.That(scorer.Calls, Is.EqualTo(1));
        }

        [Test]
        public async Task Argmax_Maps_The_Selected_Logit_Back_To_The_Exact_Legal_Intent()
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = ProjectOpeningState(db, setup);
            var scorer = new FakeScorer(count =>
            {
                var logits = new double[count];
                logits[count - 1] = 10.0;
                return logits;
            });
            var policy = new CheckpointSeatPolicy(
                scorer,
                db,
                new CommandProcessor(db),
                setup.P0Deck,
                CheckpointSelection.Argmax,
                new string('a', 64));

            PlayerIntent? intent = await policy.ChooseAsync(state, P0);

            Assert.That(intent, Is.TypeOf<MulliganIntent>());
            Assert.That(((MulliganIntent)intent!).ToReplace,
                Is.EquivalentTo(state.PlayerOf(P0).Hand.ConvertAll(card => card.Id)));
            Assert.That(scorer.Calls, Is.EqualTo(1));
            Assert.That(scorer.LastMemory.GetProperty("selfRemainingDeck").GetArrayLength(),
                Is.EqualTo(state.PlayerOf(P0).Deck.Count));
            Assert.That(policy.Identity, Is.EqualTo($"checkpoint:{new string('a', 64)}:argmax"));
        }

        [Test]
        public async Task Seeded_Temperature_Sampling_Replays_The_Same_Action_Sequence()
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = ProjectOpeningState(db, setup);
            var first = NewUniformPolicy(seed: 71UL);
            var second = NewUniformPolicy(seed: 71UL);

            var firstSequence = new List<string>();
            var secondSequence = new List<string>();
            for (int i = 0; i < 12; i++)
            {
                firstSequence.Add(Encode(await first.ChooseAsync(state, P0), state.Id));
                secondSequence.Add(Encode(await second.ChooseAsync(state, P0), state.Id));
            }

            Assert.That(secondSequence, Is.EqualTo(firstSequence));
            Assert.That(new HashSet<string>(firstSequence).Count, Is.GreaterThan(1),
                "uniform logits should exercise more than one sampled legal action");

            CheckpointSeatPolicy NewUniformPolicy(ulong seed) => new CheckpointSeatPolicy(
                new FakeScorer(count => new double[count]),
                db,
                new CommandProcessor(db),
                setup.P0Deck,
                CheckpointSelection.Sample(0.75, seed),
                new string('b', 64));
        }

        [Test]
        public async Task Sampled_Decision_Exposes_Actor_Critic_Behavior_Provenance()
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = ProjectOpeningState(db, setup);
            string checkpointSha = new string('d', 64);
            var policy = new CheckpointSeatPolicy(
                new FakeScorer(count => new double[count]),
                db,
                new CommandProcessor(db),
                setup.P0Deck,
                CheckpointSelection.Sample(0.5, 19UL),
                checkpointSha);

            _ = await policy.ChooseAsync(state, P0);

            CheckpointDecisionTrace trace = policy.LastDecision
                ?? throw new AssertionException("sampled policy did not expose its decision trace");
            Assert.That(trace.CheckpointSha256, Is.EqualTo(checkpointSha));
            Assert.That(trace.Actor, Is.EqualTo(P0.Value));
            Assert.That(trace.StateVersion, Is.EqualTo(state.StateVersion));
            Assert.That(trace.Temperature, Is.EqualTo(0.5));
            Assert.That(trace.PredictedValue, Is.EqualTo(0.25));
            Assert.That(trace.SelectedActionIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(trace.BehaviorLogProbability,
                Is.EqualTo(-Math.Log(trace.LegalActionCount)).Within(1e-12));
        }

        [Test]
        public async Task Public_Opponent_Bounce_Is_Present_In_The_Next_Scoring_Memory()
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState authority = MatchEngine.BeginMatch(
                setup, db, new MatchId("checkpoint-memory"), P0, P1, 0xC0FFEEUL);
            int bouncedDefinition = setup.P1Deck[0];
            var bounced = new CardInstance(new InstanceId(900), bouncedDefinition, P1)
            {
                Zone = Zone.Board,
            };
            authority.PlayerOf(P1).Board.Add(bounced);
            GameState before = GameStateProjector.Project(authority, P0);
            authority.PlayerOf(P1).Board.Remove(bounced);
            bounced.Zone = Zone.Hand;
            authority.PlayerOf(P1).Hand.Add(bounced);
            GameState after = GameStateProjector.Project(authority, P0);
            var events = new[]
            {
                new GameEvent(
                    GameEventKind.CardBounced,
                    1UL,
                    source: null,
                    target: GameStateProjector.Sentinel,
                    amount: null,
                    flags: EventFlags.None),
            };
            var scorer = new FakeScorer(count => new double[count]);
            var policy = new CheckpointSeatPolicy(
                scorer,
                db,
                new CommandProcessor(db),
                setup.P0Deck,
                CheckpointSelection.Argmax,
                new string('c', 64));

            policy.ObserveTransition(before, events, after, P0);
            _ = await policy.ChooseAsync(after, P0);

            JsonElement known = scorer.LastMemory.GetProperty("knownOpponentHand");
            Assert.That(known.GetArrayLength(), Is.EqualTo(1));
            Assert.That(known[0].GetInt32(), Is.EqualTo(bouncedDefinition));
        }

        private static GameState ProjectOpeningState(FileCardDatabase db, MatchSetup setup)
        {
            GameState authority = MatchEngine.BeginMatch(
                setup, db, new MatchId("checkpoint-policy"), P0, P1, 0xC0FFEEUL);
            return GameStateProjector.Project(authority, P0);
        }

        private static string Encode(PlayerIntent? intent, MatchId match)
        {
            Assert.That(intent, Is.Not.Null);
            return System.Text.Encoding.UTF8.GetString(
                ProtocolCodec.Encode(new IntentMsg(0UL, intent!), match, 0UL));
        }

        private sealed class FakeScorer : ICheckpointScorer
        {
            private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

            private readonly Func<int, IReadOnlyList<double>> _logits;

            public FakeScorer(Func<int, IReadOnlyList<double>> logits) => _logits = logits;

            public int Calls { get; private set; }
            public JsonElement LastMemory { get; private set; }

            public Task<PolicyScore> ScoreAsync(
                JsonElement observation,
                object playerMemory,
                IReadOnlyList<JsonElement> legalActions,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls++;
                LastMemory = JsonSerializer.SerializeToElement(playerMemory, JsonOptions);
                return Task.FromResult(new PolicyScore(_logits(legalActions.Count), 0.25));
            }
        }
    }
}
