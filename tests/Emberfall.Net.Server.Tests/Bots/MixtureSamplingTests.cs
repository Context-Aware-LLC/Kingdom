using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Policy;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Tests.Support;
using Emberfall.Training.Corpus;
using Emberfall.Tools.NetSoak;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Bots
{
    /// <summary>
    /// The mixture sampler exists because temperature could not move this policy: a p(chosen) of
    /// 0.999 that survived division by 4 implies a raw logit gap near 27, and temperature is
    /// multiplicative. An additive exploration floor is the intervention that reaches it.
    ///
    /// Everything here guards ONE failure mode, and it is a silent one. The recorded
    /// behaviorLogProbability is the denominator of every importance ratio the trainer computes.
    /// If the number written down is not the number the draw actually used, every ratio in the run
    /// is wrong by a factor that nothing logs, no gate checks, and no arena would reveal.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class MixtureSamplingTests
    {
        private static readonly PlayerId P0 = new PlayerId("p0");
        private static readonly PlayerId P1 = new PlayerId("p1");

        private const double Epsilon = 0.15;

        // ── the mixture algebra ────────────────────────────────────────────────

        [TestCase(0.05)]
        [TestCase(0.15)]
        [TestCase(0.5)]
        public void Mixture_Probabilities_Sum_To_One(double epsilon)
        {
            foreach (double[] pi in Distributions())
            {
                string[] keys = KeysFor(pi.Length);
                double[] mu = CheckpointSeatPolicy.MixtureProbabilities(pi, keys, epsilon, out _);
                double total = 0.0;
                foreach (double p in mu) total += p;
                Assert.That(total, Is.EqualTo(1.0).Within(1e-12),
                    $"epsilon={epsilon} pi=[{string.Join(",", pi)}]");

                // THE FLOOR IS PER PLAY, NOT PER SPELLING, and the difference is the whole point.
                // A spelling the model gives zero mass keeps zero: its play's exploration share
                // routes to whichever spelling the model prefers. Asserting a floor per index
                // would be asserting canonical-versus-model routing backwards.
                foreach (KeyValuePair<string, double> play in MassByPlay(mu, keys))
                    Assert.That(play.Value, Is.GreaterThanOrEqualTo(epsilon / Plays(keys) - 1e-15),
                        $"play {play.Key} at epsilon={epsilon}");
            }
        }

        [Test]
        public void Play_Level_Mass_Is_The_Policy_Mixed_With_Uniform_Over_PLAYS()
        {
            // Two spellings of play A, three of play B, one of play C.
            double[] pi = { 0.60, 0.20, 0.10, 0.04, 0.03, 0.03 };
            string[] keys = { "A", "A", "B", "B", "B", "C" };
            double[] mu = CheckpointSeatPolicy.MixtureProbabilities(pi, keys, Epsilon, out int plays);
            Assert.That(plays, Is.EqualTo(3));

            var expected = new Dictionary<string, double>
            {
                ["A"] = (1 - Epsilon) * 0.80 + Epsilon / 3,
                ["B"] = (1 - Epsilon) * 0.17 + Epsilon / 3,
                ["C"] = (1 - Epsilon) * 0.03 + Epsilon / 3,
            };
            foreach (KeyValuePair<string, double> play in expected)
            {
                double actual = 0.0;
                for (int i = 0; i < keys.Length; i++) if (keys[i] == play.Key) actual += mu[i];
                Assert.That(actual, Is.EqualTo(play.Value).Within(1e-12), $"play {play.Key}");
            }
        }

        [Test]
        public void Duplicating_A_Spelling_Does_Not_Change_A_Plays_Exploration_Share()
        {
            // THIS IS THE REASON THE KEY EXISTS. Mixing over raw indices would hand a play more
            // exploration for being listed more times — board slot 0 versus slot 1, two copies of
            // one card — which is a property of the action encoding, not of the game.
            double[] twoWay = { 0.9, 0.1 };
            string[] twoKeys = { "A", "B" };
            double[] spreadOut = { 0.3, 0.3, 0.3, 0.1 };
            string[] spreadKeys = { "A", "A", "A", "B" };

            double[] muTwo = CheckpointSeatPolicy.MixtureProbabilities(twoWay, twoKeys, Epsilon, out int k1);
            double[] muSpread = CheckpointSeatPolicy.MixtureProbabilities(spreadOut, spreadKeys, Epsilon, out int k2);
            Assert.That(k1, Is.EqualTo(2));
            Assert.That(k2, Is.EqualTo(2), "three spellings of A are still one play");

            Assert.That(muTwo[1], Is.EqualTo(muSpread[3]).Within(1e-12),
                "play B's total must not depend on how many ways play A is spelled");
            Assert.That(muSpread[0] + muSpread[1] + muSpread[2],
                Is.EqualTo(muTwo[0]).Within(1e-12));
        }

        [Test]
        public void Within_A_Play_The_Model_Keeps_Its_Preference_Among_Spellings()
        {
            // Model-conditional routing, not canonical routing: the ratio between two spellings of
            // one play is the model's own ratio, untouched by exploration. Canonical routing would
            // put the play's entire eps/K on one member and is a DIFFERENT policy — both are valid,
            // both are silent, and only one can be the recorded one.
            double[] pi = { 0.6, 0.2, 0.2 };
            string[] keys = { "A", "A", "B" };
            double[] mu = CheckpointSeatPolicy.MixtureProbabilities(pi, keys, Epsilon, out _);
            Assert.That(mu[0] / mu[1], Is.EqualTo(pi[0] / pi[1]).Within(1e-12));
        }

        [Test]
        public void A_Saturated_Policy_Still_Gives_Every_Play_A_Floor()
        {
            // The measured state of the world: effective branching 1.18 at temperature 4, 53% of
            // decisions above p=0.999. A play the policy has written off must still be reachable.
            double[] pi = { 1.0 - 1e-12, 1e-12, 0.0, 0.0 };
            string[] keys = { "A", "B", "C", "D" };
            double[] mu = CheckpointSeatPolicy.MixtureProbabilities(pi, keys, Epsilon, out int plays);
            Assert.That(plays, Is.EqualTo(4));
            // Each play is its own single spelling here, so the floor lands per index.
            for (int i = 1; i < mu.Length; i++)
                Assert.That(mu[i], Is.GreaterThanOrEqualTo(Epsilon / plays - 1e-15), $"index {i}");
            Assert.That(mu[0], Is.LessThan(pi[0]), "exploration must cost the favourite some mass");
        }

        [Test]
        public void An_Underflowed_Play_Splits_Its_Exploration_Evenly()
        {
            // pi(P) == 0 makes the conditional pi(i)/pi(P) undefined, and on this policy that is
            // the common case rather than an edge case.
            double[] pi = { 1.0, 0.0, 0.0 };
            string[] keys = { "A", "B", "B" };
            double[] mu = CheckpointSeatPolicy.MixtureProbabilities(pi, keys, Epsilon, out int plays);
            Assert.That(plays, Is.EqualTo(2));
            Assert.That(mu[1], Is.EqualTo(mu[2]).Within(1e-15));
            Assert.That(mu[1] + mu[2], Is.EqualTo(Epsilon / 2).Within(1e-15));
            double total = mu[0] + mu[1] + mu[2];
            Assert.That(total, Is.EqualTo(1.0).Within(1e-12));
        }

        [Test]
        public void Epsilon_Zero_Is_Refused_Rather_Than_Silently_Degrading_To_Temperature()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CheckpointSelection.Mixture(1.0, 0.0, 7UL));
            Assert.Throws<ArgumentOutOfRangeException>(() => CheckpointSelection.Mixture(1.0, 1.0, 7UL));
            Assert.Throws<ArgumentOutOfRangeException>(() => CheckpointSelection.Mixture(0.0, 0.15, 7UL));
        }

        // ── the recorded probability is the one the draw used ──────────────────

        [Test]
        public async Task Recorded_Behavior_Probability_Is_The_Probability_The_Draw_Used()
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = OpeningState(db, setup);

            var enumerator = new LegalActionEnumerator(db, new CommandProcessor(db));
            System.Collections.Generic.IReadOnlyList<PlayerIntent> actions =
                enumerator.Enumerate(state, P0, includeConcede: false);
            Assert.That(actions.Count, Is.GreaterThan(1), "this fixture must offer a real choice");

            double[] expected = ExpectedMixture(actions, state, db);

            // Many seeds, so the assertion covers more than one branch of the cumulative walk.
            for (ulong seed = 1; seed <= 64; seed++)
            {
                var policy = new CheckpointSeatPolicy(
                    new SaturatedScorer(), db, new CommandProcessor(db), setup.P0Deck,
                    CheckpointSelection.Mixture(1.0, Epsilon, seed), new string('a', 64));
                PlayerIntent? intent = await policy.ChooseAsync(state, P0);
                Assert.That(intent, Is.Not.Null);
                CheckpointDecisionTrace trace = policy.LastDecision!;
                Assert.That(Math.Exp(trace.BehaviorLogProbability),
                    Is.EqualTo(expected[trace.SelectedActionIndex]).Within(1e-12),
                    $"seed {seed} recorded a probability the draw did not use");
                Assert.That(trace.SamplerMode, Is.EqualTo("mixture"));
                Assert.That(trace.Epsilon, Is.EqualTo(Epsilon));
                Assert.That(trace.PlayKeySchemaVersion, Is.EqualTo(DistinctPlayKey.SchemaVersion));
                Assert.That(trace.DistinctPlayCount, Is.InRange(1, actions.Count));
            }
        }

        [Test]
        public async Task A_Saturated_Policy_Reaches_More_Than_One_Play_Across_Seeds()
        {
            // The behavioural claim, not just the algebra: under the same scorer that argmax would
            // pin to a single action, the mixture actually visits alternatives.
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = OpeningState(db, setup);

            var seen = new HashSet<int>();
            for (ulong seed = 1; seed <= 256; seed++)
            {
                var policy = new CheckpointSeatPolicy(
                    new SaturatedScorer(), db, new CommandProcessor(db), setup.P0Deck,
                    CheckpointSelection.Mixture(1.0, Epsilon, seed), new string('a', 64));
                _ = await policy.ChooseAsync(state, P0);
                seen.Add(policy.LastDecision!.SelectedActionIndex);
            }
            Assert.That(seen.Count, Is.GreaterThan(1),
                "a policy at p=1-1e-13 must still explore under an additive floor");

            var argmaxSeen = new HashSet<int>();
            for (ulong seed = 1; seed <= 16; seed++)
            {
                var policy = new CheckpointSeatPolicy(
                    new SaturatedScorer(), db, new CommandProcessor(db), setup.P0Deck,
                    CheckpointSelection.Argmax, new string('a', 64));
                _ = await policy.ChooseAsync(state, P0);
                argmaxSeen.Add(policy.LastDecision!.SelectedActionIndex);
            }
            Assert.That(argmaxSeen.Count, Is.EqualTo(1), "control: argmax on the same scorer");
        }

        [Test]
        public async Task A_Temperature_Rollout_Records_No_Mixture_Fields()
        {
            // Absent means "the plain tempered sampler". A row that carried half a mixture
            // specification would read as reproducible and not be.
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            var policy = new CheckpointSeatPolicy(
                new SaturatedScorer(), db, new CommandProcessor(db), setup.P0Deck,
                CheckpointSelection.Sample(4.0, 11UL), new string('a', 64));
            _ = await policy.ChooseAsync(OpeningState(db, setup), P0);
            CheckpointDecisionTrace trace = policy.LastDecision!;
            Assert.That(trace.SamplerMode, Is.EqualTo("temperature"));
            Assert.That(trace.Epsilon, Is.Null);
            Assert.That(trace.PlayKeySchemaVersion, Is.Null);
            Assert.That(trace.BehaviorLogProbability, Is.LessThanOrEqualTo(0.0));
        }

        [Test]
        public async Task A_Mixture_Row_Records_Which_Play_Each_Action_Belongs_To()
        {
            // Without the grouping, mu(i) = (1-eps)*pi(i) + (eps/K)*pi(i)/pi(P) cannot be
            // recomputed by anyone but the code that drew it — pi(P) needs to know which indices
            // share a play. Recording epsilon and K alone LOOKS reproducible and is not.
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = OpeningState(db, setup);
            var policy = new CheckpointSeatPolicy(
                new SaturatedScorer(), db, new CommandProcessor(db), setup.P0Deck,
                CheckpointSelection.Mixture(1.0, Epsilon, 5UL), new string('a', 64));
            _ = await policy.ChooseAsync(state, P0);

            CheckpointDecisionTrace trace = policy.LastDecision!;
            Assert.That(trace.PlayGroups, Is.Not.Null);
            Assert.That(trace.PlayGroups!.Count, Is.EqualTo(trace.LegalActionCount),
                "every listed action must be assigned to a play");
            Assert.That(trace.PlayGroups, Is.All.InRange(0, trace.DistinctPlayCount!.Value - 1));
            Assert.That(new HashSet<int>(trace.PlayGroups).Count,
                Is.EqualTo(trace.DistinctPlayCount!.Value),
                "the grouping and the distinct-play count must not disagree");

            // And the grouping is the one the sampler used, not a re-derivation: actions sharing a
            // play key must share a group id.
            var enumerator = new LegalActionEnumerator(db, new CommandProcessor(db));
            IReadOnlyList<PlayerIntent> actions = enumerator.Enumerate(state, P0, includeConcede: false);
            var keyToGroup = new Dictionary<string, int>();
            for (int i = 0; i < actions.Count; i++)
            {
                string key = DistinctPlayKey.Of(actions[i], state);
                if (keyToGroup.TryGetValue(key, out int group))
                    Assert.That(trace.PlayGroups[i], Is.EqualTo(group), $"action {i} split its play");
                else keyToGroup[key] = trace.PlayGroups[i];
            }
        }

        [Test]
        public void A_Manifest_Refuses_A_Mixture_Row_Whose_Grouping_Does_Not_Add_Up()
        {
            // A groups array that disagrees with the action or play count is worse than an absent
            // one: it lets a verifier recompute a confident, wrong mu.
            Assert.Multiple(() =>
            {
                Assert.That(() => ManifestWith(groups: null), Throws.ArgumentException,
                    "a mixture row with no grouping must be refused");
                Assert.That(() => ManifestWith(new[] { 0, 0 }), Throws.ArgumentException,
                    "grouping shorter than the action count must be refused");
                Assert.That(() => ManifestWith(new[] { 0, 1, 5 }), Throws.ArgumentException,
                    "a play index outside 0..K-1 must be refused");
                Assert.That(() => ManifestWith(new[] { 0, 0, 0 }), Throws.ArgumentException,
                    "a grouping that never uses play 1 must be refused");
                Assert.That(() => ManifestWith(new[] { 0, 1, 1 }), Throws.Nothing,
                    "a consistent grouping is accepted");
            });
        }

        private static void ManifestWith(IReadOnlyList<int>? groups)
        {
            var decision = new EpisodeDecisionHash(
                0, P0.Value, new string('b', 64), new string('c', 64),
                BehaviorCheckpointSha256: new string('a', 64),
                BehaviorLogProbability: -0.5,
                BehaviorPredictedValue: 0.1,
                BehaviorTemperature: 4.0,
                BehaviorChosenActionIndex: 0,
                BehaviorLegalActionCount: 3,
                BehaviorAllowsConcede: false,
                BehaviorSamplerMode: "mixture",
                BehaviorEpsilon: Epsilon,
                BehaviorDistinctPlayCount: 2,
                BehaviorPlayKeySchemaVersion: 1,
                BehaviorPlayGroups: groups);
            new EpisodeManifest(
                EpisodeManifest.CurrentSchemaVersion, "m", 1UL, P0.Value, P1.Value,
                CardClass.EMBERWARD, CardClass.TIDECALLER, new[] { 1 }, new[] { 1 }, 30, 1,
                EpisodeManifest.CurrentEngineContractVersion, ProtocolContract.Version, "rev",
                new string('d', 64), "p0", "p1", "r.jsonl", new string('e', 64),
                new string('f', 64), new string('0', 64), new[] { decision },
                MatchOutcome.Player0Win).Validate();
        }

        // ── the distinct-play key ──────────────────────────────────────────────

        [Test]
        public void One_Card_Into_Two_Board_Slots_Is_One_Play()
        {
            GameState state = SyntheticBoard();
            string slot0 = DistinctPlayKey.Of(
                new PlayCardIntent(P0, new InstanceId(10), 0, Array.Empty<TargetRef>()), state);
            string slot1 = DistinctPlayKey.Of(
                new PlayCardIntent(P0, new InstanceId(10), 1, Array.Empty<TargetRef>()), state);
            Assert.That(slot0, Is.EqualTo(slot1));
        }

        [Test]
        public void Two_Copies_Of_One_Card_Are_One_Play_And_Two_Definitions_Are_Not()
        {
            GameState state = SyntheticBoard();
            // Instances 10 and 11 share definition 100; instance 12 is definition 200.
            string copyA = DistinctPlayKey.Of(
                new PlayCardIntent(P0, new InstanceId(10), 0, Array.Empty<TargetRef>()), state);
            string copyB = DistinctPlayKey.Of(
                new PlayCardIntent(P0, new InstanceId(11), 0, Array.Empty<TargetRef>()), state);
            string other = DistinctPlayKey.Of(
                new PlayCardIntent(P0, new InstanceId(12), 0, Array.Empty<TargetRef>()), state);
            Assert.That(copyA, Is.EqualTo(copyB));
            Assert.That(copyA, Is.Not.EqualTo(other));
        }

        [Test]
        public void Target_Order_Is_Preserved()
        {
            // ReanimateHandler enters and triggers each target in sequence, so two orderings of one
            // set are two plays. This is also why Mass Rebirth enumerates permutations.
            GameState state = SyntheticBoard();
            var forward = new[] { new TargetRef(new InstanceId(20)), new TargetRef(new InstanceId(21)) };
            var reverse = new[] { new TargetRef(new InstanceId(21)), new TargetRef(new InstanceId(20)) };
            Assert.That(
                DistinctPlayKey.Of(new PlayCardIntent(P0, new InstanceId(12), 0, forward), state),
                Is.Not.EqualTo(
                    DistinctPlayKey.Of(new PlayCardIntent(P0, new InstanceId(12), 0, reverse), state)));
        }

        [Test]
        public void A_Non_Default_Binding_Is_A_Different_Play()
        {
            // A sacrifice is not a target. The Unity reviewer's ActionGrouping normalises a target
            // to its instance and drops the binding, so it merges strictly more than this does;
            // the production contract forbids collapsing non-default bindings.
            GameState state = SyntheticBoard();
            var bare = new[] { new TargetRef(new InstanceId(20)) };
            var bound = new[] { new TargetRef(new InstanceId(20), "sacrifice") };
            Assert.That(
                DistinctPlayKey.Of(new PlayCardIntent(P0, new InstanceId(12), 0, bare), state),
                Is.Not.EqualTo(
                    DistinctPlayKey.Of(new PlayCardIntent(P0, new InstanceId(12), 0, bound), state)));
        }

        [TestCase("")]
        [TestCase("target")]
        [TestCase("sacrifice")]
        public void A_Wire_Round_Trip_Does_Not_Change_The_Play_Key(string binding)
        {
            // A target has two JSON spellings and both occur in this corpus: a bare "i20" when the
            // binding is empty and an object when it is not (IntentJsonCodec.WriteTargets branches
            // on exactly that). Keying from JSON, the two never compare equal and the grouping
            // under-merges. Keying from the typed TargetRef, the distinction cannot arise — this
            // test is what says so rather than the comment.
            GameState state = SyntheticBoard();
            var intent = new PlayCardIntent(P0, new InstanceId(12), 0,
                new[] { new TargetRef(new InstanceId(20), binding.Length == 0 ? null : binding) });
            string before = DistinctPlayKey.Of(intent, state);

            byte[] wire = ProtocolCodec.Encode(new IntentMsg(0UL, intent), new MatchId("m"), 0UL);
            Assert.That(Encoding.UTF8.GetString(wire),
                Does.Contain(binding.Length == 0 ? "\"i20\"" : "\"binding\""),
                "the fixture must actually exercise the spelling under test");

            DecodeResult decodeResult = ProtocolCodec.Decode(wire);
            Assert.That(decodeResult.Message, Is.TypeOf<IntentMsg>());
            PlayerIntent decoded = ((IntentMsg)decodeResult.Message!).Intent;
            Assert.That(DistinctPlayKey.Of(decoded, state), Is.EqualTo(before));
        }

        [Test]
        public void An_Unearth_Choice_Is_Keyed_By_The_CARD_It_Picks()
        {
            // Keying unearth by its type alone merged three offered cards into one row in the
            // reviewer, and cost two mis-graded states before it was found. It would do worse here:
            // three real choices would read as one play and share a single exploration slot.
            GameState state = SyntheticBoard();
            string first = DistinctPlayKey.Of(new UnearthChoiceIntent(P0, 101, 3UL), state);
            string second = DistinctPlayKey.Of(new UnearthChoiceIntent(P0, 102, 3UL), state);
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(DistinctPlayKey.Of(new UnearthChoiceIntent(P0, 101, 9UL), state),
                Is.EqualTo(first), "choiceSequence is provenance, not part of the choice");
        }

        [Test]
        public void End_Turn_Is_Its_Own_Play_And_Is_Not_Concede()
        {
            // end_turn stays a genuine distinct choice: taking the only available play versus
            // passing measures restraint and changes outcomes.
            GameState state = SyntheticBoard();
            Assert.That(DistinctPlayKey.Of(new EndTurnIntent(P0), state),
                Is.Not.EqualTo(DistinctPlayKey.Of(new ConcedeIntent(P0), state)));
        }

        [Test]
        public void An_Attack_Merges_Identical_Attackers_But_Not_A_Damaged_One()
        {
            GameState state = SyntheticBoard();
            TargetRef defender = new TargetRef(new InstanceId(20));
            string healthyA = DistinctPlayKey.Of(new AttackIntent(P0, new InstanceId(30), defender), state);
            string healthyB = DistinctPlayKey.Of(new AttackIntent(P0, new InstanceId(31), defender), state);
            string damaged = DistinctPlayKey.Of(new AttackIntent(P0, new InstanceId(32), defender), state);
            Assert.That(healthyA, Is.EqualTo(healthyB), "two identical undamaged minions are one play");
            Assert.That(healthyA, Is.Not.EqualTo(damaged), "a damaged attacker is not interchangeable");
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static IEnumerable<double[]> Distributions()
        {
            yield return new[] { 1.0 };
            yield return new[] { 0.5, 0.5 };
            yield return new[] { 1.0 - 1e-13, 1e-13 };
            yield return new[] { 0.7, 0.2, 0.05, 0.03, 0.02 };
            yield return new[] { 1.0, 0.0, 0.0, 0.0 };
        }

        /// <summary>Keys in pairs, so several of these cases contain real alias groups.</summary>
        private static string[] KeysFor(int count)
        {
            var keys = new string[count];
            for (int i = 0; i < count; i++) keys[i] = ((char)('A' + (i / 2))).ToString();
            return keys;
        }

        private static int Plays(IReadOnlyList<string> keys) => new HashSet<string>(keys).Count;

        private static Dictionary<string, double> MassByPlay(
            IReadOnlyList<double> mu,
            IReadOnlyList<string> keys)
        {
            var byPlay = new Dictionary<string, double>();
            for (int i = 0; i < keys.Count; i++)
            {
                byPlay.TryGetValue(keys[i], out double running);
                byPlay[keys[i]] = running + mu[i];
            }
            return byPlay;
        }

        private static double[] ExpectedMixture(
            IReadOnlyList<PlayerIntent> actions,
            GameState state,
            FileCardDatabase db)
        {
            // Recomputed from the same logits the scorer hands the policy, independently of the
            // policy's own path through them.
            var pi = new double[actions.Count];
            double total = 0.0;
            IReadOnlyList<double> logits = SaturatedScorer.LogitsFor(actions.Count);
            double max = double.NegativeInfinity;
            foreach (double logit in logits) max = Math.Max(max, logit);
            for (int i = 0; i < actions.Count; i++)
            {
                pi[i] = Math.Exp(logits[i] - max);
                total += pi[i];
            }
            for (int i = 0; i < pi.Length; i++) pi[i] /= total;

            var keys = new string[actions.Count];
            for (int i = 0; i < actions.Count; i++) keys[i] = DistinctPlayKey.Of(actions[i], state);
            return CheckpointSeatPolicy.MixtureProbabilities(pi, keys, Epsilon, out _);
        }

        private static GameState OpeningState(FileCardDatabase db, MatchSetup setup)
        {
            GameState authority = MatchEngine.BeginMatch(
                setup, db, new MatchId("mixture-sampling"), P0, P1, 0xC0FFEEUL);
            return GameStateProjector.Project(authority, P0);
        }

        /// <summary>
        /// A hand-built board for the key tests. It never reaches the validator: a play key is a
        /// function of an intent and a state, so legality is irrelevant to what it must return.
        /// </summary>
        private static GameState SyntheticBoard()
        {
            FileCardDatabase db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            GameState state = OpeningState(db, setup);
            PlayerState self = state.PlayerOf(P0);
            PlayerState foe = state.Players[1 - state.SeatOf(P0)];
            self.Hand.Clear();
            self.Board.Clear();
            foe.Board.Clear();
            self.Hand.Add(new CardInstance(new InstanceId(10), 100, P0) { Zone = Zone.Hand });
            self.Hand.Add(new CardInstance(new InstanceId(11), 100, P0) { Zone = Zone.Hand });
            self.Hand.Add(new CardInstance(new InstanceId(12), 200, P0) { Zone = Zone.Hand });
            foe.Board.Add(new CardInstance(new InstanceId(20), 300, P1) { Zone = Zone.Board });
            foe.Board.Add(new CardInstance(new InstanceId(21), 300, P1) { Zone = Zone.Board });
            self.Board.Add(Minion(new InstanceId(30), 400, 3, 4));
            self.Board.Add(Minion(new InstanceId(31), 400, 3, 4));
            self.Board.Add(Minion(new InstanceId(32), 400, 3, 1)); // same card, damaged
            return state;
        }

        private static CardInstance Minion(InstanceId id, int definition, int attack, int health)
            => new CardInstance(id, definition, P0)
            {
                Zone = Zone.Board,
                CurrentAttack = attack,
                CurrentHealth = health,
            };

        /// <summary>
        /// A policy that has collapsed: index 0 at a logit gap of 30, which is the gap actually
        /// measured on this corpus and the reason temperature could not reach it.
        /// </summary>
        private sealed class SaturatedScorer : ICheckpointScorer
        {
            public static IReadOnlyList<double> LogitsFor(int count)
            {
                var logits = new double[count];
                for (int i = 0; i < count; i++) logits[i] = i == 0 ? 30.0 : -0.001 * i;
                return logits;
            }

            public Task<PolicyScore> ScoreAsync(
                JsonElement observation,
                object playerMemory,
                IReadOnlyList<JsonElement> legalActions,
                CancellationToken cancellationToken)
                => Task.FromResult(new PolicyScore(LogitsFor(legalActions.Count), 0.25));
        }
    }
}
