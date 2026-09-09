using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Emberfall.Review
{
    /// <summary>
    /// The label vocabulary — EDITABLE, and deliberately not part of the bundle.
    ///
    /// It lived in bundle-manifest.json for one commit, on the reasoning that a grade must not
    /// change meaning because someone edited a list later. That property is real but the bundle was
    /// the wrong place to get it: the vocabulary is the least settled thing in this whole tool — the
    /// first hand pass produced 60 motif tags in 73 uses — and a bundle rebuild is a scan of a
    /// 1.4 GB corpus. Freezing a work-in-progress behind the most expensive operation available is
    /// backwards. (Sam, 2026-09-08: "that doesn't sound good.. this is a work in progress".)
    ///
    /// The property survives anyway, and more cheaply: an annotation row stores the label STRINGS
    /// it was given, so renaming or removing a value here cannot reach back and rewrite what a past
    /// grade said. Old rows keep their words.
    ///
    /// Lives beside the annotations because it belongs to the reviewer, not to the evidence. Edit
    /// the file by hand, or add values from the panel; it is only ever written when it does not
    /// exist, so nothing here overwrites your edits.
    /// </summary>
    public sealed class ReviewVocabulary
    {
        public const string FileName = "vocabulary.json";

        private readonly string _path;

        private ReviewVocabulary(string path) => _path = path;

        public string Path => _path;

        /// <summary>Pick one. Converged in the first pass — 5 terms, all reused.</summary>
        public List<string> StructuralValues { get; private set; } = new List<string>();

        /// <summary>Suggestions only; the field is free text. NOT a closed list, because this axis
        /// has not converged: 52 of the first pass's 60 motif tags were used exactly once, and a
        /// closed list over that would throw away most of what they captured.</summary>
        public List<string> MotifSuggestions { get; private set; } = new List<string>();

        /// <summary>Index 0 is score 1. Sam's scale for the PLAY.</summary>
        public List<string> ReasonablenessLabels { get; private set; } = new List<string>();

        /// <summary>Index 0 is score 1. Whether near-certainty suited the SITUATION.</summary>
        public List<string> ConfidenceLabels { get; private set; } = new List<string>();

        public static ReviewVocabulary Open(string annotationsDirectory)
        {
            Directory.CreateDirectory(annotationsDirectory);
            string path = System.IO.Path.Combine(annotationsDirectory, FileName);
            var vocabulary = new ReviewVocabulary(path);

            if (File.Exists(path))
            {
                vocabulary.Read(JObject.Parse(File.ReadAllText(path, Encoding.UTF8)));
                return vocabulary;
            }

            vocabulary.Defaults();
            vocabulary.Write();
            return vocabulary;
        }

        /// <summary>Re-read from disk, so editing the file by hand does not need a play-mode restart.</summary>
        public void Reload()
        {
            if (!File.Exists(_path)) { Defaults(); Write(); return; }
            Read(JObject.Parse(File.ReadAllText(_path, Encoding.UTF8)));
        }

        /// <summary>Add a value and persist it. Returns false when it is already there.</summary>
        public bool AddStructural(string value) => Add(StructuralValues, value);

        public bool AddMotif(string value) => Add(MotifSuggestions, value);

        private bool Add(List<string> into, string value)
        {
            string trimmed = (value ?? "").Trim();
            if (trimmed.Length == 0) return false;
            foreach (string existing in into)
                if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase)) return false;
            into.Add(trimmed);
            Write();
            return true;
        }

        private void Read(JObject json)
        {
            StructuralValues = Strings(json["structuralClass"]);
            MotifSuggestions = Strings(json["tacticalMotif"]);
            ReasonablenessLabels = Strings(json["reasonableness"]);
            if (ReasonablenessLabels.Count == 0) ReasonablenessLabels = DefaultScale();
            ConfidenceLabels = Strings(json["confidence"]);
            if (ConfidenceLabels.Count == 0) ConfidenceLabels = DefaultConfidence();
        }

        private static List<string> Strings(JToken token)
        {
            var values = new List<string>();
            if (token is JArray array)
                foreach (JToken item in array)
                {
                    string text = (string)item;
                    if (!string.IsNullOrWhiteSpace(text)) values.Add(text.Trim());
                }
            return values;
        }

        private void Write()
        {
            var json = new JObject
            {
                ["_comment"] = "Edit freely. Annotation rows record the label strings they used, "
                               + "so changing this list never rewrites a grade already recorded.",
                ["structuralClass"] = new JArray(StructuralValues),
                ["tacticalMotif"] = new JArray(MotifSuggestions),
                ["reasonableness"] = new JArray(ReasonablenessLabels),
                ["confidence"] = new JArray(ConfidenceLabels),
            };
            File.WriteAllText(_path, json.ToString(Formatting.Indented), new UTF8Encoding(false));
        }

        /// <summary>
        /// The starting point, from the "Emerging classification axes" table in
        /// docs/sam/rl-confidence-larger-sample-review-2026-09-08.md. A starting point is all it
        /// is — the whole reason this file is editable is that the right answer is not known yet.
        /// </summary>
        private void Defaults()
        {
            StructuralValues = new List<string>
            {
                "genuine-choice",
                "duplicate-card-alias",
                "board-slot-alias",
                "interchangeable-entity-alias",
                "action-order-alias",
            };
            MotifSuggestions = new List<string>
            {
                "development", "removal", "blocker-clearing", "burn",
                "resource-preservation", "shield-timing", "fatigue", "discover-unearth",
            };
            ReasonablenessLabels = DefaultScale();
            ConfidenceLabels = DefaultConfidence();
        }

        private static List<string> DefaultConfidence() => new List<string>
        {
            "1 - Unwarranted. The situation admits real alternatives; near-certainty misrepresents it.",
            "2 - Overstated. Some support for certainty, but not this much.",
            "3 - Warranted. The situation is clear-cut; near-certainty is correct.",
        };

        private static List<string> DefaultScale() => new List<string>
        {
            "1 — Completely wrong / doesn't even make sense",
            "2 — Effectively throwing the game, but could technically be worse",
            "3 — Standard suboptimal play for a new player",
            "4 — Probably not what I would have done, but not definitively wrong off one turn",
            "5 — Absolutely right / no brainer",
        };
    }
}
