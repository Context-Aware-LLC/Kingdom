using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// #240 — Android's back gesture and hardware back button navigate the app.
    ///
    /// <para>Sam, Pixel 5a playtest 2026-08-21: <i>"Swiping backwards will not navigate you through
    /// the app. Cool, if it did."</i> It is a quality-of-life ask with a sharper edge than it looks:
    /// paired with a screen that has no back control of its own, the absence of a back gesture makes
    /// killing the app the only exit. That is the trap #181 and #232 are both about.</para>
    ///
    /// <para><b>IT CLICKS THE SCREEN'S OWN BACK CONTROL RATHER THAN NAVIGATING ITSELF.</b> Every
    /// screen already has a control that knows where back goes, and some of that knowledge is not
    /// reproducible from here — <c>CollectionController.ReturnScene</c> is a runtime value set by
    /// whoever opened the deck builder, so a table of scene-to-parent mappings in this file would be
    /// wrong for the roster entry path. Clicking the real control means the gesture and the button
    /// can never disagree, and a screen that changes where its back goes takes this with it for
    /// free.</para>
    ///
    /// <para><b>Opt in with the <c>emb-back</c> class</b> in UXML. It is declarative and greppable,
    /// and it is deliberately not a name convention: the controls are called <c>back-button</c>,
    /// <c>btn-back</c>, <c>create-cancel</c>, <c>craft-close</c>, <c>deck-modal-cancel</c> and
    /// <c>btn-shop-close</c>, and matching that set by substring would also catch
    /// <c>deck-change-back</c> (which opens a card-back picker) and <c>deck-back-grid</c>. Guessing
    /// from names would make the back gesture open a picker.</para>
    ///
    /// <para><b>The TOPMOST visible one wins</b>, which is how a modal gets dismissed before the
    /// screen behind it. "Topmost" is reverse document order, because a modal is declared after the
    /// content it covers in every one of these screens; visibility is checked up the whole ancestor
    /// chain, since a hidden modal's buttons are still in the tree with a live class list.</para>
    ///
    /// <para><b>SELF-INSTALLING</b>, the same as <see cref="DevicePerfCapture"/> and for the same
    /// reason: a mechanism that needs a scene edit cannot be landed by anyone who does not hold the
    /// Unity Editor seat, and this would otherwise need a component in six scenes.</para>
    ///
    /// <para><b>The new Input System, not <c>Input.GetKeyDown</c>.</b> The project is
    /// <c>activeInputHandler: 1</c> (new only), where the legacy API throws rather than returning
    /// false. Android maps both the hardware back button and the predictive back gesture to
    /// <c>Key.Escape</c>.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BackNavigation : MonoBehaviour
    {
        /// <summary>Put this on the control that means "back" for its screen or modal.</summary>
        public const string BackClass = "emb-back";

        /// <summary>Last scene we complained about, so the warning below is once per screen rather
        /// than once per press.</summary>
        private static string _warnedScene;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("~BackNavigation");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<BackNavigation>();
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            TryGoBack();
        }

        /// <summary>
        /// Click the topmost visible back control, and report whether one was found. Internal rather
        /// than private so a check can drive it without synthesising a key press.
        /// </summary>
        internal static bool TryGoBack()
        {
            VisualElement target = FindTopmostBackControl();
            if (target == null)
            {
                // ONCE PER SCENE, not once per press. A player mashing back on a screen that has
                // no exit is exactly the situation worth a log line, and exactly the situation that
                // would otherwise flood the log with the same line — during a match, where the
                // board legitimately has nothing to go back to and the settings gear is the only
                // marked control.
                string scene = SceneManager.GetActiveScene().name;
                if (scene != "MainMenu" && scene != _warnedScene)
                {
                    _warnedScene = scene;
                    Debug.LogWarning("[BackNavigation] back pressed on scene '" + scene
                        + "' but no visible ." + BackClass + " control was found — the gesture did "
                        + "nothing. Mark that screen's back control with the class. "
                        + "(Logged once per scene.)");
                }
                return false;
            }

            using (var evt = ClickEvent.GetPooled())
            {
                evt.target = target;
                target.SendEvent(evt);
            }
            return true;
        }

        private static VisualElement FindTopmostBackControl()
        {
            var docs = Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude,
                                                            FindObjectsSortMode.None);
            VisualElement best = null;
            int bestDepth = -1;

            foreach (UIDocument doc in docs)
            {
                VisualElement root = doc == null ? null : doc.rootVisualElement;
                if (root == null) continue;

                var found = new List<VisualElement>();
                root.Query<VisualElement>(className: BackClass).ToList(found);
                foreach (VisualElement e in found)
                {
                    if (!IsVisible(e)) continue;

                    // Depth is the tie-break WITHIN a document: a modal's Cancel is deeper than the
                    // screen's own Back, and it is also later in document order. Depth is the more
                    // robust of the two to read, and the two agree on every screen here.
                    int depth = Depth(e);
                    if (depth <= bestDepth) continue;
                    bestDepth = depth;
                    best = e;
                }
            }
            return best;
        }

        /// <summary>Visible means IT and every ancestor are displayed and have a real box. A hidden
        /// modal's buttons are still in the tree with their classes intact, so testing the element
        /// alone would dismiss a modal that is not open.</summary>
        private static bool IsVisible(VisualElement e)
        {
            for (VisualElement v = e; v != null; v = v.parent)
            {
                if (v.resolvedStyle.display == DisplayStyle.None) return false;
                if (v.resolvedStyle.visibility == Visibility.Hidden) return false;
            }
            return e.worldBound.width > 0f && e.worldBound.height > 0f;
        }

        private static int Depth(VisualElement e)
        {
            int d = 0;
            for (VisualElement v = e.parent; v != null; v = v.parent) d++;
            return d;
        }
    }
}
