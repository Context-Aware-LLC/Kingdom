using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Emberfall.Review
{
    /// <summary>
    /// The grading sidecar. Labels live here and NOWHERE ELSE — the bundle is evidence and this
    /// file is judgement, and a tool that let the second write into the first would eventually
    /// produce a decision whose recorded state had been edited to suit its grade.
    ///
    /// Append-only JSONL, last row per decision wins. Append rather than rewrite because a review
    /// pass is long and interruptible: a crash halfway through costs the last row, never the file.
    /// The history that accumulates is a feature — a changed grade keeps the grade it replaced.
    ///
    /// Every row carries the identities the grade is only valid under: the observation hash, the
    /// card-data hash and the checkpoint hash. A label about a decision made against different
    /// cards, or by a different policy, is a label about a different thing.
    /// </summary>
    public sealed class ReviewAnnotations
    {
        public const int SchemaVersion = 1;

        private readonly string _path;
        private readonly ReviewBundleManifest _manifest;
        private readonly Dictionary<string, Annotation> _byDecision =
            new Dictionary<string, Annotation>(StringComparer.Ordinal);

        private ReviewAnnotations(string path, ReviewBundleManifest manifest)
        {
            _path = path;
            _manifest = manifest;
        }

        public string Path => _path;
        public int Count => _byDecision.Count;

        /// <summary>
        /// Sidecars sit BESIDE the bundle directory, not inside it. Inside, a bundle's own hash
        /// checks would start failing the moment anyone graded anything.
        /// </summary>
        public static ReviewAnnotations Open(ReviewBundle bundle, string reviewer)
        {
            string parent = System.IO.Path.GetDirectoryName(
                bundle.Directory.TrimEnd(System.IO.Path.DirectorySeparatorChar))
                ?? bundle.Directory;
            string directory = System.IO.Path.Combine(parent, "annotations");
            Directory.CreateDirectory(directory);

            string file = System.IO.Path.Combine(
                directory, $"{Sanitize(reviewer)}-{Sanitize(bundle.Manifest.BundleId)}.jsonl");
            var store = new ReviewAnnotations(file, bundle.Manifest);
            store.Load();
            return store;
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                if (line.Length == 0) continue;
                Annotation row;
                try { row = Annotation.Read(JObject.Parse(line)); }
                catch (JsonException) { continue; }   // a torn last line loses that row, not the file
                _byDecision[Key(row.MatchId, row.DecisionIndex)] = row;   // last wins
            }
        }

        public Annotation For(ReviewFrame frame) =>
            _byDecision.TryGetValue(Key(frame.MatchId, frame.DecisionIndex), out Annotation row)
                ? row
                : null;

        /// <summary>True once a decision carries any judgement at all. A comment alone counts —
        /// "I looked at this and had something to say" is a review.</summary>
        public bool IsGraded(ReviewFrame frame)
        {
            Annotation row = For(frame);
            return row != null && !row.IsEmpty;
        }

        public void Save(ReviewFrame frame, string reviewer, Annotation edit)
        {
            var row = new Annotation
            {
                MatchId = frame.MatchId,
                DecisionIndex = frame.DecisionIndex,
                ObservationHash = frame.ObservationHash,
                Reviewer = reviewer,
                AnnotatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                StructuralClass = edit.StructuralClass,
                TacticalMotif = edit.TacticalMotif,
                Reasonableness = edit.Reasonableness,
                ConfidenceAppropriateness = edit.ConfidenceAppropriateness,
                Comment = edit.Comment,
            };

            var json = new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["bundleId"] = _manifest.BundleId,
                ["matchId"] = row.MatchId,
                ["decisionIndex"] = row.DecisionIndex,
                ["observationHash"] = row.ObservationHash,
                ["cardDataSha256"] = _manifest.CardDataSha256,
                ["checkpointSha256"] = _manifest.CheckpointSha256,
                ["reviewer"] = row.Reviewer,
                ["annotatedAtUtc"] = row.AnnotatedAtUtc,
                ["structuralClass"] = row.StructuralClass ?? "",
                ["tacticalMotif"] = new JArray(row.TacticalMotif ?? Array.Empty<string>()),
                ["reasonableness"] = row.Reasonableness,
                ["confidenceAppropriateness"] = row.ConfidenceAppropriateness,
                ["comment"] = row.Comment ?? "",
            };

            File.AppendAllText(_path, json.ToString(Formatting.None) + "\n", new UTF8Encoding(false));
            _byDecision[Key(row.MatchId, row.DecisionIndex)] = row;
        }

        private static string Key(string matchId, int decisionIndex) =>
            matchId + "#" + decisionIndex.ToString(CultureInfo.InvariantCulture);

        private static string Sanitize(string name)
        {
            var text = new StringBuilder(name.Length);
            foreach (char c in name)
                text.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            return text.Length == 0 ? "reviewer" : text.ToString();
        }
    }

    /// <summary>
    /// One decision's judgement. The three labels are the handoff's three, independent by design:
    /// whether the ACTION SET was really offering distinct choices, what KIND of decision it was,
    /// and whether the pick was REASONABLE. Collapsing any two of them is what produced compound
    /// labels like "routine-structural-alias" in the first hand pass.
    /// </summary>
    public sealed class Annotation
    {
        /// <summary>One of the bundle's structural values, or empty.</summary>
        public string StructuralClass { get; set; } = "";

        /// <summary>
        /// OPEN, deliberately. The bundle offers suggestions, but the first hand pass over these
        /// same 30 decisions produced 60 distinct motif tags in 73 uses — 52 of them used exactly
        /// once. A closed list over a vocabulary that has not converged would throw away most of
        /// what the tags actually captured. Tighten it once terms start repeating.
        /// </summary>
        public string[] TacticalMotif { get; set; } = Array.Empty<string>();

        /// <summary>Sam's 1-5 scale for the PLAY itself; 0 means ungraded.</summary>
        public int Reasonableness { get; set; }

        /// <summary>
        /// 1-3: was the expressed confidence appropriate FOR THIS SITUATION, independent of whether
        /// the play was any good? 0 means ungraded.
        ///
        /// Separate from <see cref="Reasonableness"/> on Sam's instruction, and the separation is
        /// the whole point: an ambiguous spot can be overstated, while an obvious play can be
        /// understated. Fused into one number those calibration failures disappear.
        /// </summary>
        public int ConfidenceAppropriateness { get; set; }

        public string Comment { get; set; } = "";

        public string MatchId { get; set; } = "";
        public int DecisionIndex { get; set; }
        public string ObservationHash { get; set; } = "";
        public string Reviewer { get; set; } = "";
        public string AnnotatedAtUtc { get; set; } = "";

        public bool IsEmpty =>
            string.IsNullOrEmpty(StructuralClass)
            && (TacticalMotif == null || TacticalMotif.Length == 0)
            && Reasonableness == 0
            && ConfidenceAppropriateness == 0
            && string.IsNullOrWhiteSpace(Comment);

        public Annotation Clone() => new Annotation
        {
            StructuralClass = StructuralClass,
            TacticalMotif = (string[])(TacticalMotif ?? Array.Empty<string>()).Clone(),
            Reasonableness = Reasonableness,
            ConfidenceAppropriateness = ConfidenceAppropriateness,
            Comment = Comment,
            MatchId = MatchId,
            DecisionIndex = DecisionIndex,
            ObservationHash = ObservationHash,
            Reviewer = Reviewer,
            AnnotatedAtUtc = AnnotatedAtUtc,
        };

        internal static Annotation Read(JObject json)
        {
            var motifs = new List<string>();
            if (json["tacticalMotif"] is JArray array)
                foreach (JToken tag in array)
                {
                    string text = (string)tag;
                    if (!string.IsNullOrWhiteSpace(text)) motifs.Add(text);
                }

            return new Annotation
            {
                MatchId = (string)json["matchId"] ?? "",
                DecisionIndex = (int?)json["decisionIndex"] ?? 0,
                ObservationHash = (string)json["observationHash"] ?? "",
                Reviewer = (string)json["reviewer"] ?? "",
                AnnotatedAtUtc = (string)json["annotatedAtUtc"] ?? "",
                StructuralClass = (string)json["structuralClass"] ?? "",
                TacticalMotif = motifs.ToArray(),
                Reasonableness = (int?)json["reasonableness"] ?? 0,
                ConfidenceAppropriateness = (int?)json["confidenceAppropriateness"] ?? 0,
                Comment = (string)json["comment"] ?? "",
            };
        }
    }
}
