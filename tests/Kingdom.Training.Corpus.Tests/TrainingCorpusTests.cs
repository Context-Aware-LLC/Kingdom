using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Projection;
using Kingdom.Core.Serialization;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.Replay;
using Kingdom.Training.Corpus;
using NUnit.Framework;
using Kingdom.Core.Tests.Support;
namespace Kingdom.Training.Corpus.Tests
{
    [TestFixture]
    [Category("Unit")]
    public sealed class TrainingCorpusTests
    {
        [Test]
        public void Known_opponent_hand_learns_a_public_bounce_for_both_viewer_directions()
        {
            for (int viewerSeat = 0; viewerSeat < 2; viewerSeat++)
            {
                var viewer = new PlayerId($"p{viewerSeat}");
                var tracker = new KnownOpponentHandTracker(viewer);
                GameState before = PublicTransitionState(viewerSeat, new[] { 41 }, opponentHand: 2);
                GameState after = PublicTransitionState(viewerSeat, Array.Empty<int>(), opponentHand: 3);

                tracker.Observe(before, new[]
                {
                    PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
                }, after, Array.Empty<int>());

                Assert.That(tracker.Snapshot(), Is.EqualTo(new[] { 41 }));
                Assert.That(tracker.Snapshot(), Does.Not.Contain(101),
                    "memory stores the public definition, never the retired board instance id");
            }
        }

        [Test]
        public void Known_opponent_hand_tracks_duplicate_mass_recall_as_a_sorted_multiset()
        {
            var tracker = new KnownOpponentHandTracker(new PlayerId("p0"));
            GameState before = PublicTransitionState(0, new[] { 73, 19, 73 }, opponentHand: 1);
            GameState after = PublicTransitionState(0, Array.Empty<int>(), opponentHand: 4);

            tracker.Observe(before, new[]
            {
                PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
                PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
                PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
            }, after, Array.Empty<int>());

            Assert.That(tracker.Snapshot(), Is.EqualTo(new[] { 19, 73, 73 }));
        }

        [Test]
        public void Known_opponent_hand_forgets_one_public_play_and_never_learns_an_unknown_draw()
        {
            var tracker = new KnownOpponentHandTracker(new PlayerId("p0"));
            GameState publicBoard = PublicTransitionState(0, new[] { 73, 73 }, opponentHand: 0);
            tracker.Observe(publicBoard, new[]
            {
                PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
                PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
            }, PublicTransitionState(0, Array.Empty<int>(), opponentHand: 2), Array.Empty<int>());

            GameState afterPlay = PublicTransitionState(0, new[] { 999 }, opponentHand: 1,
                firstOpponentBoardId: 501);
            tracker.Observe(
                PublicTransitionState(0, Array.Empty<int>(), opponentHand: 2),
                new[] { PublicEvent(GameEventKind.CardPlayed, source: new InstanceId(501)) },
                afterPlay,
                new[] { 73 });
            tracker.Observe(
                afterPlay,
                new[] { PublicEvent(GameEventKind.CardDrawn, source: GameStateProjector.Sentinel) },
                PublicTransitionState(0, new[] { 999 }, opponentHand: 2,
                    firstOpponentBoardId: 501),
                Array.Empty<int>());

            Assert.That(tracker.Snapshot(), Is.EqualTo(new[] { 73 }));
        }

        [Test]
        public void Known_opponent_hand_fails_closed_when_a_hidden_bounce_has_no_public_definition()
        {
            var tracker = new KnownOpponentHandTracker(new PlayerId("p0"));

            tracker.Observe(
                PublicTransitionState(0, Array.Empty<int>(), opponentHand: 1),
                new[] { PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel) },
                PublicTransitionState(0, Array.Empty<int>(), opponentHand: 2),
                Array.Empty<int>());

            Assert.That(tracker.Snapshot(), Is.Empty,
                "a hidden deck-to-hand transition must not invent a known definition");
        }

