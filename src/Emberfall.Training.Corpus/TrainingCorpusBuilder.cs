using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using Emberfall.Net.Protocol;

namespace Emberfall.Training.Corpus
{
    public sealed record CorpusBuildResult(string DatasetManifestPath, int Episodes, int Decisions);

    /// <summary>
    /// Materializes replay + episode sidecar into model-neutral decision rows. It replays through
    /// the real core, refuses provenance or outcome drift, and emits only the acting player's
    /// projected observation. Tensor and chat exporters consume this stable seam later.
    /// </summary>
    public sealed class TrainingCorpusBuilder
    {
        public const string DecisionsFileName = "decisions.jsonl";
        public const string EpisodesFileName = "episodes.jsonl";
        public const string DatasetManifestFileName = "dataset-manifest.json";

        private readonly ICardDatabase _db;

        public TrainingCorpusBuilder(ICardDatabase db) => _db = db;

        public CorpusBuildResult Build(string sourceManifestPath, string cardDataSha256, string outputDirectory)
        {
            string manifests = Path.GetFullPath(sourceManifestPath);
            string sourceDirectory = Path.GetDirectoryName(manifests)
                ?? throw new ArgumentException("Manifest path has no parent directory.", nameof(sourceManifestPath));
            Directory.CreateDirectory(outputDirectory);
            string decisionsPath = Path.Combine(outputDirectory, DecisionsFileName);
            string episodesPath = Path.Combine(outputDirectory, EpisodesFileName);
            string decisionsPartial = decisionsPath + ".partial";
            string episodesPartial = episodesPath + ".partial";
            var matchIds = new HashSet<string>(StringComparer.Ordinal);
            int episodeCount = 0;
            int decisionCount = 0;

            try
            {
                using (var decisionStream = DurableCreate(decisionsPartial))
                using (var episodeStream = DurableCreate(episodesPartial))
                {
                    foreach (EpisodeManifest episode in JsonlEpisodeManifestWriter.Read(manifests))
                    {
                        if (!matchIds.Add(episode.MatchId))
                            throw new InvalidDataException(
                                $"Episode manifest contains duplicate matchId '{episode.MatchId}'.");
                        episodeCount++;
                        if (!string.Equals(episode.EngineContractVersion,
                                           EpisodeManifest.CurrentEngineContractVersion,
                                           StringComparison.Ordinal))
                            throw new InvalidDataException(
                                $"Episode '{episode.MatchId}' engine contract '{episode.EngineContractVersion}' " +
                                $"is not supported by this builder ('{EpisodeManifest.CurrentEngineContractVersion}').");
                        if (!string.Equals(episode.WireContractVersion, ProtocolContract.Version,
                                           StringComparison.Ordinal))
                            throw new InvalidDataException(
                                $"Episode '{episode.MatchId}' wire contract '{episode.WireContractVersion}' " +
                                $"is not supported by this builder ('{ProtocolContract.Version}').");
                        if (!string.Equals(episode.CardDataSha256, cardDataSha256, StringComparison.Ordinal))
                            throw new InvalidDataException(
                                $"Episode '{episode.MatchId}' card-data hash {episode.CardDataSha256} " +
                                $"does not match loaded cards {cardDataSha256}.");

                        int steps = MaterializeEpisode(episode, sourceDirectory, decisionStream);
                        decisionCount += steps;
                        WriteLine(episodeStream, new CorpusEpisodeRow(
                            episode.MatchId, episode.Outcome, steps, episode.P0Policy, episode.P1Policy));
                    }
                    if (episodeCount == 0)
                        throw new InvalidDataException("The episode manifest contains no rows.");
                    decisionStream.Flush(flushToDisk: true);
                    episodeStream.Flush(flushToDisk: true);
                }

                File.Move(decisionsPartial, decisionsPath, overwrite: true);
                File.Move(episodesPartial, episodesPath, overwrite: true);
            }
            catch
            {
                TryDelete(decisionsPartial);
                TryDelete(episodesPartial);
                throw;
            }

            var dataset = new DatasetManifest(
                DatasetManifest.CurrentSchemaVersion,
                EpisodeManifest.CurrentSchemaVersion,
                Path.GetFileName(manifests),
                CorpusFiles.Sha256File(manifests),
                cardDataSha256,
                episodeCount,
                decisionCount,
                new[]
                {
                    new DatasetShard(DecisionsFileName, decisionCount, CorpusFiles.Sha256File(decisionsPath)),
                    new DatasetShard(EpisodesFileName, episodeCount, CorpusFiles.Sha256File(episodesPath)),
                });
            string datasetPath = Path.Combine(outputDirectory, DatasetManifestFileName);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(dataset, CorpusJson.Options);
            CorpusFiles.AtomicWrite(datasetPath, json);
            return new CorpusBuildResult(datasetPath, episodeCount, decisionCount);
        }

