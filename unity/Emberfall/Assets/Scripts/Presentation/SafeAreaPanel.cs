using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// #237. The UI Toolkit counterpart of <see cref="SafeAreaCamera"/>.
    ///
    /// <para>The board has respected <c>Screen.safeArea</c> since EF-062 and says so in its log.
    /// The UI Toolkit screens never did, so on a Pixel 5a — whose cutout becomes a 136 px LEFT
    /// inset in landscape — the identity plate, the shard counter and the footer all sat partly
    /// off-screen. This is not a missing safe-area concept project-wide; it is that the camera has
    /// one and the panels do not.</para>
    ///
    /// <para>WHAT IT PADS, AND WHY THAT ELEMENT. Every screen's UXML has a top element classed
    /// <c>emb-root</c> — the shared root convention, which is also where theme.uss hangs its
    /// custom properties. Padding it insets the content while leaving the element's own
    /// background painting the full panel, because a background covers the padding box. So a
    /// screen whose backdrop is on the root keeps its full bleed for free and only its contents
    /// move.</para>
    ///
    /// <para>A layer that must bleed past the inset ANYWAY — a scrim drawn over the whole panel,
    /// not just the safe part — takes the <c>emb-bleed</c> class and gets equal negative margins,
    /// putting it back on the panel edges. That is the opt-out, and it is deliberately explicit:
    /// the default has to be "respect the cutout", because the failure mode of the other default
    /// is content the player cannot see.</para>
    ///
    /// <para>ATTACHED AUTOMATICALLY, not authored into six scenes. <see cref="SafeAreaCamera"/>
    /// set the precedent for the same reason — Board.unity and BoardNetworked.unity could not be
    /// allowed to drift from each other, and here there are six screens plus every one added
    /// later. A screen opts in by using <c>emb-root</c>, which it already does; a UIDocument
    /// without one is left alone.</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SafeAreaPanel : MonoBehaviour
    {
        /// <summary>The shared root class every screen's UXML puts on its top element. Also the
        /// opt-in: a UIDocument with no such element is not a full-screen screen and is skipped.</summary>
        public const string RootClass = "emb-root";

        /// <summary>Opt-out for a layer that must span the whole panel regardless — a full-bleed
        /// scrim or backdrop. Gets the inset back as negative margin.</summary>
        public const string BleedClass = "emb-bleed";

        private UIDocument _document;
        private VisualElement _root;
        private Rect _lastSafeArea;
        private int _lastWidth;
        private int _lastHeight;
        private float _lastPanelHeight;
        private bool _applied;
        private bool _logged;

        // ── Automatic attachment ────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => AttachAll();

        /// <summary>Give every <see cref="UIDocument"/> in the loaded scenes a panel guard. Safe to
        /// call repeatedly — <c>[DisallowMultipleComponent]</c> plus the explicit check make it
        /// idempotent, and a document that already has one is untouched.</summary>
        public static void AttachAll()
        {
            UIDocument[] documents = Object.FindObjectsByType<UIDocument>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < documents.Length; i++)
            {
                if (documents[i] == null) continue;
                if (documents[i].GetComponent<SafeAreaPanel>() == null)
                    documents[i].gameObject.AddComponent<SafeAreaPanel>();
            }
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            _applied = false;
            _root = null;
        }

        /// <summary>
        /// Polled, for the same reason <see cref="SafeAreaCamera"/> polls: orientation changes,
        /// multi-window resizes and the navigation bar appearing all arrive as a new
        /// <c>Screen.safeArea</c> with no callback attached to it.
        ///
        /// <para>It also covers a startup ordering problem this component has and the camera one
        /// does not. <c>UIDocument</c> builds its visual tree in its own OnEnable and the panel's
        /// layout is not resolved until the first layout pass, so the panel height read on frame 0
        /// is 0 or stale. Resolving lazily here — and re-applying when the panel height changes —
        /// is what makes the component independent of script execution order.</para>
        /// </summary>
        private void Update()
        {
            if (_document == null) return;

            if (_root == null)
            {
                _root = ResolveRoot();
                if (_root == null) return;
            }

            float panelH = PanelHeight();
            if (panelH <= 0f) return;

            if (_applied
                && Screen.width == _lastWidth
                && Screen.height == _lastHeight
                && Screen.safeArea == _lastSafeArea
                && Mathf.Approximately(panelH, _lastPanelHeight))
                return;

            Apply(panelH);
        }

        private VisualElement ResolveRoot()
        {
            VisualElement wrapper = _document.rootVisualElement;
            if (wrapper == null) return null;

            // rootVisualElement is Unity's synthetic per-panel wrapper, NOT the UXML's own top
            // element — the same distinction MainMenuController documents, where padding the
            // wrapper instead of the real root made a class operation a silent no-op.
            VisualElement found = wrapper.Q<VisualElement>(className: RootClass);
            if (found == null && !_logged)
            {
                _logged = true;
                // Not an error. A UIDocument that is not a full-screen screen legitimately has no
                // emb-root, and this component is attached to every one of them indiscriminately.
                Debug.Log($"[SafeAreaPanel] {name}: no .{RootClass} element — not a full-screen "
                          + "panel, leaving it alone.");
            }
            return found;
        }

        /// <summary>The panel's height in ITS OWN units. Taken from the wrapper rather than from
        /// PanelSettings' reference resolution, so the scale mode and match axis authored in the
        /// asset can change without this needing to know (the NOR-358 trigger on DeviceLayout).</summary>
        private float PanelHeight()
        {
            VisualElement wrapper = _document.rootVisualElement;
            if (wrapper == null) return 0f;
            float h = wrapper.resolvedStyle.height;
            return float.IsNaN(h) ? 0f : h;
        }

        private void Apply(float panelH)
        {
            _lastWidth = Screen.width;
            _lastHeight = Screen.height;
            _lastSafeArea = Screen.safeArea;
            _lastPanelHeight = panelH;
            _applied = true;

            DeviceLayout.PanelInsets insets = DeviceLayout.SolvePanelInsets(
                _lastSafeArea, _lastWidth, _lastHeight, panelH);

            ApplyInsets(_root, insets);

            // One line, once, carrying the numbers a device run has to report back — the same
            // contract SafeAreaCamera's log line has, and the reason #237 could be diagnosed from
            // a log at all. Logged even on identity, because "this ran and found nothing to do"
            // and "this never ran" are the two answers a device report has to tell apart.
            if (_logged) return;
            _logged = true;
            Debug.Log($"[SafeAreaPanel] {name}: screen {_lastWidth}x{_lastHeight} · "
                      + $"safeArea x{_lastSafeArea.x:F0} y{_lastSafeArea.y:F0} "
                      + $"w{_lastSafeArea.width:F0} h{_lastSafeArea.height:F0} · "
                      + $"panel {panelH:F1} units (px/{_lastHeight / panelH:F2}) · "
                      + $"padding {insets}{(insets.IsIdentity ? " (identity — nothing to inset)" : "")}");
        }

        /// <summary>
        /// Put <paramref name="insets"/> onto a screen root. Static and free of
        /// <c>Screen</c>/<c>UIDocument</c> so the layout consequence can be asserted directly
        /// against real Yoga — see <c>SafeAreaPanelVerify</c>, which drives this with synthetic
        /// insets and measures what actually moved. The arithmetic is
        /// <see cref="DeviceLayout.SolvePanelInsets"/>'s; this is only where it lands.
        /// </summary>
        public static void ApplyInsets(VisualElement root, DeviceLayout.PanelInsets insets)
        {
            if (root == null) return;

            root.style.paddingLeft = insets.Left;
            root.style.paddingTop = insets.Top;
            root.style.paddingRight = insets.Right;
            root.style.paddingBottom = insets.Bottom;

            // Full-bleed layers get the inset back as negative margin, AND the same amount again
            // as padding. Both halves matter and each alone is wrong:
            //
            //   margin only  — a scrim covers the cutout strip (right), but a modal container's
            //                  centred panel is then free to sit under it (wrong).
            //   padding only — which is what NOT bleeding does: the modal's content is safe
            //                  (right), but its dimming background stops short of the panel edge
            //                  and leaves an undimmed strip down the cutout (wrong).
            //
            // Together they mean what the class name says: paint to the panel edge, keep your
            // CONTENT inside the safe area. That is what all thirteen of these layers want, which
            // is why one class covers both the bare scrims and the modal containers.
            //
            // Queried on every apply rather than cached: a screen can build a scrim after the
            // first frame, and the query is over a single panel.
            foreach (VisualElement bleed in root.Query<VisualElement>(className: BleedClass).Build())
            {
                bleed.style.marginLeft = -insets.Left;
                bleed.style.marginTop = -insets.Top;
                bleed.style.marginRight = -insets.Right;
                bleed.style.marginBottom = -insets.Bottom;

                bleed.style.paddingLeft = insets.Left;
                bleed.style.paddingTop = insets.Top;
                bleed.style.paddingRight = insets.Right;
                bleed.style.paddingBottom = insets.Bottom;
            }
        }
    }
}