        [Test]
        public void Known_opponent_hand_conservatively_reduces_an_unidentified_hand_departure()
        {
            var tracker = new KnownOpponentHandTracker(new PlayerId("p0"));
            tracker.Observe(
                PublicTransitionState(0, new[] { 19, 73, 73 }, opponentHand: 0),
                new[]
                {
                    PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
                    PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
                    PublicEvent(GameEventKind.CardBounced, target: GameStateProjector.Sentinel),
                },
                PublicTransitionState(0, Array.Empty<int>(), opponentHand: 3),
                Array.Empty<int>());

            tracker.Observe(
                PublicTransitionState(0, Array.Empty<int>(), opponentHand: 3),
                new[] { PublicEvent(GameEventKind.WeaponEquipped, source: new InstanceId(2)) },
                PublicTransitionState(0, Array.Empty<int>(), opponentHand: 2),
                Array.Empty<int>());

            Assert.That(tracker.Snapshot(), Is.EqualTo(new[] { 73 }),
                "after one unidentified departure only one of two duplicate definitions is guaranteed");
        }

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
        public void Episode_manifest_writer_discards_an_uncommitted_trailing_row_on_reopen()
        {
            string root = TempDirectory();
            try
            {
                var writer = new JsonlEpisodeManifestWriter(root);
                writer.Append(ExampleManifest("match-one", "replays/match-one.jsonl"));
                long committedLength = new FileInfo(writer.Path).Length;
                File.AppendAllText(writer.Path, "{\"schemaVersion\":1", new UTF8Encoding(false));

                var recovered = new JsonlEpisodeManifestWriter(root);

                Assert.That(new FileInfo(recovered.Path).Length, Is.EqualTo(committedLength));
                Assert.That(JsonlEpisodeManifestWriter.ReadAll(recovered.Path), Has.Count.EqualTo(1));
                recovered.Append(ExampleManifest("match-two", "replays/match-two.jsonl"));
                Assert.That(JsonlEpisodeManifestWriter.ReadAll(recovered.Path), Has.Count.EqualTo(2));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Builder_replays_validates_and_emits_hidden_information_safe_decisions(bool excludeConcede)
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
                var processor = new CommandProcessor(db);
                var host = new LocalMatchHost(processor, db, setup);
                var legal = new LegalActionEnumerator(db, processor);
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
                    bool? allowsConcede = excludeConcede && intent is not ConcedeIntent ? false : null;
                    IReadOnlyList<PlayerIntent> actions = legal.Enumerate(
                        host.State, intent.Player, includeConcede: allowsConcede ?? true);
                    byte[] encodedIntent = ProtocolCodec.Encode(
                        new IntentMsg(0UL, intent), id, 0UL);
                    int chosenIndex = actions.ToList().FindIndex(action =>
                        ProtocolCodec.Encode(new IntentMsg(0UL, action), id, 0UL)
                            .SequenceEqual(encodedIntent));
                    Assert.That(chosenIndex, Is.GreaterThanOrEqualTo(0));
                    CommandResult applied = host.Submit(intent);
                    Assert.That(applied.Accepted, Is.True);
                    replay.Append(id, intent);
                    decisionHashes.Add(new EpisodeDecisionHash(
                        decisionHashes.Count,
                        intent.Player.Value,
                        before,
                        GameStateHasher.Hash(GameStateProjector.Project(host.State, intent.Player)),
                        new string('a', 64),
                        -0.5,
                        0.25,
                        0.75,
                        chosenIndex,
                        actions.Count,
                        allowsConcede));
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
                Assert.That(row.GetProperty("legalActions").EnumerateArray().Any(action =>
                    action.GetProperty("$type").GetString() == "concede"), Is.EqualTo(!excludeConcede));
                JsonElement opponentHand = row.GetProperty("observation").GetProperty("players")[1].GetProperty("hand");
                Assert.That(opponentHand.GetArrayLength(), Is.GreaterThan(0));
                foreach (JsonElement card in opponentHand.EnumerateArray())
                    Assert.That(card.GetProperty("definitionId").GetInt32(), Is.Zero,
                        "the acting player must not receive opponent hand identities");
                JsonElement memory = row.GetProperty("playerMemory");
                JsonElement remainingDeck = memory.GetProperty("selfRemainingDeck");
                Assert.That(remainingDeck.GetArrayLength(),
                    Is.EqualTo(row.GetProperty("observation").GetProperty("players")[0]
                        .GetProperty("deck").GetArrayLength()));
                foreach (JsonElement cardId in remainingDeck.EnumerateArray())
                    Assert.That(cardId.GetInt32(), Is.GreaterThan(0),
                        "the actor's remembered remaining deck contains definitions, never order or redacted ids");
                Assert.That(memory.TryGetProperty("opponentRemainingDeck", out _), Is.False,
                    "player memory must not expose the opponent's unrevealed deck");
                Assert.That(memory.GetProperty("selfGraveyard").GetArrayLength(), Is.Zero);
                Assert.That(memory.GetProperty("opponentGraveyard").GetArrayLength(), Is.Zero);
                Assert.That(memory.GetProperty("knownOpponentHand").GetArrayLength(), Is.Zero);
                Assert.That(row.GetProperty("done").GetBoolean(), Is.False,
                    "p0's transition is not terminal while p1 still acts afterward");
                Assert.That(row.GetProperty("reward").GetInt32(), Is.Zero);
                Assert.That(row.GetProperty("episodeReturn").GetInt32(), Is.EqualTo(1));
                Assert.That(row.GetProperty("behaviorCheckpointSha256").GetString(),
                    Is.EqualTo(new string('a', 64)));
                Assert.That(row.GetProperty("behaviorLogProbability").GetDouble(), Is.EqualTo(-0.5));
                Assert.That(row.GetProperty("behaviorPredictedValue").GetDouble(), Is.EqualTo(0.25));
                Assert.That(row.GetProperty("behaviorTemperature").GetDouble(), Is.EqualTo(0.75));

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
        public void Builder_materializes_public_bounce_memory_and_later_removal_deterministically()
        {
            string root = TempDirectory();
            try
            {
                string raw = Path.Combine(root, "raw");
                string replayDir = Path.Combine(raw, "replays");
                string built = Path.Combine(root, "built");
                string rebuilt = Path.Combine(root, "rebuilt");
                Directory.CreateDirectory(replayDir);

                const string cardDataSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                int minionDefinition = FixtureCard.Id("training_public_bounce_minion");
                int bounceDefinition = FixtureCard.Id("training_public_bounce_spell");
                var minionCardDefinition = new CardDefinition(
                    minionDefinition, "training_public_bounce_minion", "Public Bounce Minion",
                    CardType.MINION, 1, 1, 1, null, CardClass.EMBERWARD, Rarity.COMMON,
                    Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(), "",
                    "training_public_bounce_minion", SetId.CORE, false, null, "");
                var bounce = new CardDefinition(
                    bounceDefinition, "training_public_bounce_spell", "Public Bounce Spell",
                    CardType.SPELL, 1, null, null, null, CardClass.TIDECALLER, Rarity.COMMON,
                    Array.Empty<Keyword>(), new[]
                    {
                        new RuleDefinition(
                            new RuleId("bounce"),
                            new TriggeredActivation(Trigger.ON_CAST),
                            AlwaysCondition.Instance,
                            OneShotLifetime.Instance,
                            new ReturnToHandAction(
                                new TargetSpec("ANY_MINION", TargetSelection.Of(1))))
                    }, "", "training_public_bounce_spell", SetId.CORE, false, null, "");
                FakeCardDatabase db = new FakeCardDatabase().Register(minionCardDefinition).Register(bounce);
                var setup = new MatchSetup(
                    CardClass.EMBERWARD,
                    Enumerable.Repeat(minionDefinition, 30).ToArray(),
                    CardClass.TIDECALLER,
                    Enumerable.Repeat(bounceDefinition, 30).ToArray());
                var id = new MatchId("training-known-hand-test");
                var p0 = new PlayerId("p0");
                var p1 = new PlayerId("p1");
                const ulong seed = 0xB0AACEUL;
                var replay = new JsonlReplayStore(replayDir);
                var host = new LocalMatchHost(new CommandProcessor(db), db, setup);
                host.BeginMatch(id, p0, p1, seed);
                replay.Begin(id, seed, p0, p1);
                var decisionHashes = new List<EpisodeDecisionHash>();

                void Apply(PlayerIntent intent)
                {
                    string before = GameStateHasher.Hash(
                        GameStateProjector.Project(host.State, intent.Player));
                    CommandResult applied = host.Submit(intent);
                    Assert.That(applied.Accepted, Is.True, applied.Reason?.ToString());
                    replay.Append(id, intent);
                    decisionHashes.Add(new EpisodeDecisionHash(
                        decisionHashes.Count,
                        intent.Player.Value,
                        before,
                        GameStateHasher.Hash(GameStateProjector.Project(host.State, intent.Player))));
                }

                Apply(new MulliganIntent(p0, Array.Empty<InstanceId>()));
                Apply(new MulliganIntent(p1, Array.Empty<InstanceId>()));
                if (host.State.ActivePlayer == p1) Apply(new EndTurnIntent(p1));

                CardInstance minion = host.State.PlayerOf(p0).Hand.First(
                    card => card.DefinitionId == minionDefinition);
                Apply(new PlayCardIntent(p0, minion.Id, 0, Array.Empty<TargetRef>()));
                Apply(new EndTurnIntent(p0));

                CardInstance bounceCard = host.State.PlayerOf(p1).Hand.First(
                    card => card.DefinitionId == bounceDefinition);
                CardInstance publicTarget = host.State.PlayerOf(p0).Board.Single();
                InstanceId retiredPublicId = publicTarget.Id;
                Apply(new PlayCardIntent(
                    p1,
                    bounceCard.Id,
                    0,
                    new[] { new TargetRef(publicTarget.Id) }));
                int rememberedDecision = decisionHashes.Count;
                Apply(new EndTurnIntent(p1));

                CardInstance returned = host.State.PlayerOf(p0).Hand.First(
                    card => card.DefinitionId == minionDefinition);
                Assert.That(returned.Id, Is.Not.EqualTo(retiredPublicId));
                Apply(new PlayCardIntent(p0, returned.Id, 0, Array.Empty<TargetRef>()));
                Apply(new EndTurnIntent(p0));
                int clearedDecision = decisionHashes.Count;
                Apply(new ConcedeIntent(p1));
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
                    cardDataSha256,
                    "test-policy",
                    "test-policy",
                    "replays/training-known-hand-test.jsonl",
                    CorpusFiles.Sha256File(replayPath),
                    GameStateHasher.Hash(GameStateProjector.Project(host.State, p0)),
                    GameStateHasher.Hash(GameStateProjector.Project(host.State, p1)),
                    decisionHashes,
                    host.State.Outcome);
                var writer = new JsonlEpisodeManifestWriter(raw);
                writer.Append(manifest);

                new TrainingCorpusBuilder(db).Build(writer.Path, cardDataSha256, built);
                new TrainingCorpusBuilder(db).Build(writer.Path, cardDataSha256, rebuilt);
                string decisionsPath = Path.Combine(built, TrainingCorpusBuilder.DecisionsFileName);
                string[] decisions = File.ReadAllLines(decisionsPath);

                using JsonDocument remembered = JsonDocument.Parse(decisions[rememberedDecision]);
                Assert.That(remembered.RootElement.GetProperty("actor").GetString(), Is.EqualTo("p1"));
                Assert.That(
                    remembered.RootElement.GetProperty("playerMemory")
                        .GetProperty("knownOpponentHand").EnumerateArray()
                        .Select(value => value.GetInt32()),
                    Is.EqualTo(new[] { minionDefinition }));
                foreach (JsonElement card in remembered.RootElement.GetProperty("observation")
                    .GetProperty("players")[0].GetProperty("hand").EnumerateArray())
                {
                    Assert.That(card.GetProperty("definitionId").GetInt32(), Is.Zero);
                    Assert.That(card.GetProperty("id").GetString(), Is.EqualTo("i0"));
                }

                using JsonDocument cleared = JsonDocument.Parse(decisions[clearedDecision]);
                Assert.That(cleared.RootElement.GetProperty("actor").GetString(), Is.EqualTo("p1"));
                Assert.That(cleared.RootElement.GetProperty("playerMemory")
                    .GetProperty("knownOpponentHand").GetArrayLength(), Is.Zero);
                Assert.That(
                    CorpusFiles.Sha256File(Path.Combine(rebuilt, TrainingCorpusBuilder.DecisionsFileName)),
                    Is.EqualTo(CorpusFiles.Sha256File(decisionsPath)));
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
                new[] { FixtureCard.Id("card-a") },
                new[] { FixtureCard.Id("card-b") },
                20,
                FixtureCard.Id("token_spark_coin"),
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

        private static GameState PublicTransitionState(
            int viewerSeat,
            IReadOnlyList<int> opponentBoardDefinitions,
            int opponentHand,
            int firstOpponentBoardId = 101)
        {
            var p0 = new PlayerId("p0");
            var p1 = new PlayerId("p1");
            var state = new GameState(
                new MatchId("public-transition"),
                new DeterministicRandom(0),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)));
            PlayerState opponent = state.Players[1 - viewerSeat];
            for (int index = 0; index < opponentBoardDefinitions.Count; index++)
                opponent.Board.Add(new CardInstance(
                    new InstanceId(firstOpponentBoardId + index),
                    opponentBoardDefinitions[index],
                    opponent.Id)
                {
                    Zone = Zone.Board,
                    Position = index,
                });
            for (int index = 0; index < opponentHand; index++)
                opponent.Hand.Add(new CardInstance(
                    GameStateProjector.Sentinel,
                    0,
                    opponent.Id)
                {
                    Zone = Zone.Hand,
                });
            return state;
        }

        private static GameEvent PublicEvent(
            GameEventKind kind,
            InstanceId? source = null,
            InstanceId? target = null)
            => new GameEvent(kind, 0, source, target, null, EventFlags.None);

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
                string candidate = Path.Combine(path, "unity", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Could not locate Unity card data from the test output directory.");
        }
    }
}