        private int MaterializeEpisode(EpisodeManifest episode, string sourceDirectory, FileStream output)
        {
            string replayPath = ResolveContainedPath(sourceDirectory, episode.ReplayFile);
            string actualReplayHash = CorpusFiles.Sha256File(replayPath);
            if (!string.Equals(actualReplayHash, episode.ReplaySha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Episode '{episode.MatchId}' replay hash {actualReplayHash} does not match manifest {episode.ReplaySha256}.");

            ReplayRecord replay = ReadReplay(replayPath, episode.MatchId);
            ValidateReplayHeader(episode, replay);
            if (episode.DecisionHashes.Count != replay.Intents.Count)
                throw new InvalidDataException(
                    $"Episode '{episode.MatchId}' has {episode.DecisionHashes.Count} state-hash anchors " +
                    $"for {replay.Intents.Count} replay intents.");
            var setup = new MatchSetup(episode.P0Class, episode.P0Deck, episode.P1Class, episode.P1Deck,
                                       episode.HeroMaxHealth, episode.SparkTokenId);
            var processor = new CommandProcessor(_db);
            var host = new LocalMatchHost(processor, _db, setup);
            var p0 = new PlayerId(episode.P0);
            var p1 = new PlayerId(episode.P1);
            host.BeginMatch(new MatchId(episode.MatchId), p0, p1, episode.Seed);
            var legal = new LegalActionEnumerator(_db, processor);
            var p0Knowledge = new KnownOpponentHandTracker(p0);
            var p1Knowledge = new KnownOpponentHandTracker(p1);

            int index = 0;
            foreach (PlayerIntent intent in replay.Intents)
            {
                GameState projectedBeforeP0 = GameStateProjector.Project(host.State, p0);
                GameState projectedBeforeP1 = GameStateProjector.Project(host.State, p1);
                GameState projectedBefore = intent.Player == p0 ? projectedBeforeP0 : projectedBeforeP1;
                IReadOnlyList<PlayerIntent> actions = legal.Enumerate(host.State, intent.Player,
                    includeConcede: true);
                List<JsonElement> actionJson = EncodeActions(actions, new MatchId(episode.MatchId));
                PlayerIntent normalizedIntent = NormalizeIntent(host.State, intent);
                JsonElement chosenJson = EncodeIntent(normalizedIntent, new MatchId(episode.MatchId));
                int chosenIndex = FindAction(actionJson, chosenJson);
                if (chosenIndex < 0)
                    throw new InvalidDataException(
                        $"Episode '{episode.MatchId}' intent {index} ({intent.GetType().Name}) was not in the legal action set.");

                string beforeHash = GameStateHasher.Hash(projectedBefore);
                EpisodeDecisionHash expectedHashes = episode.DecisionHashes[index];
                if (!string.Equals(expectedHashes.Actor, intent.Player.Value, StringComparison.Ordinal)
                    || !string.Equals(expectedHashes.BeforeSha256, beforeHash, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Episode '{episode.MatchId}' intent {index} pre-action observation hash disagrees with capture.");
                JsonElement observation = EncodeObservation(projectedBefore, new MatchId(episode.MatchId));
                PlayerMemory playerMemory = EncodePlayerMemory(
                    host.State,
                    intent.Player,
                    intent.Player == p0 ? p0Knowledge.Snapshot() : p1Knowledge.Snapshot());
                CommandResult result = host.Submit(intent);
                if (!result.Accepted)
                    throw new InvalidDataException(
                        $"Episode '{episode.MatchId}' intent {index} was rejected during replay: {result.Reason}.");
                GameState projectedAfterP0 = GameStateProjector.Project(host.State, p0);
                GameState projectedAfterP1 = GameStateProjector.Project(host.State, p1);
                IReadOnlyList<GameEvent> p0Events = GameStateProjector.ProjectEvents(
                    result.Events, processor.LastEventVisibility, viewerSeat: 0);
                IReadOnlyList<GameEvent> p1Events = GameStateProjector.ProjectEvents(
                    result.Events, processor.LastEventVisibility, viewerSeat: 1);
                IReadOnlyList<int> publiclyPlayedDefinitions = PubliclyPlayedDefinitions(
                    projectedBefore,
                    intent.Player == p0 ? p1Events : p0Events,
                    intent.Player);
                p0Knowledge.Observe(
                    projectedBeforeP0,
                    p0Events,
                    projectedAfterP0,
                    intent.Player == p0 ? Array.Empty<int>() : publiclyPlayedDefinitions);
                p1Knowledge.Observe(
                    projectedBeforeP1,
                    p1Events,
                    projectedAfterP1,
                    intent.Player == p1 ? Array.Empty<int>() : publiclyPlayedDefinitions);
                GameState projectedAfter = intent.Player == p0 ? projectedAfterP0 : projectedAfterP1;
                string afterHash = GameStateHasher.Hash(projectedAfter);
                if (!string.Equals(expectedHashes.AfterSha256, afterHash, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Episode '{episode.MatchId}' intent {index} post-action observation hash disagrees with capture.");
                bool done = host.State.Outcome != MatchOutcome.InProgress;
                int reward = done ? RewardFor(replay.Outcome, host.State.SeatOf(intent.Player)) : 0;
                int episodeReturn = RewardFor(replay.Outcome, host.State.SeatOf(intent.Player));

                WriteLine(output, new CorpusDecisionRow(
                    episode.MatchId,
                    index,
                    intent.Player.Value,
                    projectedBefore.StateVersion,
                    beforeHash,
                    observation,
                    playerMemory,
                    actionJson,
                    chosenIndex,
                    chosenJson,
                    afterHash,
                    reward,
                    done,
                    episodeReturn));
                index++;
            }

            if (host.State.Outcome != replay.Outcome || host.State.Outcome != episode.Outcome)
                throw new InvalidDataException(
                    $"Episode '{episode.MatchId}' outcome drift: replay={replay.Outcome}, " +
                    $"manifest={episode.Outcome}, reconstructed={host.State.Outcome}.");
            string p0Hash = GameStateHasher.Hash(GameStateProjector.Project(host.State, new PlayerId(episode.P0)));
            string p1Hash = GameStateHasher.Hash(GameStateProjector.Project(host.State, new PlayerId(episode.P1)));
            if (!string.Equals(p0Hash, episode.FinalP0ObservationSha256, StringComparison.Ordinal)
                || !string.Equals(p1Hash, episode.FinalP1ObservationSha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Episode '{episode.MatchId}' terminal observation hash disagrees with its manifest.");
            return index;
        }

        private static ReplayRecord ReadReplay(string path, string expectedMatchId)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2) throw new InvalidDataException($"Replay '{path}' is incomplete.");
            using JsonDocument header = JsonDocument.Parse(lines[0]);
            JsonElement root = header.RootElement;
            if (!root.TryGetProperty("emberfallReplay", out JsonElement version) || version.GetInt32() != 1)
                throw new InvalidDataException($"Replay '{path}' has an unsupported header.");
            ulong seed = root.GetProperty("seed").GetUInt64();
            string p0 = root.GetProperty("p0").GetString() ?? "";
            string p1 = root.GetProperty("p1").GetString() ?? "";

            var intents = new List<PlayerIntent>();
            MatchOutcome? outcome = null;
            for (int i = 1; i < lines.Length; i++)
            {
                using JsonDocument row = JsonDocument.Parse(lines[i]);
                if (row.RootElement.TryGetProperty("outcome", out JsonElement outcomeElement))
                {
                    if (!Enum.TryParse(outcomeElement.GetString(), false, out MatchOutcome parsed))
                        throw new InvalidDataException($"Replay '{path}' has an unknown outcome.");
                    outcome = parsed;
                    if (i != lines.Length - 1)
                        throw new InvalidDataException($"Replay '{path}' has rows after its outcome footer.");
                    continue;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(lines[i]);
                DecodeResult decoded = ProtocolCodec.Decode(bytes);
                if (!decoded.Ok || decoded.Message is not IntentMsg intent)
                    throw new InvalidDataException(
                        $"Replay '{path}' row {i + 1} is not an intent: {decoded.Error} {decoded.Detail}");
                if (!string.Equals(decoded.MatchId.Value, expectedMatchId, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Replay '{path}' row {i + 1} belongs to match '{decoded.MatchId.Value}', " +
                        $"expected '{expectedMatchId}'.");
                intents.Add(intent.Intent);
            }
            if (!outcome.HasValue) throw new InvalidDataException($"Replay '{path}' has no outcome footer.");
            return new ReplayRecord(seed, p0, p1, intents, outcome.Value);
        }

        private static void ValidateReplayHeader(EpisodeManifest episode, ReplayRecord replay)
        {
            if (replay.Seed != episode.Seed || replay.P0 != episode.P0 || replay.P1 != episode.P1)
                throw new InvalidDataException($"Episode '{episode.MatchId}' replay header disagrees with its manifest.");
        }

        private static string ResolveContainedPath(string root, string relative)
        {
            if (Path.IsPathRooted(relative))
                throw new InvalidDataException("ReplayFile must be relative to the episode manifest.");
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ReplayFile '{relative}' escapes the manifest directory.");
            return candidate;
        }

        private static JsonElement EncodeObservation(GameState projected, MatchId match)
        {
            byte[] envelope = ProtocolCodec.Encode(SnapshotMsg.Of(projected), match, 0UL);
            using JsonDocument document = JsonDocument.Parse(envelope);
            return document.RootElement.GetProperty("body").GetProperty("state").Clone();
        }

        private static PlayerMemory EncodePlayerMemory(
            GameState state,
            PlayerId actor,
            IReadOnlyList<int> knownOpponentHand)
        {
            int actorSeat = state.SeatOf(actor);
            PlayerState self = state.Players[actorSeat];
            PlayerState opponent = state.Players[1 - actorSeat];
            return new PlayerMemory(
                SortedDefinitions(self.Deck),
                SortedDefinitions(self.Graveyard),
                SortedDefinitions(opponent.Graveyard),
                knownOpponentHand);
        }

        private static IReadOnlyList<int> SortedDefinitions(IReadOnlyList<CardInstance> cards)
        {
            var definitions = new List<int>(cards.Count);
            foreach (CardInstance card in cards) definitions.Add(card.DefinitionId);
            definitions.Sort();
            return definitions;
        }

        private static IReadOnlyList<int> PubliclyPlayedDefinitions(
            GameState projectedActorBefore,
            IReadOnlyList<GameEvent> publicEvents,
            PlayerId actor)
        {
            PlayerState actingPlayer = projectedActorBefore.PlayerOf(actor);
            var definitions = new List<int>();
            foreach (GameEvent gameEvent in publicEvents)
            {
                if (gameEvent.Kind != GameEventKind.CardPlayed || !gameEvent.Source.HasValue)
                    continue;
                foreach (CardInstance card in actingPlayer.Hand)
                    if (card.Id == gameEvent.Source.Value)
                    {
                        definitions.Add(card.DefinitionId);
                        break;
                    }
            }
            return definitions;
        }

        private static List<JsonElement> EncodeActions(IReadOnlyList<PlayerIntent> actions, MatchId match)
        {
            var encoded = new List<JsonElement>(actions.Count);
            foreach (PlayerIntent action in actions) encoded.Add(EncodeIntent(action, match));
            return encoded;
        }

        private static JsonElement EncodeIntent(PlayerIntent intent, MatchId match)
        {
            byte[] envelope = ProtocolCodec.Encode(new IntentMsg(0UL, intent), match, 0UL);
            using JsonDocument document = JsonDocument.Parse(envelope);
            return document.RootElement.GetProperty("body").GetProperty("intent").Clone();
        }

        private static int FindAction(IReadOnlyList<JsonElement> actions, JsonElement chosen)
        {
            string chosenText = chosen.GetRawText();
            for (int i = 0; i < actions.Count; i++)
                if (string.Equals(actions[i].GetRawText(), chosenText, StringComparison.Ordinal)) return i;
            return -1;
        }

        private PlayerIntent NormalizeIntent(GameState state, PlayerIntent intent)
        {
            if (intent is not PlayCardIntent play) return intent;
            PlayerState player = state.PlayerOf(play.Player);
            foreach (CardInstance card in player.Hand)
            {
                if (card.Id != play.Card) continue;
                return _db.Get(card.DefinitionId).Type == CardType.MINION
                    ? intent
                    : play with { BoardSlot = 0 };
            }
            return intent;
        }

        private static int RewardFor(MatchOutcome outcome, int seat)
        {
            if (outcome == MatchOutcome.Draw) return 0;
            bool won = outcome == MatchOutcome.Player0Win ? seat == 0 : seat == 1;
            return won ? 1 : -1;
        }

        private static FileStream DurableCreate(string path)
            => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                              64 * 1024, FileOptions.WriteThrough);

        private static void WriteLine<T>(FileStream stream, T value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, CorpusJson.Options);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte((byte)'\n');
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }

        private sealed record ReplayRecord(ulong Seed, string P0, string P1,
                                           IReadOnlyList<PlayerIntent> Intents, MatchOutcome Outcome);

        private sealed record CorpusEpisodeRow(string MatchId, MatchOutcome Outcome, int Decisions,
                                               string P0Policy, string P1Policy);

        private sealed record CorpusDecisionRow(
            string MatchId,
            int DecisionIndex,
            string Actor,
            ulong StateVersion,
            string ObservationHash,
            JsonElement Observation,
            PlayerMemory PlayerMemory,
            IReadOnlyList<JsonElement> LegalActions,
            int ChosenActionIndex,
            JsonElement ChosenAction,
            string PostActionObservationHash,
            int Reward,
            bool Done,
            int EpisodeReturn);

        private sealed record PlayerMemory(
            IReadOnlyList<int> SelfRemainingDeck,
            IReadOnlyList<int> SelfGraveyard,
            IReadOnlyList<int> OpponentGraveyard,
            IReadOnlyList<int> KnownOpponentHand);
    }
}
