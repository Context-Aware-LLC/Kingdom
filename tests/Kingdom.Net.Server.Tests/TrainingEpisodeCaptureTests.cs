using System;
using System.IO;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Tests.Support;
using Kingdom.Net.Server.Training;
using Kingdom.Training.Corpus;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    [TestFixture]
    [Category("Contract")]
    public sealed class TrainingEpisodeCaptureTests
    {
        private string _root = null!;

        [SetUp]
        public void SetUp()
            => _root = Path.Combine(Path.GetTempPath(), "emberfall-live-capture-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void Clean_Completed_Match_Writes_A_Corpus_Valid_Inbox_Episode()
        {
            string replayDir = Path.Combine(_root, "operational-replays");
            string inbox = Path.Combine(_root, "training-inbox");
            string cards = SessionHarness.CardDataDir();
            string cardDataSha256 = CorpusFiles.Sha256CardData(cards);
            var replay = new JsonlReplayStore(replayDir);
            var capture = new JsonlTrainingEpisodeStore(
                inbox,
                replay.PathFor,
                cardDataSha256,
                "test-build",
                ProtocolContract.Version);

            var time = new FakeTime();
            var p0 = new PlayerId("human-seat");
            var p1 = new PlayerId("bot-seat");
            var id = new MatchId("capture-match");
            var c0 = new FakeConnection();
            var c1 = new FakeConnection();
            var client0 = new ScriptedClient(p0);
            var client1 = new ScriptedClient(p1);
            var session = new MatchSession(
                id,
                SessionHarness.Seed,
                new ServerOptions
                {
                    HeartbeatTimeoutMs = long.MaxValue / 4,
                    StallGuardMs = long.MaxValue / 4,
                },
                time,
                SessionHarness.Db,
                PrebuiltDecks.EmberwardVsTidecaller(),
                replay,
                p0,
                p1,
                trainingEpisodes: capture,
                p0Policy: "human",
                p1Policy: "greedy-ai.v2");

            session.Start(c0, c1);
            session.Dispatch(new SessionCommand.Frame(
                c0, client0.Intent(new MulliganIntent(p0, Array.Empty<InstanceId>()), id)));
            session.Dispatch(new SessionCommand.Frame(
                c1, client1.Intent(new MulliganIntent(p1, Array.Empty<InstanceId>()), id)));
            session.Dispatch(new SessionCommand.Frame(
                c1, client1.Intent(new ConcedeIntent(p1), id)));

            string manifests = Path.Combine(inbox, JsonlEpisodeManifestWriter.FileName);
            var rows = JsonlEpisodeManifestWriter.ReadAll(manifests);
            Assert.That(rows, Has.Count.EqualTo(1));
            EpisodeManifest episode = rows[0];
            Assert.Multiple(() =>
            {
                Assert.That(episode.MatchId, Is.EqualTo(id.Value));
                Assert.That(episode.P0Policy, Is.EqualTo("human"));
                Assert.That(episode.P1Policy, Is.EqualTo("greedy-ai.v2"));
                Assert.That(episode.ReplayFile, Is.EqualTo("replays/capture-match.jsonl"));
                Assert.That(episode.DecisionHashes, Has.Count.EqualTo(3));
                Assert.That(episode.DecisionHashes[0].BehaviorLogProbability, Is.Null);
                Assert.That(episode.Outcome, Is.EqualTo(MatchOutcome.Player0Win));
            });

            string copiedReplay = Path.Combine(inbox, "replays", "capture-match.jsonl");
            Assert.That(File.Exists(copiedReplay), Is.True);
            Assert.That(CorpusFiles.Sha256File(copiedReplay), Is.EqualTo(episode.ReplaySha256));

            var built = new TrainingCorpusBuilder(SessionHarness.Db).Build(
                manifests,
                cardDataSha256,
                Path.Combine(_root, "materialized"));
            Assert.That(built.Episodes, Is.EqualTo(1));
            Assert.That(built.Decisions, Is.EqualTo(3));
        }

        [Test]
        public void Voided_Match_Never_Writes_A_Manifest()
        {
            string replayDir = Path.Combine(_root, "operational-replays");
            string inbox = Path.Combine(_root, "training-inbox");
            string cards = SessionHarness.CardDataDir();
            var replay = new JsonlReplayStore(replayDir);
            var capture = new JsonlTrainingEpisodeStore(
                inbox,
                replay.PathFor,
                CorpusFiles.Sha256CardData(cards),
                "test-build",
                ProtocolContract.Version);
            var c0 = new FakeConnection();
            var c1 = new FakeConnection();
            var session = new MatchSession(
                new MatchId("void-match"),
                SessionHarness.Seed,
                new ServerOptions(),
                new FakeTime(),
                SessionHarness.Db,
                PrebuiltDecks.EmberwardVsTidecaller(),
                replay,
                new PlayerId("p0"),
                new PlayerId("p1"),
                trainingEpisodes: capture);

            session.Start(c0, c1);
            session.Dispatch(new SessionCommand.Disconnected(c0));
            session.Dispatch(new SessionCommand.Disconnected(c1));

            Assert.That(
                File.Exists(Path.Combine(inbox, JsonlEpisodeManifestWriter.FileName)),
                Is.False);
            Assert.That(Directory.GetFiles(Path.Combine(inbox, "replays")), Is.Empty);
        }
    }
}
