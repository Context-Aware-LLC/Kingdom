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

            if (_reviewer.NativeActionMode)
            {
                EditorGUILayout.HelpBox(
                    "Native action capture is active in the Game view. This panel does not grade it.",
                    MessageType.Info);
                return;
            }

            // The edit buffer follows the frame, so stepping away never carries one decision's
            // half-typed comment onto the next.
            if (_editingKey != FrameKey(frame)) LoadEdit(frame);

            // The verdict comes FIRST and takes the keyboard. Measured after Sam's first three
            // grades: score set on all three, structural/motif/comment empty on all three. Every
            // state in the bundle was selected for >=0.99 confidence, so the verdict IS the
            // finding — the rest is optional colour and now sits below it, out of the way.
            if (frame.IsTargetAudit) HandleAuditKeys(frame);
            else HandleScoreKeys(frame);

            DrawNavigation(frame);
            EditorGUILayout.Space(4f);
            if (frame.IsTargetAudit) DrawAuditVerdict(frame);
            else DrawVerdict(frame);
            EditorGUILayout.Space(4f);
            DrawIdentity(frame);
            EditorGUILayout.Space(6f);
            DrawActions(frame);
            EditorGUILayout.Space(6f);
            DrawExtraLabels(frame);
        }

        /// <summary>
        /// 1-5 the play, Q/W/E the confidence, 0 clears both, space or → advances.
        ///
        /// NOTHING AUTO-ADVANCES. It did while there was one axis, and with two that would jump
        /// away the moment the first was answered. Advancing is a keypress of its own so the order
        /// you grade in is yours.
        ///
        /// All of it is ignored while a text field is being edited — otherwise typing "5" into the
        /// comment would grade the decision mid-sentence.
        /// </summary>
        private void HandleScoreKeys(ReviewFrame frame)
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;

            int play = -1;
            if (e.keyCode >= KeyCode.Alpha0 && e.keyCode <= KeyCode.Alpha5)
                play = e.keyCode - KeyCode.Alpha0;
            else if (e.keyCode >= KeyCode.Keypad0 && e.keyCode <= KeyCode.Keypad5)
                play = e.keyCode - KeyCode.Keypad0;

            if (play == 0)   // clear both axes together — "I have not judged this"
            {
                _edit.Reasonableness = 0;
                _edit.ConfidenceAppropriateness = 0;
                Save(frame);
                e.Use();
                return;
            }
            if (play > 0)
            {
                _edit.Reasonableness = play;
                Save(frame);
                e.Use();
                return;
            }

            int confidence = e.keyCode == KeyCode.Q ? 1
                           : e.keyCode == KeyCode.W ? 2
                           : e.keyCode == KeyCode.E ? 3
                                                    : -1;
            if (confidence > 0)
            {
                _edit.ConfidenceAppropriateness = confidence;
                Save(frame);
                e.Use();
                return;
            }

            if (e.keyCode == KeyCode.Space) { _reviewer.Next(); e.Use(); }
        }

        private void HandleAuditKeys(ReviewFrame frame)
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;

            int confidence = -1;
            if (e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha3)
                confidence = e.keyCode - KeyCode.Alpha0;
            else if (e.keyCode >= KeyCode.Keypad1 && e.keyCode <= KeyCode.Keypad3)
                confidence = e.keyCode - KeyCode.Keypad0;
            if (confidence > 0 && !_edit.Unreviewable)
            {
                _edit.AuditConfidence = confidence;
                Save(frame);
                e.Use();
                return;
            }

            if (e.keyCode == KeyCode.U)
            {
                _edit.Unreviewable = !_edit.Unreviewable;
                if (_edit.Unreviewable)
                {
                    _edit.PreferredActionIndices = new int[0];
                    _edit.AuditConfidence = 0;
                }
                Save(frame);
                e.Use();
                return;
            }
            if (e.keyCode == KeyCode.Space) { _reviewer.Next(); e.Use(); }
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
        /// The verdict. Two independent questions: play quality and confidence calibration.
        ///
        /// Sam's play scale is 1-3 fail, 4 indeterminate, 5 pass. Confidence is scored separately
        /// against the probability the policy actually expressed, so both overconfidence and
        /// underconfidence remain visible.
        /// </summary>
        private void DrawVerdict(ReviewFrame frame)
        {
            ReviewVocabulary vocabulary = _reviewer.Vocabulary;
            if (vocabulary == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Verdict", EditorStyles.boldLabel, GUILayout.Width(52f));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(Tally(), EditorStyles.miniLabel);
                    if (GUILayout.Button("↻ vocab", EditorStyles.miniButton)) vocabulary.Reload();
                }

                // Axis 1 — the PLAY. 1..5, keys 1..5.
                GUILayout.Label("Was the play legitimate?   keys 1-5", EditorStyles.miniBoldLabel);
                int play = DrawScoreRow(_edit.Reasonableness, 5, s => s <= 3, s => s == 4);
                if (play != _edit.Reasonableness) { _edit.Reasonableness = play; Save(frame); }
                GUILayout.Label(Line(vocabulary.ReasonablenessLabels, _edit.Reasonableness),
                                EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(4f);

                // Axis 2 — the CONFIDENCE. 1..3, keys Q/W/E. Independent of axis 1 on purpose:
                // either an ambiguous spot can be overstated or an obvious play can be understated,
                // and both cases disappear if confidence and play quality are fused.
                GUILayout.Label("Was the expressed confidence reasonable here?   keys Q W E",
                                EditorStyles.miniBoldLabel);
                int confidence = DrawScoreRow(_edit.ConfidenceAppropriateness, 3, s => s == 1, s => s == 2);
                if (confidence != _edit.ConfidenceAppropriateness)
                {
                    _edit.ConfidenceAppropriateness = confidence;
                    Save(frame);
                }
                GUILayout.Label(Line(vocabulary.ConfidenceLabels, _edit.ConfidenceAppropriateness),
                                EditorStyles.wordWrappedMiniLabel);

                GUILayout.Label("0 clears · space / → next", EditorStyles.miniLabel);

                EditorGUILayout.LabelField("Comment");
                _edit.Comment = EditorGUILayout.TextArea(_edit.Comment ?? "", GUILayout.MinHeight(40f));
                if (GUILayout.Button("Save comment")) Save(frame);
            }
        }

        private void DrawAuditVerdict(ReviewFrame frame)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Blind target audit", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{_reviewer.GradedCount} / {_reviewer.FrameCount} complete",
                                    EditorStyles.miniLabel);
                }

                EditorGUILayout.HelpBox(
                    "Select every play you would accept as best. Ties are allowed. No model pick, "
                    + "score, outcome, or disagreement category is shown.", MessageType.Info);

                bool unreviewable = EditorGUILayout.ToggleLeft(
                    "Unreviewable from this state", _edit.Unreviewable);
                if (unreviewable != _edit.Unreviewable)
                {
                    _edit.Unreviewable = unreviewable;
                    if (unreviewable)
                    {
                        _edit.PreferredActionIndices = new int[0];
                        _edit.AuditConfidence = 0;
                    }
                    Save(frame);
                }

                using (new EditorGUI.DisabledScope(_edit.Unreviewable))
                {
                    GUILayout.Label("Confidence in your preferred set   keys 1-3", EditorStyles.miniBoldLabel);
                    int confidence = DrawScoreRow(_edit.AuditConfidence, 3, s => false, s => s == 2);
                    if (confidence != _edit.AuditConfidence)
                    {
                        _edit.AuditConfidence = confidence;
                        Save(frame);
                    }
                    GUILayout.Label(
                        _edit.AuditConfidence == 1 ? "low"
                        : _edit.AuditConfidence == 2 ? "medium"
                        : _edit.AuditConfidence == 3 ? "high"
                        : "unset",
                        EditorStyles.wordWrappedMiniLabel);
                }

                GUILayout.Label("U marks unreviewable · space / → next", EditorStyles.miniLabel);

                EditorGUILayout.LabelField("Comment (optional)");
                _edit.Comment = EditorGUILayout.TextArea(_edit.Comment ?? "", GUILayout.MinHeight(36f));
                if (GUILayout.Button("Save comment")) Save(frame);
            }
        }

        /// <summary>A row of score buttons; returns the score after any click. Red below the line,
        /// amber on the middle, green at the top — the same reading in both axes.</summary>
        private static int DrawScoreRow(int current, int max, Func<int, bool> isBad, Func<int, bool> isMiddle)
        {
            int result = current;
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int score = 1; score <= max; score++)
                {
                    Color was = GUI.backgroundColor;
                    if (current == score)
                        GUI.backgroundColor = isBad(score) ? new Color(1f, 0.55f, 0.5f)
                                            : isMiddle(score) ? new Color(1f, 0.9f, 0.5f)
                                                              : new Color(0.55f, 1f, 0.6f);
                    if (GUILayout.Button(score.ToString(), GUILayout.Height(30f))) result = score;
                    GUI.backgroundColor = was;
                }
            }
            return result;
        }

        private static string Line(IReadOnlyList<string> labels, int score) =>
            score >= 1 && score <= labels.Count ? labels[score - 1] : "ungraded";

        /// <summary>
        /// Two rates, never averaged together. The play rate tracks illegitimate plays; the
        /// confidence rate tracks probabilities that were not clearly reasonable for the visible
        /// situation. A single mixed number would hide either overconfidence or underconfidence.
        /// </summary>
        private string Tally()
        {
            if (_reviewer.Annotations == null) return "";
            int badPlay = 0, judgedPlay = 0, badConfidence = 0, judgedConfidence = 0;
            foreach (ReviewFrame f in _reviewer.Bundle.Frames)
            {
                Annotation a = _reviewer.Annotations.For(f);
                if (a == null) continue;
                if (a.Reasonableness >= 1)
                {
                    // 4 is "not resolvable from one turn" — counted as judged-but-not-a-failure
                    // would overstate the pass rate, so it is excluded from the denominator.
                    if (a.Reasonableness != 4) { judgedPlay++; if (a.Reasonableness <= 3) badPlay++; }
                }
                if (a.ConfidenceAppropriateness >= 1)
                {
                    judgedConfidence++;
                    if (a.ConfidenceAppropriateness <= 2) badConfidence++;
                }
            }
            string play = judgedPlay > 0
                ? $"play {badPlay}/{judgedPlay} bad"
                : "play -";
            string confidence = judgedConfidence > 0
                ? $"conf {badConfidence}/{judgedConfidence} not clearly reasonable"
                : "conf -";
            return $"{play}  ·  {confidence}  ·  of {_reviewer.FrameCount}";
        }


        /// <summary>
        /// Motif, and anything else worth saying beyond the verdict. Folded away by default: the
        /// verdict is the deliverable and this is optional grouping for later.
        ///
        /// The structural dropdown that used to sit here is GONE — see ActionGrouping. It asked the
        /// reviewer to notice something the engine can compute, on every single screen.
        /// </summary>
        private void DrawExtraLabels(ReviewFrame frame)
        {
            ReviewVocabulary vocabulary = _reviewer.Vocabulary;
            if (vocabulary == null) return;

            _showExtras = EditorGUILayout.Foldout(_showExtras, "Motif (optional)", true);
            if (!_showExtras) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("What the decision was BETWEEN — the situation, not the pick.",
                                EditorStyles.miniLabel);
                _motifText = EditorGUILayout.TextField("Motif", _motifText);
                DrawMotifSuggestions(vocabulary);
                if (GUILayout.Button("Save motif")) Save(frame);
            }
        }

        private bool _showExtras;

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

                if (!frame.IsTargetAudit)
                {
                    // Shown as CONTEXT and labelled as such. The review document is explicit that the
                    // episode result is not evidence the action was good — a won game does not redeem a
                    // bad play and a lost one does not condemn a forced move.
                    string outcome = frame.EpisodeReturnForActor > 0 ? "actor won"
                        : frame.EpisodeReturnForActor < 0 ? "actor lost" : "draw";
                    EditorGUILayout.LabelField("Eventually", $"{outcome}  (context, not proof)");
                }
            }
        }

        private void DrawActions(ReviewFrame frame)
        {
            if (frame.IsTargetAudit)
            {
                DrawAuditActions(frame);
                return;
            }

            EditorGUILayout.LabelField($"Legal actions ({frame.Actions.Count})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Click one to light up what it names. Probabilities are the policy's, over this set.",
                EditorStyles.miniLabel);

            // What the highlight COULD and could not do. Without this line a hero-targeting action
            // looks like a broken feature instead of a stated limitation.
            if (!string.IsNullOrEmpty(_reviewer.HighlightNote))
                EditorGUILayout.HelpBox(_reviewer.HighlightNote, MessageType.None);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(190f));
            foreach (ActionGroup group in _reviewer.Groups)
            {
                using (new EditorGUILayout.HorizontalScope(
                           group.ContainsChosen ? EditorStyles.helpBox : GUIStyle.none))
                {
                    GUILayout.Label(group.ContainsChosen ? "●" : "○", GUILayout.Width(16f));
                    GUILayout.Label(FormatProbability(group.Probability),
                                    group.ContainsChosen ? EditorStyles.boldLabel : EditorStyles.label,
                                    GUILayout.Width(84f));
                    if (GUILayout.Button(group.Label, EditorStyles.linkLabel))
                        _reviewer.SelectAction(group.Members[0]);
                    GUILayout.FlexibleSpace();
                }

                // The alias note is the whole point of grouping: it says the confidence you are
                // reading was assembled from several spellings of one play, not split between
                // competing ones.
                if (group.IsAlias)
                    GUILayout.Label($"        {group.AliasNote} — "
                                    + $"top spelling alone {FormatProbability(group.TopProbability)}",
                                    EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawAuditActions(ReviewFrame frame)
        {
            int visible = 0;
            foreach (ActionGroup group in _reviewer.Groups) if (!group.IsConcede) visible++;
            EditorGUILayout.LabelField($"Distinct plays ({visible})", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Check every preferred play. View only lights up named cards or targets.",
                EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(_reviewer.HighlightNote))
                EditorGUILayout.HelpBox(_reviewer.HighlightNote, MessageType.None);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(240f));
            foreach (ActionGroup group in _reviewer.Groups)
            {
                if (group.IsConcede) continue;
                int representative = group.Members[0];
                bool wasPreferred = IsPreferred(representative);
                using (new EditorGUILayout.HorizontalScope(wasPreferred ? EditorStyles.helpBox : GUIStyle.none))
                {
                    bool preferred = GUILayout.Toggle(wasPreferred, GUIContent.none, GUILayout.Width(22f));
                    if (preferred != wasPreferred)
                    {
                        SetPreferred(representative, preferred);
                        _edit.Unreviewable = false;
                        Save(frame);
                    }
                    GUILayout.Label(group.Label,
                                    wasPreferred ? EditorStyles.boldLabel : EditorStyles.label);
                    if (GUILayout.Button("View", EditorStyles.miniButton, GUILayout.Width(42f)))
                        _reviewer.SelectAction(representative);
                }
                if (group.IsAlias)
                    GUILayout.Label($"        {group.AliasNote}", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private bool IsPreferred(int actionIndex)
        {
            foreach (int selected in _edit.PreferredActionIndices ?? new int[0])
                if (selected == actionIndex) return true;
            return false;
        }

        private void SetPreferred(int actionIndex, bool preferred)
        {
            var values = new List<int>(_edit.PreferredActionIndices ?? new int[0]);
            values.Remove(actionIndex);
            if (preferred) values.Add(actionIndex);
            values.Sort();
            _edit.PreferredActionIndices = values.ToArray();
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
