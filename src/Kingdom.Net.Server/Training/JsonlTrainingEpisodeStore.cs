using System;
using System.Collections.Generic;
using System.IO;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Training.Corpus;

namespace Kingdom.Net.Server.Training
{
    /// <summary>
    /// Collects live decision anchors in memory, then publishes a self-contained raw episode to an
    /// inbox after the ordinary replay footer is durable. The replay is copied before the manifest
    /// row is appended, making the manifest the commit marker consumed by off-box synchronizers.
    /// </summary>
    public sealed class JsonlTrainingEpisodeStore : ITrainingEpisodeStore
    {
        private sealed class PendingEpisode
        {
            public PendingEpisode(
                MatchId id,
                ulong seed,
                PlayerId p0,
                PlayerId p1,
                MatchSetup setup,
                string p0Policy,
                string p1Policy)
            {
                Id = id;
                Seed = seed;
                P0 = p0;
                P1 = p1;
                Setup = new MatchSetup(
                    setup.P0Class,
                    new List<int>(setup.P0Deck),
                    setup.P1Class,
                    new List<int>(setup.P1Deck),
                    setup.HeroMaxHealth,
                    setup.SparkTokenId);
                P0Policy = p0Policy;
                P1Policy = p1Policy;
            }

            public MatchId Id { get; }
            public ulong Seed { get; }
            public PlayerId P0 { get; }
            public PlayerId P1 { get; }
            public MatchSetup Setup { get; }
            public string P0Policy { get; }
            public string P1Policy { get; }
            public List<EpisodeDecisionHash> Decisions { get; } = new List<EpisodeDecisionHash>();
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, PendingEpisode> _pending =
            new Dictionary<string, PendingEpisode>(StringComparer.Ordinal);
        private readonly string _replayDirectory;
        private readonly Func<MatchId, string> _sourceReplayPath;
        private readonly string _cardDataSha256;
        private readonly string _buildRevision;
        private readonly string _wireContractVersion;
        private readonly JsonlEpisodeManifestWriter _writer;

        public JsonlTrainingEpisodeStore(
            string inboxDirectory,
            Func<MatchId, string> sourceReplayPath,
            string cardDataSha256,
            string buildRevision,
            string wireContractVersion)
        {
            if (string.IsNullOrWhiteSpace(inboxDirectory))
                throw new ArgumentException("Training inbox directory is required.", nameof(inboxDirectory));
            _sourceReplayPath = sourceReplayPath ?? throw new ArgumentNullException(nameof(sourceReplayPath));
            _cardDataSha256 = Require(cardDataSha256, nameof(cardDataSha256));
            _buildRevision = Require(buildRevision, nameof(buildRevision));
            _wireContractVersion = Require(wireContractVersion, nameof(wireContractVersion));
            _writer = new JsonlEpisodeManifestWriter(inboxDirectory);
            _replayDirectory = Path.Combine(inboxDirectory, "replays");
            Directory.CreateDirectory(_replayDirectory);
        }

        public void Begin(
            MatchId id,
            ulong seed,
            PlayerId p0,
            PlayerId p1,
            MatchSetup setup,
            string p0Policy,
            string p1Policy)
        {
            if (_writer.Contains(id.Value)) return;
            var pending = new PendingEpisode(
                id,
                seed,
                p0,
                p1,
                setup ?? throw new ArgumentNullException(nameof(setup)),
                Require(p0Policy, nameof(p0Policy)),
                Require(p1Policy, nameof(p1Policy)));
            lock (_gate)
            {
                if (!_pending.TryAdd(id.Value, pending))
                    throw new InvalidOperationException($"Training capture for match '{id.Value}' already started.");
            }
        }

        public void RecordAccepted(
            MatchId id,
            PlayerId actor,
            string beforeObservationSha256,
            string afterObservationSha256)
        {
            lock (_gate)
            {
                if (!_pending.TryGetValue(id.Value, out PendingEpisode? pending)) return;
                pending.Decisions.Add(new EpisodeDecisionHash(
                    pending.Decisions.Count,
                    actor.Value,
                    beforeObservationSha256,
                    afterObservationSha256));
            }
        }

        public void Complete(
            MatchId id,
            MatchOutcome outcome,
            string finalP0ObservationSha256,
            string finalP1ObservationSha256)
        {
            PendingEpisode? pending;
            lock (_gate)
            {
                if (!_pending.Remove(id.Value, out pending)) return;
            }

            if (pending.Decisions.Count == 0)
                throw new InvalidDataException($"Completed match '{id.Value}' has no accepted decisions.");

            string safeName = ReplayFileName(id);
            string destination = Path.Combine(_replayDirectory, safeName);
            CopyReplayAtomically(_sourceReplayPath(id), destination);
            string replayHash = CorpusFiles.Sha256File(destination);
            var setup = pending.Setup;
            _writer.Append(new EpisodeManifest(
                EpisodeManifest.CurrentSchemaVersion,
                id.Value,
                pending.Seed,
                pending.P0.Value,
                pending.P1.Value,
                setup.P0Class,
                setup.P1Class,
                setup.P0Deck,
                setup.P1Deck,
                setup.HeroMaxHealth,
                setup.SparkTokenId,
                EpisodeManifest.CurrentEngineContractVersion,
                _wireContractVersion,
                _buildRevision,
                _cardDataSha256,
                pending.P0Policy,
                pending.P1Policy,
                "replays/" + safeName,
                replayHash,
                finalP0ObservationSha256,
                finalP1ObservationSha256,
                pending.Decisions,
                outcome));
        }

        public void Abandon(MatchId id)
        {
            lock (_gate) _pending.Remove(id.Value);
        }

        private static string ReplayFileName(MatchId id)
        {
            string name = id.Value + ".jsonl";
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Match id '{id.Value}' is not safe as a replay filename.");
            return name;
        }

        private static void CopyReplayAtomically(string source, string destination)
        {
            if (File.Exists(destination))
            {
                if (string.Equals(
                        CorpusFiles.Sha256File(source),
                        CorpusFiles.Sha256File(destination),
                        StringComparison.Ordinal))
                    return;
                throw new IOException($"Inbox replay '{destination}' already exists with different bytes.");
            }

            string partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(
                           partial,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           64 * 1024,
                           FileOptions.WriteThrough))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                File.Move(partial, destination);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{name} is required.", name);
            return value.Trim();
        }
    }
}
