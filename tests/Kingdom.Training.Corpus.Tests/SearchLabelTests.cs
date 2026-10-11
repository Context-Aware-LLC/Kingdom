using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Projection;
using Kingdom.Core.Serialization;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.Replay;
using Kingdom.Policy;
using Kingdom.Training.Corpus;
using NUnit.Framework;

namespace Kingdom.Training.Corpus.Tests
{
    /// <summary>
    /// A search label is a training target written in legal-action indices. These pin the two
    /// places it is checked before anything trains on it: the manifest's own arithmetic, and the
    /// builder's comparison against the replayed legal set and the play-key rule.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SearchLabelTests
    {
        private static readonly PlayKeyRule RealRule =
            new PlayKeyRule(DistinctPlayKey.SchemaVersion, DistinctPlayKey.Of);

        [Test]
        public void A_complete_search_label_validates_and_round_trips_with_its_unvisited_mean_absent()
        {
            string root = TempDirectory();
            try
            {
                EpisodeManifest manifest = ManifestWith(Label());
                var writer = new JsonlEpisodeManifestWriter(root);
                writer.Append(manifest);

                string line = File.ReadAllLines(writer.Path).Single();
                Assert.That(line, Does.Contain("\"searchMeanValues\":[0.5,-1,null]"),
                    "an unvisited candidate has no mean; zero would read as a drawn rollout");
                EpisodeDecisionHash read = JsonlEpisodeManifestWriter.ReadAll(writer.Path)[0].DecisionHashes[0];
                Assert.Multiple(() =>
                {
                    Assert.That(read.SearchVersion, Is.EqualTo("fair-search.v1"));
                    Assert.That(read.SearchCandidateActionIndices, Is.EqualTo(new[] { 0, 2, 3 }));
                    Assert.That(read.SearchVisits, Is.EqualTo(new[] { 6, 2, 0 }));
                    Assert.That(read.SearchMeanValues, Is.EqualTo(new double?[] { 0.5, -1.0, null }));
                    Assert.That(read.SearchPlayGroups, Is.EqualTo(new[] { 0, 0, 1, 2 }));
                    Assert.That(read.SearchChosenActionIndex, Is.EqualTo(0));
                    Assert.That(read.SearchPlayKeySchemaVersion, Is.EqualTo(1));
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void A_decision_without_a_search_label_serializes_exactly_as_it_did_before()
        {
            var plain = new EpisodeDecisionHash(0, "p0", new string('e', 64), new string('f', 64));
            string json = JsonSerializer.Serialize(plain, CorpusJson.Options);
            Assert.That(json, Is.EqualTo(
                "{\"intentIndex\":0,\"actor\":\"p0\",\"beforeSha256\":\"" + new string('e', 64)
                + "\",\"afterSha256\":\"" + new string('f', 64) + "\"}"));
        }

        private static IEnumerable<TestCaseData> BrokenLabels()
        {
            EpisodeDecisionHash good = Label();
            yield return Broken("missing visits", good with { SearchVisits = null });
            yield return Broken("missing play groups", good with { SearchPlayGroups = null });
            yield return Broken("missing version", good with { SearchVersion = null });
            yield return Broken("negative visits", good with { SearchVisits = new[] { 6, -1, 0 } });
            yield return Broken("visit count per candidate", good with { SearchVisits = new[] { 6, 2 } });
            yield return Broken("mean outside [-1, 1]",
                good with { SearchMeanValues = new double?[] { 1.5, -1.0, null } });
            yield return Broken("mean on an unvisited candidate",
                good with { SearchMeanValues = new double?[] { 0.5, -1.0, 0.0 } });
            yield return Broken("no mean on a visited candidate",
                good with { SearchMeanValues = new double?[] { 0.5, null, null } });
            yield return Broken("chosen is not a candidate", good with { SearchChosenActionIndex = 1 });
            yield return Broken("chosen was never visited", good with { SearchChosenActionIndex = 3 });
            yield return Broken("candidate outside the action list",
                good with { SearchCandidateActionIndices = new[] { 0, 2, 4 } });
            yield return Broken("two spellings of one play",
                good with { SearchCandidateActionIndices = new[] { 0, 1, 3 } });
            yield return Broken("play index never used", good with { SearchPlayGroups = new[] { 0, 0, 1, 3 } });
            yield return Broken("prior is not a probability",
                good with { SearchPlayPriors = new[] { 1.5, 0.2, 0.1 } });
            yield return Broken("play-key schema", good with { SearchPlayKeySchemaVersion = 0 });
            yield return Broken("beside on-policy fields", good with
            {
                BehaviorCheckpointSha256 = new string('a', 64),
                BehaviorLogProbability = -0.5,
                BehaviorPredictedValue = 0.0,
                BehaviorTemperature = 1.0,
                BehaviorChosenActionIndex = 0,
                BehaviorLegalActionCount = 4,
            });
        }

        private static TestCaseData Broken(string name, EpisodeDecisionHash label)
            => new TestCaseData(label).SetArgDisplayNames(name);

        [Test]
        public void A_label_with_its_rollouts_validates_and_round_trips_in_the_recorded_order()
        {
            string root = TempDirectory();
            try
            {
                var writer = new JsonlEpisodeManifestWriter(root);
                writer.Append(ManifestWith(LabelWithRollouts()));

                string line = File.ReadAllLines(writer.Path).Single();
                Assert.That(line, Does.Contain(
                    "\"searchRolloutValues\":[1,1,-1,-1,null,null,1,1,0,-1,null,null,null,null]"),
                    "a rollout with no value is null; zero is a rollout that stopped at the ply cap or drew");
                Assert.That(line, Does.Contain("\"searchDecisionSeed\":18446744073709551615"),
                    "the seed is the whole 64 bits, as a number");
                EpisodeDecisionHash read = JsonlEpisodeManifestWriter.ReadAll(writer.Path)[0].DecisionHashes[0];
                Assert.Multiple(() =>
                {
                    Assert.That(read.SearchDecisionSeed, Is.EqualTo(ulong.MaxValue));
                    Assert.That(read.SearchRootValue, Is.EqualTo(0.25));
                    Assert.That(read.SearchCandidateDroppedRound, Is.EqualTo(new[] { -1, 1, 0 }));
                    Assert.That(read.SearchRoundWorlds, Is.EqualTo(new[] { 2, 4 }));
                    Assert.That(read.SearchNullRollouts, Is.EqualTo(6));
                    Assert.That(read.SearchRolloutValues, Is.EqualTo(LabelWithRollouts().SearchRolloutValues));
                    Assert.That(read.SearchRolloutOpponentHealth,
                        Is.EqualTo(LabelWithRollouts().SearchRolloutOpponentHealth));
                    Assert.That(read.SearchRolloutPlies, Is.EqualTo(LabelWithRollouts().SearchRolloutPlies));
                    Assert.That(read.SearchRolloutCapped, Is.EqualTo(new[] { 8 }));
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static IEnumerable<TestCaseData> BrokenRollouts()
        {
            EpisodeDecisionHash good = LabelWithRollouts();
            double?[] values = good.SearchRolloutValues!.ToArray();
            yield return Broken("rollouts without a label", new EpisodeDecisionHash(
                0, "p0", new string('e', 64), new string('f', 64)) { SearchRootValue = 0.25 });
            yield return Broken("missing seed", good with { SearchDecisionSeed = null });
            yield return Broken("missing one rollout array", good with { SearchRolloutPlies = null });
            yield return Broken("root value is not finite", good with { SearchRootValue = double.NaN });
            yield return Broken("dropped round per candidate",
                good with { SearchCandidateDroppedRound = new[] { -1, 1 } });
            yield return Broken("dropped in a round that was not played",
                good with { SearchCandidateDroppedRound = new[] { -1, 2, 0 } });
            yield return Broken("two survivors", good with { SearchCandidateDroppedRound = new[] { -1, -1, 0 } });
            yield return Broken("no survivor", good with { SearchCandidateDroppedRound = new[] { 1, 1, 0 } });
            yield return Broken("survivor is not the chosen play",
                good with { SearchCandidateDroppedRound = new[] { 1, -1, 0 } });
            yield return Broken("a round with no worlds", good with { SearchRoundWorlds = new[] { 2, 0 } });
            yield return Broken("rounds imply another rollout count",
                good with { SearchRoundWorlds = new[] { 2, 3 } });
            yield return Broken("one array shorter than the rest",
                good with { SearchRolloutEndTurn = good.SearchRolloutEndTurn!.Take(13).ToArray() });
            yield return Broken("null count", good with { SearchNullRollouts = 5 });
            // The first candidate's two round-0 values traded with the second's: every array is
            // still the right length, and only the sums say the layout was read wrongly.
            yield return Broken("rollouts that do not add up to the visits and means", good with
            {
                SearchRolloutValues = new double?[]
                    { -1.0, -1.0, 1.0, 1.0 }.Concat(values.Skip(4)).ToArray(),
            });
            yield return Broken("value outside [-1, 1]", good with
            {
                SearchRolloutValues = new double?[] { 3.0, -1.0 }.Concat(values.Skip(2)).ToArray(),
            });
            yield return Broken("capped rollout with a result", good with { SearchRolloutCapped = new[] { 0 } });
            yield return Broken("capped rollout with no value", good with { SearchRolloutCapped = new[] { 4 } });
            yield return Broken("capped position past the end", good with { SearchRolloutCapped = new[] { 8, 14 } });
            yield return Broken("capped positions out of order", good with { SearchRolloutCapped = new[] { 8, 8 } });
            yield return Broken("negative plies", good with
            {
                SearchRolloutPlies = new[] { -1 }.Concat(good.SearchRolloutPlies!.Skip(1)).ToArray(),
            });
        }

        [TestCaseSource(nameof(BrokenRollouts))]
        public void Search_rollouts_that_disagree_with_their_label_are_refused(EpisodeDecisionHash label)
            => Assert.Throws<ArgumentException>(() => ManifestWith(label).Validate());

        [Test]
        public void Builder_carries_the_rollouts_of_a_search_label_into_its_corpus_row()
        {
            string root = TempDirectory();
            try
            {
                FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
                // One round of three worlds over the fixture's two candidates: the chosen play
                // won two and lost one, the other lost the one world it could be played in.
                (string manifestPath, string cardHash, _) = Capture(root, db, label => label with
                {
                    SearchVersion = "fair-search.v3",
                    SearchDecisionSeed = 12345678901234567890UL,
                    SearchRootValue = -0.125,
                    SearchCandidateDroppedRound = new[] { -1, 0 },
                    SearchRoundWorlds = new[] { 3 },
                    SearchNullRollouts = 2,
                    SearchRolloutValues = new double?[] { 1.0, 1.0, -1.0, -1.0, null, null },
                    SearchRolloutOwnHealth = new[] { 12, 30, 0, -2, 0, 0 },
                    SearchRolloutOwnArmor = new[] { 0, 3, 0, 0, 0, 0 },
                    SearchRolloutOpponentHealth = new[] { 0, -4, 9, 17, 0, 0 },
                    SearchRolloutOpponentArmor = new[] { 0, 0, 2, 0, 0, 0 },
                    SearchRolloutPlies = new[] { 41, 60, 38, 77, 0, 0 },
                    SearchRolloutEndTurn = new[] { 9, 12, 8, 14, 0, 0 },
                    SearchRolloutCapped = Array.Empty<int>(),
                });

                new TrainingCorpusBuilder(db, RealRule).Build(manifestPath, cardHash, Path.Combine(root, "built"));
                string[] rows = File.ReadAllLines(
                    Path.Combine(root, "built", TrainingCorpusBuilder.DecisionsFileName));
                using JsonDocument searched = JsonDocument.Parse(rows[0]);
                using JsonDocument plain = JsonDocument.Parse(rows[1]);
                JsonElement row = searched.RootElement;
                int[] Ints(string name) => row.GetProperty(name).EnumerateArray().Select(v => v.GetInt32()).ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(row.GetProperty("searchDecisionSeed").GetUInt64(), Is.EqualTo(12345678901234567890UL));
                    Assert.That(row.GetProperty("searchRootValue").GetDouble(), Is.EqualTo(-0.125));
                    Assert.That(Ints("searchCandidateDroppedRound"), Is.EqualTo(new[] { -1, 0 }));
                    Assert.That(Ints("searchRoundWorlds"), Is.EqualTo(new[] { 3 }));
                    Assert.That(row.GetProperty("searchNullRollouts").GetInt32(), Is.EqualTo(2));
                    Assert.That(row.GetProperty("searchRolloutValues").GetRawText(),
                        Is.EqualTo("[1,1,-1,-1,null,null]"));
                    Assert.That(Ints("searchRolloutOwnHealth"), Is.EqualTo(new[] { 12, 30, 0, -2, 0, 0 }));
                    Assert.That(Ints("searchRolloutOwnArmor"), Is.EqualTo(new[] { 0, 3, 0, 0, 0, 0 }));
                    Assert.That(Ints("searchRolloutOpponentHealth"), Is.EqualTo(new[] { 0, -4, 9, 17, 0, 0 }));
                    Assert.That(Ints("searchRolloutOpponentArmor"), Is.EqualTo(new[] { 0, 0, 2, 0, 0, 0 }));
                    Assert.That(Ints("searchRolloutPlies"), Is.EqualTo(new[] { 41, 60, 38, 77, 0, 0 }));
                    Assert.That(Ints("searchRolloutEndTurn"), Is.EqualTo(new[] { 9, 12, 8, 14, 0, 0 }));
                    Assert.That(Ints("searchRolloutCapped"), Is.Empty);
                    Assert.That(plain.RootElement.EnumerateObject().Select(field => field.Name),
                        Has.None.StartsWith("search"), "an unsearched row has no search field at all");
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestCaseSource(nameof(BrokenLabels))]
        public void A_search_label_is_refused(EpisodeDecisionHash label)
            => Assert.Throws<ArgumentException>(() => ManifestWith(label).Validate());

        [Test]
        public void Builder_checks_a_search_label_against_the_replayed_actions_and_the_play_key()
        {
            string root = TempDirectory();
            try
            {
                FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
                (string manifestPath, string cardHash, int searchedActions) = Capture(root, db, label => label);

                CorpusBuildResult result = new TrainingCorpusBuilder(db, RealRule)
                    .Build(manifestPath, cardHash, Path.Combine(root, "built"));
                Assert.That(result.Decisions, Is.EqualTo(3));
                Assert.That(result.SearchedDecisions, Is.EqualTo(1));

                string[] rows = File.ReadAllLines(
                    Path.Combine(root, "built", TrainingCorpusBuilder.DecisionsFileName));
                using JsonDocument searched = JsonDocument.Parse(rows[0]);
                using JsonDocument plain = JsonDocument.Parse(rows[1]);
                JsonElement row = searched.RootElement;
                Assert.Multiple(() =>
                {
                    // The seat never enumerates concede, and its indices are positions in the list
                    // without it; an ordinary row keeps the list every earlier corpus has.
                    Assert.That(Types(row), Has.None.EqualTo("concede"));
                    Assert.That(Types(plain.RootElement), Has.Some.EqualTo("concede"));
                    Assert.That(row.GetProperty("legalActions").GetArrayLength(), Is.EqualTo(searchedActions));
                    Assert.That(row.GetProperty("searchPlayGroups").GetArrayLength(), Is.EqualTo(searchedActions));
                    Assert.That(row.GetProperty("searchVersion").GetString(), Is.EqualTo("fair-search.v1"));
                    Assert.That(row.GetProperty("searchVisits").EnumerateArray().Select(v => v.GetInt32()),
                        Is.EqualTo(new[] { 3, 1 }));
                    Assert.That(row.GetProperty("searchMeanValues")[1].GetDouble(), Is.EqualTo(-1.0));
                    Assert.That(row.GetProperty("searchCandidateActionIndices")[0].GetInt32(),
                        Is.EqualTo(row.GetProperty("chosenActionIndex").GetInt32()));
                    Assert.That(plain.RootElement.TryGetProperty("searchVersion", out _), Is.False);
                });
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void Builder_refuses_a_search_label_it_cannot_or_does_not_confirm()
        {
            string root = TempDirectory();
            try
            {
                FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
                (string manifestPath, string cardHash, _) = Capture(root, db, label => label);
                int attempt = 0;
                string Out() => Path.Combine(root, "built-" + attempt++);

                Assert.That(
                    Assert.Throws<InvalidDataException>(() => new TrainingCorpusBuilder(db)
                        .Build(manifestPath, cardHash, Out()))!.Message,
                    Does.Contain("no play-key rule"));
                Assert.That(
                    Assert.Throws<InvalidDataException>(() => new TrainingCorpusBuilder(
                            db, new PlayKeyRule(DistinctPlayKey.SchemaVersion + 1, DistinctPlayKey.Of))
                        .Build(manifestPath, cardHash, Out()))!.Message,
                    Does.Contain("play-key schema"));
                // A rule that calls every action the same play: the capture's groups no longer
                // match a fresh computation, which is what a changed DistinctPlayKey looks like.
                Assert.That(
                    Assert.Throws<InvalidDataException>(() => new TrainingCorpusBuilder(
                            db, new PlayKeyRule(DistinctPlayKey.SchemaVersion, (_, _) => "one-play"))
                        .Build(manifestPath, cardHash, Out()))!.Message,
                    Does.Contain("the play-key"));

                // The label is internally valid but names a play the replay did not make.
                string other = Path.Combine(root, "other");
                (string wrongChoice, _, _) = Capture(other, db, label => label with
                {
                    SearchCandidateActionIndices = label.SearchCandidateActionIndices!.Reverse().ToArray(),
                    SearchChosenActionIndex = label.SearchCandidateActionIndices![1],
                });
                Assert.That(
                    Assert.Throws<InvalidDataException>(() => new TrainingCorpusBuilder(db, RealRule)
                        .Build(wrongChoice, cardHash, Out()))!.Message,
                    Does.Contain("the replay holds"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>
        /// Two mulligans and a concession. The first mulligan carries a search label written the
        /// way capture writes one (indices into the list without concede, groups from the real
        /// rule), then passed through <paramref name="edit"/>.
        /// </summary>
        private static (string ManifestPath, string CardHash, int SearchedActions) Capture(
            string root, FileCardDatabase db, Func<EpisodeDecisionHash, EpisodeDecisionHash> edit)
        {
            string replayDir = Path.Combine(root, "replays");
            Directory.CreateDirectory(replayDir);
            MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
            var id = new MatchId("search-label-test");
            var p0 = new PlayerId("p0");
            var p1 = new PlayerId("p1");
            const ulong seed = 0xDADAUL;
            var replay = new JsonlReplayStore(replayDir);
            var processor = new CommandProcessor(db);
            var host = new LocalMatchHost(processor, db, setup);
            var legal = new LegalActionEnumerator(db, processor);
            host.BeginMatch(id, p0, p1, seed);
            replay.Begin(id, seed, p0, p1);
            var decisions = new List<EpisodeDecisionHash>();
            int searchedActions = 0;

            foreach (PlayerIntent intent in new PlayerIntent[]
                     {
                         new MulliganIntent(p0, Array.Empty<InstanceId>()),
                         new MulliganIntent(p1, Array.Empty<InstanceId>()),
                         new ConcedeIntent(p1),
                     })
            {
                GameState view = GameStateProjector.Project(host.State, intent.Player);
                string before = GameStateHasher.Hash(view);
                IReadOnlyList<PlayerIntent> actions = legal.Enumerate(host.State, intent.Player, includeConcede: false);
                byte[] encoded = ProtocolCodec.Encode(new IntentMsg(0UL, intent), id, 0UL);
                int chosen = actions.ToList().FindIndex(action =>
                    ProtocolCodec.Encode(new IntentMsg(0UL, action), id, 0UL).SequenceEqual(encoded));
                Assert.That(host.Submit(intent).Accepted, Is.True);
                replay.Append(id, intent);
                var plain = new EpisodeDecisionHash(
                    decisions.Count, intent.Player.Value, before,
                    GameStateHasher.Hash(GameStateProjector.Project(host.State, intent.Player)));
                if (decisions.Count == 0)
                {
                    Assert.That(chosen, Is.GreaterThanOrEqualTo(0));
                    Assert.That(actions.Count, Is.GreaterThan(2));
                    searchedActions = actions.Count;
                    int other = chosen == 0 ? 1 : 0;
                    plain = edit(plain with
                    {
                        SearchVersion = "fair-search.v1",
                        SearchCandidateActionIndices = new[] { chosen, other },
                        SearchVisits = new[] { 3, 1 },
                        SearchMeanValues = new double?[] { 1.0 / 3.0, -1.0 },
                        SearchPlayPriors = new[] { 0.6, 0.1 },
                        SearchChosenActionIndex = chosen,
                        SearchPlayGroups = DistinctPlayKey.GroupsOf(actions, view),
                        SearchPlayKeySchemaVersion = DistinctPlayKey.SchemaVersion,
                    });
                }
                decisions.Add(plain);
            }
            replay.Complete(id, host.State.Outcome);

            string cardHash = CorpusFiles.Sha256CardData(FindCardData());
            string replayPath = replay.PathFor(id);
            var manifest = new EpisodeManifest(
                EpisodeManifest.CurrentSchemaVersion, id.Value, seed, p0.Value, p1.Value,
                setup.P0Class, setup.P1Class, setup.P0Deck, setup.P1Deck,
                setup.HeroMaxHealth, setup.SparkTokenId,
                EpisodeManifest.CurrentEngineContractVersion, ProtocolContract.Version,
                "test-revision", cardHash, "fair-search-test", "fair-search-test",
                "replays/" + Path.GetFileName(replayPath), CorpusFiles.Sha256File(replayPath),
                GameStateHasher.Hash(GameStateProjector.Project(host.State, p0)),
                GameStateHasher.Hash(GameStateProjector.Project(host.State, p1)),
                decisions, host.State.Outcome);
            var writer = new JsonlEpisodeManifestWriter(root);
            writer.Append(manifest);
            return (writer.Path, cardHash, searchedActions);
        }

        private static IEnumerable<string?> Types(JsonElement row)
            => row.GetProperty("legalActions").EnumerateArray()
                .Select(action => action.GetProperty("$type").GetString());

        /// <summary>Four legal actions in three plays (0 and 1 spell the same one); the search
        /// tried three candidates, never reached the third, and chose the first.</summary>
        private static EpisodeDecisionHash Label()
            => new EpisodeDecisionHash(0, "p0", new string('e', 64), new string('f', 64))
            {
                SearchVersion = "fair-search.v1",
                SearchCandidateActionIndices = new[] { 0, 2, 3 },
                SearchVisits = new[] { 6, 2, 0 },
                SearchMeanValues = new double?[] { 0.5, -1.0, null },
                SearchPlayPriors = new[] { 0.7, 0.2, 0.1 },
                SearchChosenActionIndex = 0,
                SearchPlayGroups = new[] { 0, 0, 1, 2 },
                SearchPlayKeySchemaVersion = 1,
            };

        /// <summary>
        /// <see cref="Label"/> with the rollouts behind it. Two rounds, of two and four worlds:
        /// all three candidates played the first, the third never got a world it could play and
        /// was cut, and the second could not be played in any world of the second round. Laid
        /// out round by round, candidate by candidate:
        /// round 0: c0 [1, 1], c1 [-1, -1], c2 [null, null]; round 1: c0 [1, 1, 0, -1], c1 [null x4].
        /// The 0 is a rollout that ran into the ply cap, at position 8.
        /// </summary>
        private static EpisodeDecisionHash LabelWithRollouts()
            => Label() with
            {
                SearchVersion = "fair-search.v3",
                SearchDecisionSeed = ulong.MaxValue,
                SearchRootValue = 0.25,
                SearchCandidateDroppedRound = new[] { -1, 1, 0 },
                SearchRoundWorlds = new[] { 2, 4 },
                SearchNullRollouts = 6,
                SearchRolloutValues = new double?[]
                    { 1.0, 1.0, -1.0, -1.0, null, null, 1.0, 1.0, 0.0, -1.0, null, null, null, null },
                SearchRolloutOwnHealth = new[] { 20, 14, 0, -3, 0, 0, 7, 30, 11, 0, 0, 0, 0, 0 },
                SearchRolloutOwnArmor = new[] { 0, 2, 0, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0 },
                SearchRolloutOpponentHealth = new[] { 0, -2, 18, 9, 0, 0, 0, -6, 4, 22, 0, 0, 0, 0 },
                SearchRolloutOpponentArmor = new[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 0 },
                SearchRolloutPlies = new[] { 31, 48, 27, 52, 0, 0, 36, 44, 2400, 19, 0, 0, 0, 0 },
                SearchRolloutEndTurn = new[] { 11, 14, 10, 15, 0, 0, 12, 13, 61, 9, 0, 0, 0, 0 },
                SearchRolloutCapped = new[] { 8 },
            };

        private static EpisodeManifest ManifestWith(EpisodeDecisionHash decision)
            => new EpisodeManifest(
                EpisodeManifest.CurrentSchemaVersion, "match-one", 7UL, "p0", "p1",
                CardClass.EMBERWARD, CardClass.TIDECALLER, new[] { 1001 }, new[] { 1002 }, 20, 1,
                EpisodeManifest.CurrentEngineContractVersion, ProtocolContract.Version,
                "test-revision", new string('a', 64), "policy-a", "policy-b",
                "replays/match-one.jsonl", new string('b', 64), new string('c', 64), new string('d', 64),
                new[] { decision }, MatchOutcome.Player0Win);

        private static string TempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "emberfall-search-label-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string FindCardData()
        {
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 12 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "unity", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Could not locate Unity card data from the test output directory.");
        }
    }
}
