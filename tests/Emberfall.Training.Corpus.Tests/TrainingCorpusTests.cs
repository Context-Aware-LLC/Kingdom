using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Replay;
using Emberfall.Training.Corpus;
using NUnit.Framework;

namespace Emberfall.Training.Corpus.Tests
{
    [TestFixture]
    [Category("Unit")]
    public sealed class TrainingCorpusTests
    {
        [Test]
        public void Episode_manifest_writer_round_trips_and_rejects_duplicate_match_ids()
        {
            string root = TempDirectory();
            try
            {
                EpisodeManifest manifest = ExampleManifest("match-one", "replays/match-one.jsonl");
                var writer = new JsonlEpisodeManifestWriter(root);
                writer.Append(manifest);

                IReadOnlyList<EpisodeManifest> rows = JsonlEpisodeManifestWriter.ReadAll(writer.Path);
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.Multiple(() =>
                {
                    Assert.That(rows[0].MatchId, Is.EqualTo(manifest.MatchId));
                    Assert.That(rows[0].Seed, Is.EqualTo(manifest.Seed));
                    Assert.That(rows[0].P0Deck, Is.EqualTo(manifest.P0Deck));
                    Assert.That(rows[0].P1Deck, Is.EqualTo(manifest.P1Deck));
                    Assert.That(rows[0].ReplaySha256, Is.EqualTo(manifest.ReplaySha256));
                });
                Assert.That(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
                    .SequenceEqual(File.ReadAllBytes(writer.Path).Take(3)), Is.False, "manifest JSONL must be UTF-8 without BOM");
                Assert.Throws<InvalidOperationException>(() => writer.Append(manifest));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void Builder_replays_validates_and_emits_hidden_information_safe_decisions()
        {
            string root = TempDirectory();
            try
            {
                string raw = Path.Combine(root, "raw");
                string replayDir = Path.Combine(raw, "replays");
                string built = Path.Combine(root, "built");
                Directory.CreateDirectory(replayDir);

                string cards = FindCardData();
                FileCardDatabase db = FileCardDatabase.LoadDirectory(cards);
                MatchSetup setup = PrebuiltDecks.EmberwardVsTidecaller();
                var id = new MatchId("training-test");
                var p0 = new PlayerId("p0");
                var p1 = new PlayerId("p1");
                const ulong seed = 0xDADAUL;
                var replay = new JsonlReplayStore(replayDir);
                var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
                host.BeginMatch(id, p0, p1, seed);
                replay.Begin(id, seed, p0, p1);
                var decisionHashes = new List<EpisodeDecisionHash>();

                foreach (PlayerIntent intent in new PlayerIntent[]
                         {
                             new MulliganIntent(p0, Array.Empty<InstanceId>()),
                             new MulliganIntent(p1, Array.Empty<InstanceId>()),
                             new ConcedeIntent(p1),
                         })
                {
                    string before = GameStateHasher.Hash(GameStateProjector.Project(host.State, intent.Player));
                    CommandResult applied = host.Submit(intent);
                    Assert.That(applied.Accepted, Is.True);
                    replay.Append(id, intent);
                    decisionHashes.Add(new EpisodeDecisionHash(
                        decisionHashes.Count,
                        intent.Player.Value,
                        before,
                        GameStateHasher.Hash(GameStateProjector.Project(host.State, intent.Player))));
                }
                replay.Complete(id, host.State.Outcome);

                string replayPath = replay.PathFor(id);
                var manifest = new EpisodeManifest(
                    EpisodeManifest.CurrentSchemaVersion,
                    id.Value,
                    seed,
                    p0.Value,
                    p1.Value,
                    setup.P0Class,
                    setup.P1Class,
                    setup.P0Deck,
                    setup.P1Deck,
                    setup.HeroMaxHealth,
                    setup.SparkTokenId,
                    EpisodeManifest.CurrentEngineContractVersion,
                    ProtocolContract.Version,
                    "test-revision",
                    CorpusFiles.Sha256CardData(cards),
                    "test-policy",
                    "test-policy",
                    "replays/training-test.jsonl",
                    CorpusFiles.Sha256File(replayPath),
                    GameStateHasher.Hash(GameStateProjector.Project(host.State, p0)),
                    GameStateHasher.Hash(GameStateProjector.Project(host.State, p1)),
                    decisionHashes,
                    host.State.Outcome);
                var writer = new JsonlEpisodeManifestWriter(raw);
                writer.Append(manifest);

                CorpusBuildResult result = new TrainingCorpusBuilder(db)
                    .Build(writer.Path, manifest.CardDataSha256, built);
                string rebuilt = Path.Combine(root, "rebuilt");
                CorpusBuildResult second = new TrainingCorpusBuilder(db)
                    .Build(writer.Path, manifest.CardDataSha256, rebuilt);

                Assert.That(result.Episodes, Is.EqualTo(1));
                Assert.That(result.Decisions, Is.EqualTo(3));
                Assert.That(second.Decisions, Is.EqualTo(result.Decisions));
                Assert.That(File.Exists(result.DatasetManifestPath), Is.True);
                Assert.That(
                    CorpusFiles.Sha256File(Path.Combine(rebuilt, TrainingCorpusBuilder.DecisionsFileName)),
                    Is.EqualTo(CorpusFiles.Sha256File(Path.Combine(built, TrainingCorpusBuilder.DecisionsFileName))),
                    "the same replay and manifest must materialize byte-identical decisions");
                Assert.That(File.ReadAllBytes(second.DatasetManifestPath),
                    Is.EqualTo(File.ReadAllBytes(result.DatasetManifestPath)),
                    "the complete dataset manifest must not depend on its output directory");
                string[] decisions = File.ReadAllLines(Path.Combine(built, TrainingCorpusBuilder.DecisionsFileName));
                Assert.That(decisions, Has.Length.EqualTo(3));

                using JsonDocument first = JsonDocument.Parse(decisions[0]);
                JsonElement row = first.RootElement;
                Assert.That(row.GetProperty("chosenActionIndex").GetInt32(), Is.GreaterThanOrEqualTo(0));
                JsonElement opponentHand = row.GetProperty("observation").GetProperty("players")[1].GetProperty("hand");
                Assert.That(opponentHand.GetArrayLength(), Is.GreaterThan(0));
                foreach (JsonElement card in opponentHand.EnumerateArray())
                    Assert.That(card.GetProperty("definitionId").GetString(), Is.Empty,
                        "the acting player must not receive opponent hand identities");
                Assert.That(row.GetProperty("done").GetBoolean(), Is.False,
                    "p0's transition is not terminal while p1 still acts afterward");
                Assert.That(row.GetProperty("reward").GetInt32(), Is.Zero);
                Assert.That(row.GetProperty("episodeReturn").GetInt32(), Is.EqualTo(1));

                using JsonDocument last = JsonDocument.Parse(decisions[2]);
                Assert.That(last.RootElement.GetProperty("done").GetBoolean(), Is.True);
                Assert.That(last.RootElement.GetProperty("reward").GetInt32(), Is.EqualTo(-1));
                Assert.That(last.RootElement.GetProperty("episodeReturn").GetInt32(), Is.EqualTo(-1));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void Builder_rejects_a_replay_changed_after_manifest_capture()
        {
            string root = TempDirectory();
            try
            {
                string raw = Path.Combine(root, "raw");
                string replayDir = Path.Combine(raw, "replays");
                Directory.CreateDirectory(replayDir);
                string replayPath = Path.Combine(replayDir, "match-one.jsonl");
                File.WriteAllText(replayPath, "original", new UTF8Encoding(false));

                EpisodeManifest manifest = ExampleManifest("match-one", "replays/match-one.jsonl") with
                {
                    ReplaySha256 = CorpusFiles.Sha256File(replayPath),
                    CardDataSha256 = CorpusFiles.Sha256CardData(FindCardData()),
                };
                var writer = new JsonlEpisodeManifestWriter(raw);
                writer.Append(manifest);
                File.AppendAllText(replayPath, "tampered", new UTF8Encoding(false));

                FileCardDatabase db = FileCardDatabase.LoadDirectory(FindCardData());
                Assert.Throws<InvalidDataException>(() => new TrainingCorpusBuilder(db)
                    .Build(writer.Path, manifest.CardDataSha256, Path.Combine(root, "built")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static EpisodeManifest ExampleManifest(string matchId, string replayFile)
            => new EpisodeManifest(
                EpisodeManifest.CurrentSchemaVersion,
                matchId,
                7UL,
                "p0",
                "p1",
                CardClass.EMBERWARD,
                CardClass.TIDECALLER,
                new[] { "card-a" },
                new[] { "card-b" },
                20,
                "token_spark_coin",
                EpisodeManifest.CurrentEngineContractVersion,
                ProtocolContract.Version,
                "test-revision",
                new string('a', 64),
                "policy-a",
                "policy-b",
                replayFile,
                new string('b', 64),
                new string('c', 64),
                new string('d', 64),
                new[] { new EpisodeDecisionHash(0, "p0", new string('e', 64), new string('f', 64)) },
                MatchOutcome.Player0Win);

        private static string TempDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "emberfall-training-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string FindCardData()
        {
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 12 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "unity", "Emberfall", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Could not locate Unity card data from the test output directory.");
        }
    }
}
