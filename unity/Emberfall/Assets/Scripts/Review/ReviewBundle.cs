using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;
using Emberfall.Data;
using Emberfall.Net.Protocol;
using Newtonsoft.Json.Linq;

namespace Emberfall.Review
{
    /// <summary>
    /// Loads a decision-review bundle produced by <c>Emberfall.Tools.DecisionReview build</c>, and
    /// REFUSES anything it cannot prove.
    ///
    /// Every load path here ends in one of two places: a state the engine agrees with, or an
    /// exception naming which identity broke. There is deliberately no third outcome — a review
    /// tool that renders an approximate board gets graded anyway, and the grade then belongs to a
    /// decision nobody made.
    ///
    /// Single-turn by scope (Sam, 2026-09-08): a frame is one still. Nothing here advances a match.
    /// </summary>
    public sealed class ReviewBundle
    {
        public const int SupportedSchemaVersion = 1;

        private ReviewBundle(string directory, ReviewBundleManifest manifest,
                             IReadOnlyList<ReviewFrame> frames, ICardDatabase cards)
        {
            Directory = directory;
            Manifest = manifest;
            Frames = frames;
            Cards = cards;
        }

        public string Directory { get; }
        public ReviewBundleManifest Manifest { get; }
        public IReadOnlyList<ReviewFrame> Frames { get; }

        /// <summary>The FROZEN definitions the decisions were made against — never the repo's
        /// current card data. <c>CardDatabaseLoader.Load()</c> would silently serve the latter, and
        /// a board rendered from it looks completely fine while every card text is from a
        /// different day.</summary>
        public ICardDatabase Cards { get; }

