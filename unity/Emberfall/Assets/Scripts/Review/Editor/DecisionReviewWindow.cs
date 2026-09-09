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

            DrawNavigation(frame);
            EditorGUILayout.Space(4f);
            DrawIdentity(frame);
            EditorGUILayout.Space(6f);
            DrawActions(frame);
        }

        private void DrawNavigation(ReviewFrame frame)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_reviewer.CurrentFrame <= 0))
                    if (GUILayout.Button("◀ Prev", GUILayout.Width(70f))) _reviewer.Previous();

                GUILayout.Label(
                    $"{_reviewer.CurrentFrame + 1} / {_reviewer.FrameCount}",
                    EditorStyles.boldLabel, GUILayout.Width(60f));

                using (new EditorGUI.DisabledScope(_reviewer.CurrentFrame >= _reviewer.FrameCount - 1))
                    if (GUILayout.Button("Next ▶", GUILayout.Width(70f))) _reviewer.Next();

                GUILayout.FlexibleSpace();
                GUILayout.Label(frame.Caption, EditorStyles.miniLabel);
            }

            // Arrow keys, while this panel has focus. The game view has no input wiring at all —
            // nothing here is playable — so there is nothing to conflict with.
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
