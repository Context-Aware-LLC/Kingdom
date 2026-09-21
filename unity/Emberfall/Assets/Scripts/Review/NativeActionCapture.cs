using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Emberfall.Review
{
    /// <summary>
    /// A separate, append-only lane for one demonstrated action per frozen state. These are not
    /// preferred sets and unchosen actions are not negative labels. The bundle and the older
    /// audit-annotation sidecar are never modified.
    /// </summary>
    public sealed class NativeActionCapture
    {
        private const int SchemaVersion = 1;
        private readonly ReviewBundle _bundle;
        private readonly string _reviewer;
        private readonly Dictionary<int, JObject> _latest = new Dictionary<int, JObject>();

        private NativeActionCapture(ReviewBundle bundle, string reviewer, string path)
        {
            _bundle = bundle;
            _reviewer = reviewer;
            Path = path;
            Load();
        }

        public string Path { get; }

        public static NativeActionCapture Open(ReviewBundle bundle, string reviewer)
        {
            string parent = System.IO.Path.GetDirectoryName(
                bundle.Directory.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? bundle.Directory;
            string directory = System.IO.Path.Combine(parent, "actions");
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory,
                Sanitize(reviewer) + "-" + Sanitize(bundle.Manifest.BundleId) + ".jsonl");
            return new NativeActionCapture(bundle, reviewer, path);
        }

        public bool IsCaptured(ReviewFrame frame) => _latest.ContainsKey(frame.FrameIndex);

        public bool WasSkipped(ReviewFrame frame) =>
            _latest.TryGetValue(frame.FrameIndex, out JObject row) && (bool?)row["unreviewable"] == true;

        public int Count => _latest.Count;

        /// <summary>Only an exact member of this frame's legal list may be recorded.</summary>
        public bool TryRecord(ReviewFrame frame, PlayerIntent intent, out string error)
        {
            error = "";
            if (intent is ConcedeIntent)
            {
                error = "Concede is not a review choice.";
                return false;
            }
            if (!string.Equals(intent.Player.Value, frame.Actor, StringComparison.Ordinal))
            {
                error = "Intent is not from this frame's actor.";
                return false;
            }

            byte[] encoded = ProtocolCodec.Encode(
                new IntentMsg(0UL, intent), new MatchId(frame.MatchId), 0UL);
            JObject envelope = JObject.Parse(Encoding.UTF8.GetString(encoded));
            JToken wireIntent = envelope["body"]?["intent"];
            for (int i = 0; i < frame.Actions.Count; i++)
            {
                if (!JToken.DeepEquals(wireIntent, frame.Actions[i].Intent)) continue;
                Append(frame, i, frame.Actions[i].Intent, false);
                return true;
            }
            error = "That gesture did not match a legal action in this frozen state.";
            return false;
        }

        public void Skip(ReviewFrame frame) => Append(frame, -1, null, true);

        private void Append(ReviewFrame frame, int position, JObject intent, bool unreviewable)
        {
            var row = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["bundleId"] = _bundle.Manifest.BundleId,
                ["frameIndex"] = frame.FrameIndex,
                ["matchId"] = frame.MatchId,
                ["decisionIndex"] = frame.DecisionIndex,
                ["observationHash"] = frame.ObservationHash,
                ["cardDataSha256"] = _bundle.Manifest.CardDataSha256,
                ["checkpointSha256"] = _bundle.Manifest.CheckpointSha256,
                ["reviewer"] = _reviewer,
                ["recordedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["actionPosition"] = position,
                ["actionIndex"] = position < 0 ? -1 : frame.Actions[position].Index,
                ["intent"] = intent?.DeepClone(),
                ["unreviewable"] = unreviewable,
            };
            File.AppendAllText(Path, row.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
            _latest[frame.FrameIndex] = row;
        }

        private void Load()
        {
            if (!File.Exists(Path)) return;
            string[] lines = File.ReadAllLines(Path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                JObject row;
                try { row = JObject.Parse(lines[i]); }
                catch (JsonException) when (i == lines.Length - 1) { break; }
                int frameIndex = (int?)row["frameIndex"] ?? -1;
                if (frameIndex < 0 || frameIndex >= _bundle.Frames.Count)
                    throw new InvalidDataException($"Action sidecar row {i + 1} has invalid frame index.");
                ReviewFrame frame = _bundle.Frames[frameIndex];
                if ((int?)row["schemaVersion"] != SchemaVersion
                    || (string)row["bundleId"] != _bundle.Manifest.BundleId
                    || (string)row["matchId"] != frame.MatchId
                    || (int?)row["decisionIndex"] != frame.DecisionIndex
                    || (string)row["observationHash"] != frame.ObservationHash
                    || (string)row["cardDataSha256"] != _bundle.Manifest.CardDataSha256
                    || (string)row["checkpointSha256"] != _bundle.Manifest.CheckpointSha256
                    || (string)row["reviewer"] != _reviewer)
                    throw new InvalidDataException($"Action sidecar row {i + 1} does not match this bundle.");
                int position = (int?)row["actionPosition"] ?? -1;
                bool skipped = (bool?)row["unreviewable"] == true;
                if (skipped ? position != -1 || row["intent"]?.Type != JTokenType.Null
                    : position < 0 || position >= frame.Actions.Count
                      || !JToken.DeepEquals(row["intent"], frame.Actions[position].Intent)
                      || (int?)row["actionIndex"] != frame.Actions[position].Index)
                    throw new InvalidDataException($"Action sidecar row {i + 1} is not a legal capture.");
                _latest[frameIndex] = row;
            }
        }

        private static string Sanitize(string name)
        {
            var result = new StringBuilder(name.Length);
            foreach (char c in name)
                result.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            return result.Length == 0 ? "reviewer" : result.ToString();
        }
    }
}