        public static ReviewBundle Load(string directory)
        {
            string root = Path.GetFullPath(directory);
            string manifestPath = Path.Combine(root, "bundle-manifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException($"No bundle-manifest.json in '{root}'.");

            JObject manifestJson = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var manifest = ReviewBundleManifest.Read(manifestJson);

            if (manifest.SchemaVersion != SupportedSchemaVersion)
                throw new InvalidDataException(
                    $"Bundle schema {manifest.SchemaVersion} is not supported by this build "
                    + $"({SupportedSchemaVersion}). Rebuild the bundle.");

            // The wire contract is a compile-time constant shared with the engine. If it has moved
            // under this bundle, the observation may still decode and mean something else.
            if (!string.Equals(manifest.WireContractVersion, ProtocolContract.Version, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Bundle speaks wire contract {manifest.WireContractVersion}; this build speaks "
                    + $"{ProtocolContract.Version}. Rebuild the bundle rather than rendering an "
                    + "approximate state.");

            ICardDatabase cards = LoadFrozenCards(root, manifest);
            IReadOnlyList<ReviewFrame> frames = LoadFrames(root, manifest);
            return new ReviewBundle(root, manifest, frames, cards);
        }

        private static ICardDatabase LoadFrozenCards(string root, ReviewBundleManifest manifest)
        {
            string cardsDirectory = Path.Combine(root, "cards");
            var payloads = new List<string>(manifest.CardFiles.Count);
            foreach (ReviewCardFile card in manifest.CardFiles)
            {
                string path = Path.Combine(cardsDirectory, card.File);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Bundle is missing card file '{card.File}'.");

                byte[] bytes = File.ReadAllBytes(path);
                string actual;
                using (var sha = SHA256.Create()) actual = Hex(sha.ComputeHash(bytes));
                if (!string.Equals(actual, card.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Bundle card file '{card.File}' hashes to {actual}, manifest says {card.Sha256}. "
                        + "These are not the definitions the decisions were made against.");
                payloads.Add(new UTF8Encoding(false).GetString(bytes));
            }
            if (payloads.Count == 0)
                throw new InvalidDataException("Bundle declares no card files.");
            return new JsonCardDatabase(payloads);
        }

        private static IReadOnlyList<ReviewFrame> LoadFrames(string root, ReviewBundleManifest manifest)
        {
            string framesPath = Path.Combine(root, "frames.jsonl");
            if (!File.Exists(framesPath))
                throw new FileNotFoundException($"No frames.jsonl in '{root}'.");

            var frames = new List<ReviewFrame>(manifest.FrameCount);
            foreach (string line in File.ReadAllLines(framesPath, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                frames.Add(ReviewFrame.Read(JObject.Parse(line)));
            }
            if (frames.Count != manifest.FrameCount)
                throw new InvalidDataException(
                    $"Manifest declares {manifest.FrameCount} frames, frames.jsonl holds {frames.Count}.");
            return frames;
        }

        internal static string Hex(byte[] digest)
        {
            var text = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) text.Append(b.ToString("x2"));
            return text.ToString();
        }
    }

    public sealed class ReviewBundleManifest
    {
        public int SchemaVersion { get; private set; }
        public string BundleId { get; private set; } = "";
        public string WireContractVersion { get; private set; } = "";
        public string EngineContractVersion { get; private set; } = "";
        public string CardDataSha256 { get; private set; } = "";
        public string CheckpointSha256 { get; private set; } = "";
        public string SelectionSeed { get; private set; } = "";
        public int FrameCount { get; private set; }
        public IReadOnlyList<ReviewCardFile> CardFiles { get; private set; } = Array.Empty<ReviewCardFile>();

        internal static ReviewBundleManifest Read(JObject json)
        {
            var cardFiles = new List<ReviewCardFile>();
            if (json["cardFiles"] is JArray array)
                foreach (JToken card in array)
                    cardFiles.Add(new ReviewCardFile(
                        (string)card["file"] ?? "", (string)card["sha256"] ?? ""));

            return new ReviewBundleManifest
            {
                SchemaVersion = (int?)json["schemaVersion"] ?? 0,
                BundleId = (string)json["bundleId"] ?? "",
                WireContractVersion = (string)json["wireContractVersion"] ?? "",
                EngineContractVersion = (string)json["engineContractVersion"] ?? "",
                CardDataSha256 = (string)json["cardDataSha256"] ?? "",
                CheckpointSha256 = (string)json["checkpointSha256"] ?? "",
                SelectionSeed = (string)json["selectionSeed"] ?? "",
                FrameCount = (int?)json["frameCount"] ?? 0,
                CardFiles = cardFiles,
            };
        }
    }

    public sealed class ReviewCardFile
    {
        public ReviewCardFile(string file, string sha256) { File = file; Sha256 = sha256; }
        public string File { get; }
        public string Sha256 { get; }
    }

    /// <summary>One decision, as its actor could see it.</summary>
    public sealed class ReviewFrame
    {
        private readonly JObject _observation;
        private GameState _state;

        private ReviewFrame(JObject observation) => _observation = observation;

        public int FrameIndex { get; private set; }
        public int SampleNumber { get; private set; }
        public string Stratum { get; private set; } = "";
        public string MatchId { get; private set; } = "";
        public int DecisionIndex { get; private set; }
        public string Actor { get; private set; } = "";
        public int Turn { get; private set; }
        public string ObservationHash { get; private set; } = "";
        public int ChosenActionIndex { get; private set; }
        public int EpisodeReturnForActor { get; private set; }
        public IReadOnlyList<ReviewAction> Actions { get; private set; } = Array.Empty<ReviewAction>();

        /// <summary>A short label for the reviewer's chrome — "12 · mid:attack · turn 9 (p1)".</summary>
        public string Caption =>
            $"{SampleNumber} · {Stratum} · turn {Turn} ({Actor})";

        /// <summary>
        /// The decoded state, resolved on first use and cached. Decoding goes through the real
        /// <see cref="ProtocolCodec"/> and the result is re-hashed against
        /// <see cref="ObservationHash"/>, so this either returns the state the corpus recorded or
        /// it throws. There is no lenient path.
        /// </summary>
        public GameState State()
        {
            if (_state != null) return _state;

            // The corpus stored `body.state`; rebuild the envelope it was taken out of. The
            // recorded hash goes in as the envelope's hash, which makes VerifyIntegrity below
            // literally the client's own mirror-integrity check run against the stored bytes.
            var envelope = new JObject
            {
                ["v"] = ProtocolCodec.Version,
                ["matchId"] = MatchId,
                ["seq"] = 0,
                ["type"] = ProtocolCodec.TypeSnapshot,
                ["body"] = new JObject
                {
                    ["stateVersion"] = _observation["stateVersion"],
                    ["hash"] = ObservationHash,
                    ["state"] = _observation,
                },
            };

            DecodeResult decoded = ProtocolCodec.Decode(
                Encoding.UTF8.GetBytes(envelope.ToString(Newtonsoft.Json.Formatting.None)));
            if (!decoded.Ok || !(decoded.Message is SnapshotMsg snapshot))
                throw new InvalidDataException(
                    $"{MatchId}#{DecisionIndex}: the stored observation does not decode as a snapshot "
                    + $"under wire contract {ProtocolContract.Version} ({decoded.Error}: {decoded.Detail}).");

            if (!snapshot.VerifyIntegrity())
                throw new InvalidDataException(
                    $"{MatchId}#{DecisionIndex}: the decoded state hashes to "
                    + $"{GameStateHasher.Hash(snapshot.State)}, the bundle recorded {ObservationHash}.");

            _state = snapshot.State;
            return _state;
        }

        internal static ReviewFrame Read(JObject json)
        {
            var actions = new List<ReviewAction>();
            if (json["legalActions"] is JArray array)
                foreach (JToken action in array)
                    actions.Add(ReviewAction.Read((JObject)action));

            return new ReviewFrame((JObject)json["observation"])
            {
                FrameIndex = (int?)json["frameIndex"] ?? 0,
                SampleNumber = (int?)json["sampleNumber"] ?? 0,
                Stratum = (string)json["stratum"] ?? "",
                MatchId = (string)json["matchId"] ?? "",
                DecisionIndex = (int?)json["decisionIndex"] ?? 0,
                Actor = (string)json["actor"] ?? "",
                Turn = (int?)json["turn"] ?? 0,
                ObservationHash = (string)json["observationHash"] ?? "",
                ChosenActionIndex = (int?)json["chosenActionIndex"] ?? 0,
                EpisodeReturnForActor = (int?)json["episodeReturnForActor"] ?? 0,
                Actions = actions,
            };
        }
    }

    public sealed class ReviewAction
    {
        private ReviewAction(JObject intent) => Intent = intent;

        public int Index { get; private set; }
        public string Description { get; private set; } = "";
        public double Probability { get; private set; }
        public bool Chosen { get; private set; }

        /// <summary>The wire intent. Kept raw: the reviewer reads its target ids to highlight the
        /// board and otherwise does not interpret it.</summary>
        public JObject Intent { get; }

        /// <summary>The entities this action names — its targets, plus an attacker or a played
        /// card. Used only to light up the board; an id the current state does not carry is simply
        /// not highlighted, because an action can name a card in hand and the board has no
        /// presenter for a spell that has not been cast.</summary>
        public IEnumerable<InstanceId> ReferencedEntities()
        {
            foreach (string field in EntityFields)
                if (TryReadInstanceId(Intent[field], out InstanceId single))
                    yield return single;

            if (Intent["targets"] is JArray targets)
                foreach (JToken target in targets)
                    if (TryReadInstanceId(target, out InstanceId listed))
                        yield return listed;
        }

        private static readonly string[] EntityFields = { "attacker", "defender", "card" };

        /// <summary>Wire ids spell as "i&lt;n&gt;" (<see cref="InstanceId.ToString"/>). Anything
        /// else is not an id and is skipped rather than guessed at.</summary>
        private static bool TryReadInstanceId(JToken token, out InstanceId id)
        {
            id = default;
            if (!(token is JValue value) || !(value.Value is string text)) return false;
            if (text.Length < 2 || text[0] != 'i') return false;
            if (!int.TryParse(text.Substring(1), System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int number))
                return false;
            id = new InstanceId(number);
            return true;
        }

        internal static ReviewAction Read(JObject json)
        {
            return new ReviewAction((JObject)json["intent"])
            {
                Index = (int?)json["index"] ?? 0,
                Description = (string)json["description"] ?? "",
                Probability = (double?)json["probability"] ?? 0d,
                Chosen = (bool?)json["chosen"] ?? false,
            };
        }
    }
}
