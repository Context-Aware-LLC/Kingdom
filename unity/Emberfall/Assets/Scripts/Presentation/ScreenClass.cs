using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// #247 — puts exactly one screen-class marker on every <c>.emb-root</c>, so the theme's type
    /// scale and touch floor can step with the size of the screen instead of shipping one size for
    /// every device.
    ///
    /// <para>Sam, 2026-08-23: <i>"we need breakpoints on some of the elements, specifically the
    /// SIZE of the elements. So far, we only have breakpoints on layout, not on the size of
    /// buttons."</i> There were in fact no screen breakpoints at all — only per-component solves
    /// (<see cref="CardWall"/> promoting tiles by available width,
    /// <c>CharacterRosterController.LayoutHeroGrid</c> solving a column count) and one modifier
    /// driven by content rather than by the screen.</para>
    ///
    /// <para><b>The classes and their values live in <c>theme.uss</c>, not here.</b> This file only
    /// decides WHICH one applies. Read the "SCREEN CLASSES" block at the bottom of that sheet for
    /// the breakpoints, why they are Material 3's numbers, and why the scale is written out token
    /// by token instead of derived from one multiplier.</para>
    ///
    /// <para><b>SELF-INSTALLING, and that is deliberate</b> — the same reason
    /// <see cref="DevicePerfCapture"/> is: a mechanism that needs a scene edit cannot be landed by
    /// anyone who does not hold the Unity Editor seat, and the Editor is a single-writer resource
    /// on this project. Six UI scenes would each have needed a component otherwise. Nothing has to
    /// be wired for this to run, and nothing references it.</para>
    ///
    /// <para><b>Keyed on the panel's SHORT AXIS in dp.</b> The game is landscape-locked (NOR-283),
    /// so that is the height, and it is the axis that actually runs out: a Pixel 5a gives 411dp of
    /// height against 914dp of width. Note this is the PANEL's height in its own authored units,
    /// not <c>Screen.height</c> in pixels — the panel runs ConstantPhysicalSize at referenceDpi
    /// 160, so its own units already ARE Android dp and no conversion belongs here. Reading
    /// <c>Screen.height</c> instead would report 1080 on the phone and land it in
    /// <c>expanded</c>.</para>
    ///
    /// <para><b>Re-evaluated on every geometry change</b>, not once: the Android split-screen and
    /// foldable cases resize a live panel, and the Editor's Game View does it constantly while a
    /// human drags the window. Applying once at load would freeze the first answer.</para>
    /// </summary>
    public static class ScreenClass
    {
        /// <summary>Material 3's window size classes. See theme.uss for why these and not ours.</summary>
        internal const float MediumMinDp = 600f;
        internal const float ExpandedMinDp = 840f;

        internal const string Compact = "emb-sw-compact";
        internal const string Medium = "emb-sw-medium";
        internal const string Expanded = "emb-sw-expanded";

        private static readonly string[] All = { Compact, Medium, Expanded };

        // Roots already carrying a geometry callback, so a re-scan cannot double-register one.
        // A HashSet of the ELEMENT, not of the document: a UIDocument rebuilds its tree on
        // enable and the old root is then a different object with a dead callback on it.
        private static readonly HashSet<VisualElement> Hooked = new HashSet<VisualElement>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            SceneManager.sceneLoaded += (_, __) => Scan();
            Scan();
        }

        /// <summary>
        /// Find every <c>UIDocument</c> in the loaded scenes and make sure its root is classified
        /// and will re-classify itself. Cheap enough to run on every scene load; it does no work
        /// for a root it has already hooked.
        /// </summary>
        internal static void Scan()
        {
            // Include inactive: a screen's document is commonly disabled until its controller
            // enables it, and it would otherwise be classified only if it happened to be open
            // at scene load.
            var docs = Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include,
                                                            FindObjectsSortMode.None);
            foreach (UIDocument doc in docs)
            {
                VisualElement root = doc == null ? null : doc.rootVisualElement;
                if (root == null) continue;

                Apply(root);

                if (Hooked.Contains(root)) continue;
                Hooked.Add(root);
                root.RegisterCallback<GeometryChangedEvent>(_ => Apply(root));
                root.RegisterCallback<DetachFromPanelEvent>(_ => Hooked.Remove(root));
            }
        }

        /// <summary>
        /// Classify one panel root. Walks to the <c>.emb-root</c> beneath it because that is where
        /// the screens' own styling hangs — putting the class on the panel root instead would work
        /// for the custom properties (they inherit) but would not match any selector someone later
        /// writes as <c>.emb-sw-compact .something</c> expecting the screen element.
        /// </summary>
        internal static void Apply(VisualElement root)
        {
            float shortAxisDp = Mathf.Min(root.resolvedStyle.width, root.resolvedStyle.height);

            // Layout has not run yet. Doing nothing is right: the compact base in :root is already
            // a complete scale, so an unclassified panel renders at phone size rather than at no
            // size, and the GeometryChangedEvent that follows will classify it.
            if (shortAxisDp <= 0f) return;

            string want = ClassFor(shortAxisDp);

            VisualElement target = root.Q<VisualElement>(className: "emb-root") ?? root;
            foreach (string c in All) target.EnableInClassList(c, c == want);
        }

        /// <summary>The class for a short axis measured in dp. Pure, so it can be asserted on.</summary>
        internal static string ClassFor(float shortAxisDp)
        {
            if (shortAxisDp >= ExpandedMinDp) return Expanded;
            if (shortAxisDp >= MediumMinDp) return Medium;
            return Compact;
        }
    }
}
