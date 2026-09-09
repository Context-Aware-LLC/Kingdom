using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Review
{
    /// <summary>
    /// The reviewer's panel: where you are in the bundle, what the policy chose, and what it could
    /// have chosen instead.
    ///
    /// An EditorWindow rather than an in-scene overlay, for two reasons. It does not cover the
    /// board — the board IS the thing being judged — and it gives grading somewhere real to put
    /// text fields and keyboard focus when the annotation pass lands.
    ///
    /// Throwaway by design (docs/sam/decision-review-poc-plan-2026-09-08.md). IMGUI, no UXML, no
    /// assets: nothing here should survive into a durable tool unexamined.
    /// </summary>
    public sealed class DecisionReviewWindow : EditorWindow
    {
        private DecisionReviewBootstrap _reviewer;
        private Vector2 _scroll;

        [MenuItem("Emberfall/Review/Decision Reviewer Panel", priority = 102)]
        public static void Open()
        {
            DecisionReviewWindow window = GetWindow<DecisionReviewWindow>("Decision Review");
            window.minSize = new Vector2(380f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Unsubscribe();
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            Unsubscribe();
            Repaint();
        }

        private void Unsubscribe()
        {
            if (_reviewer != null) _reviewer.Changed -= Repaint;
            _reviewer = null;
        }

        /// <summary>
        /// The reviewer only exists in play mode, and the object is rebuilt whenever play mode is
        /// re-entered, so this re-finds it rather than caching across sessions.
        /// </summary>
        private bool TryBind()
        {
            if (_reviewer != null) return true;
            if (!EditorApplication.isPlaying) return false;

            _reviewer = FindFirstObjectByType<DecisionReviewBootstrap>();
            if (_reviewer == null) return false;
            _reviewer.Changed += Repaint;
            return true;
        }

        private void OnGUI()
        {
            if (!TryBind())
            {
                EditorGUILayout.HelpBox(
                    EditorApplication.isPlaying
                        ? "No DecisionReviewBootstrap in the scene."
                        : "Not in play mode. Use Emberfall ▸ Review ▸ Open Decision Reviewer.",
                    MessageType.Info);
                return;
            }

            ReviewFrame frame = _reviewer.Frame;
            if (frame == null)
            {
                EditorGUILayout.HelpBox("The bundle loaded no frames.", MessageType.Warning);
                return;
            }

            // The edit buffer follows the frame, so stepping away never carries one decision's
            // half-typed comment onto the next.
            if (_editingKey != FrameKey(frame)) LoadEdit(frame);

            DrawNavigation(frame);
            EditorGUILayout.Space(4f);
            DrawIdentity(frame);
            EditorGUILayout.Space(6f);
            DrawActions(frame);
            EditorGUILayout.Space(6f);
            DrawGrading(frame);
        }

        // ── Grading ─────────────────────────────────────────────────────────────────────────

        private string _editingKey = "";
        private Annotation _edit = new Annotation();
        private string _motifText = "";

        private static string FrameKey(ReviewFrame frame) => frame.MatchId + "#" + frame.DecisionIndex;

        private void LoadEdit(ReviewFrame frame)
        {
            _editingKey = FrameKey(frame);
            Annotation saved = _reviewer.Annotations?.For(frame);
            _edit = saved != null ? saved.Clone() : new Annotation();
            _motifText = string.Join(", ", _edit.TacticalMotif ?? new string[0]);
            GUI.FocusControl(null);
        }

        /// <summary>
        /// The handoff's three labels plus a comment. They are SEPARATE FIELDS on purpose: the
        /// first hand pass over these decisions had only one label slot and produced compound
        /// values like "routine-structural-alias", which is the structural axis and the verdict
        /// axis fused into a string neither can be read back out of.
        /// </summary>
        private void DrawGrading(ReviewFrame frame)
        {
            ReviewVocabulary vocabulary = _reviewer.Vocabulary;
            if (vocabulary == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Grade", EditorStyles.boldLabel, GUILayout.Width(48f));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{_reviewer.GradedCount} / {_reviewer.FrameCount} done",
                                    EditorStyles.miniLabel);
                    // The vocabulary is a file you can edit while this is open; this re-reads it.
                    if (GUILayout.Button("↻ vocab", EditorStyles.miniButton)) vocabulary.Reload();
                }

                // 1. Was the action set really offering distinct choices?
                _edit.StructuralClass = DrawOptionalPopup(
                    "Structural", _edit.StructuralClass, vocabulary.StructuralValues);
                DrawAddValue(ref _newStructural, "add structural value",
                             v => vocabulary.AddStructural(v));

                // 2. What kind of decision was it? Open — see Annotation.TacticalMotif.
                _motifText = EditorGUILayout.TextField("Motif", _motifText);
                DrawMotifSuggestions(vocabulary);

                // 3. Was the pick reasonable? Sam's scale, quoted from the review document.
                DrawReasonableness(vocabulary.ReasonablenessLabels);

                EditorGUILayout.LabelField("Comment");
                _edit.Comment = EditorGUILayout.TextArea(
                    _edit.Comment ?? "", GUILayout.MinHeight(48f));

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Save", GUILayout.Height(22f))) Save(frame);
                    if (GUILayout.Button("Save & Next ▶", GUILayout.Height(22f)))
                    {
                        Save(frame);
                        _reviewer.Next();
                    }
                }
            }
        }

        private void Save(ReviewFrame frame)
        {
            _edit.TacticalMotif = SplitTags(_motifText);
            _reviewer.Annotations?.Save(frame, _reviewer.Reviewer, _edit);

            // A tag you actually used is a better suggestion than one nobody has. This is the only
            // way the motif list is meant to grow — by use, not by anyone deciding it up front.
            if (_reviewer.Vocabulary != null)
                foreach (string tag in _edit.TacticalMotif) _reviewer.Vocabulary.AddMotif(tag);

            Repaint();
        }

        /// <summary>A popup that can also mean "I have not decided" — the default, and never a
        /// silently-recorded first value.</summary>
        private static string DrawOptionalPopup(string label, string current, IReadOnlyList<string> values)
        {
            var options = new string[values.Count + 1];
            options[0] = "(unset)";
            for (int i = 0; i < values.Count; i++) options[i + 1] = values[i];

            int index = 0;
            for (int i = 0; i < values.Count; i++)
                if (string.Equals(values[i], current, StringComparison.Ordinal)) { index = i + 1; break; }

            int picked = EditorGUILayout.Popup(label, index, options);
            return picked <= 0 ? "" : options[picked];
        }

        private void DrawReasonableness(IReadOnlyList<string> labels)
        {
            var options = new string[labels.Count + 1];
            options[0] = "(unset)";
            for (int i = 0; i < labels.Count; i++) options[i + 1] = labels[i];

            int index = _edit.Reasonableness >= 1 && _edit.Reasonableness <= labels.Count
                ? _edit.Reasonableness
                : 0;
            int picked = EditorGUILayout.Popup("Reasonable?", index, options);
            _edit.Reasonableness = picked;
        }

        private string _newStructural = "";

        /// <summary>Grow a list without leaving the panel. The vocabulary is still being discovered,
        /// so the moment you notice a missing value is the moment you should be able to add it.</summary>
        private static void DrawAddValue(ref string buffer, string placeholder, Func<string, bool> add)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                buffer = EditorGUILayout.TextField(buffer);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(buffer)))
                    if (GUILayout.Button("＋", EditorStyles.miniButton, GUILayout.Width(26f)))
                    {
                        add(buffer);
                        buffer = "";
                        GUI.FocusControl(null);
                    }
            }
            GUILayout.Label($"    {placeholder}", EditorStyles.miniLabel);
        }

        private void DrawMotifSuggestions(ReviewVocabulary vocabulary)
        {
            // Suggestions grow with use: anything already typed into this sidecar is offered back,
            // which is how a vocabulary converges without anyone declaring it closed.
            IReadOnlyList<string> suggestions = vocabulary.MotifSuggestions;
            if (suggestions.Count == 0) return;
            EditorGUILayout.LabelField(
                "suggestions — free text, add your own", EditorStyles.miniLabel);

            float width = Mathf.Max(120f, position.width - 24f);
            int perRow = Mathf.Max(1, Mathf.FloorToInt(width / 110f));
            for (int i = 0; i < suggestions.Count; i += perRow)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int j = i; j < Mathf.Min(i + perRow, suggestions.Count); j++)
                        if (GUILayout.Button(suggestions[j], EditorStyles.miniButton))
                            _motifText = AppendTag(_motifText, suggestions[j]);
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private static string AppendTag(string current, string tag)
        {
            foreach (string existing in SplitTags(current))
                if (string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)) return current;
            return string.IsNullOrWhiteSpace(current) ? tag : current.TrimEnd() + ", " + tag;
        }

        private static string[] SplitTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new string[0];
            string[] parts = text.Split(',');
            var tags = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) tags.Add(trimmed);
            }
            return tags.ToArray();
        }

        /// <summary>
        /// Buttons first, keys second, and a slider to jump.
        ///
        /// The first cut offered arrow keys and little else, and Sam's reaction was "I don't
        /// understand the controls really" — fair, because the keys only fire while THIS window
        /// holds focus, and nothing said so. A control whose precondition is invisible is not a
        /// control.
        /// </summary>
        private void DrawNavigation(ReviewFrame frame)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_reviewer.CurrentFrame <= 0))
                    if (GUILayout.Button("◀  Previous", GUILayout.Height(24f))) _reviewer.Previous();

                // The dot says this decision already carries a judgement — the one thing you need
                // at a glance when picking a 30-state pass back up.
                bool graded = _reviewer.Annotations != null && _reviewer.Annotations.IsGraded(frame);
                GUILayout.Label($"{(graded ? "●" : "○")} {_reviewer.CurrentFrame + 1} / {_reviewer.FrameCount}",
                                EditorStyles.boldLabel, GUILayout.Width(66f));

                using (new EditorGUI.DisabledScope(_reviewer.CurrentFrame >= _reviewer.FrameCount - 1))
                    if (GUILayout.Button("Next  ▶", GUILayout.Height(24f))) _reviewer.Next();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Jump", GUILayout.Width(34f));
                int target = Mathf.RoundToInt(GUILayout.HorizontalSlider(
                    _reviewer.CurrentFrame + 1, 1f, Mathf.Max(1, _reviewer.FrameCount)));
                if (target - 1 != _reviewer.CurrentFrame) _reviewer.Show(target - 1);
            }

            GUILayout.Label("← / → also step, while this window has focus.", EditorStyles.miniLabel);
            GUILayout.Label(frame.Caption, EditorStyles.miniBoldLabel);

            Event e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.LeftArrow) { _reviewer.Previous(); e.Use(); }
                else if (e.keyCode == KeyCode.RightArrow) { _reviewer.Next(); e.Use(); }
            }
        }

        private void DrawIdentity(ReviewFrame frame)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Decision", $"{frame.MatchId} #{frame.DecisionIndex}");
                EditorGUILayout.LabelField("Actor", $"{frame.Actor} · turn {frame.Turn}");
                EditorGUILayout.LabelField("Observation", frame.ObservationHash.Substring(0, 16) + "…");

                // Shown as CONTEXT and labelled as such. The review document is explicit that the
                // episode result is not evidence the action was good — a won game does not redeem a
                // bad play and a lost one does not condemn a forced move.
                string outcome = frame.EpisodeReturnForActor > 0 ? "actor won"
                    : frame.EpisodeReturnForActor < 0 ? "actor lost" : "draw";
                EditorGUILayout.LabelField("Eventually", $"{outcome}  (context, not proof)");
            }
        }

        private void DrawActions(ReviewFrame frame)
        {
            EditorGUILayout.LabelField($"Legal actions ({frame.Actions.Count})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Click one to light up what it names. Probabilities are the policy's, over this set.",
                EditorStyles.miniLabel);

            // What the highlight COULD and could not do. Without this line a hero-targeting action
            // looks like a broken feature instead of a stated limitation.
            if (!string.IsNullOrEmpty(_reviewer.HighlightNote))
                EditorGUILayout.HelpBox(_reviewer.HighlightNote, MessageType.None);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < frame.Actions.Count; i++)
            {
                ReviewAction action = frame.Actions[i];
                bool selected = i == _reviewer.SelectedAction;

                using (new EditorGUILayout.HorizontalScope(
                           selected ? EditorStyles.helpBox : GUIStyle.none))
                {
                    GUILayout.Label(action.Chosen ? "●" : "○", GUILayout.Width(16f));
                    GUILayout.Label(FormatProbability(action.Probability),
                                    action.Chosen ? EditorStyles.boldLabel : EditorStyles.label,
                                    GUILayout.Width(84f));

                    if (GUILayout.Button(action.Description, EditorStyles.linkLabel))
                        _reviewer.SelectAction(i);
                    GUILayout.FlexibleSpace();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Probabilities in this corpus span 1.0 down to about 7e-172, so a fixed percentage would
        /// print "0.000%" for almost every alternative and hide the ordering entirely. Small values
        /// keep their exponent.
        ///
        /// Deliberately NOT aggregated. The sample's own reading is that two placements of the same
        /// card, or two copies of it, split a distribution without splitting a decision — but which
        /// entries are aliases of each other is a JUDGEMENT, and it is one of the three labels the
        /// reviewer is here to record. Collapsing them here would make that call silently and
        /// destroy the evidence for it.
        /// </summary>
        private static string FormatProbability(double p)
        {
            if (p >= 0.0001d)
                return (p * 100d).ToString("0.000", CultureInfo.InvariantCulture) + "%";
            if (p <= 0d) return "0";
            return p.ToString("0.0e+0", CultureInfo.InvariantCulture);
        }
    }
}
