using System.Globalization;
using System.Reflection;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Editor
{
    /// <summary>
    /// Renders CollectionScreen.uxml into an OFFSCREEN panel at the Pixel 5a's dp budget and reports
    /// the resolved geometry of the pieces the 2026-08-31 rail/drawer swap moved.
    ///
    /// <para><b>Why this is not the Device DP Rig.</b> <see cref="DeviceDpRig"/> retunes the live
    /// Game View and needs PLAY MODE, because it swaps each UIDocument onto an in-memory clone; and
    /// its own docs say not to judge shape from a Game View capture, which letterboxes to 1280x720
    /// and squashes a 2400x1080 view. This builds its own panel against its own RenderTexture, so it
    /// runs in batchmode and touches no tracked asset.</para>
    ///
    /// <para><b>IT CANNOT GIVE YOU A PICTURE, and that was tried.</b> Layout is not paint: driving
    /// the panel's own Repaint by hand settles nothing, because UI Toolkit's RUNTIME rendering goes
    /// through URP's render loop and batchmode never runs one. The target texture came back holding
    /// exactly the clear colour. Judging appearance needs a real Editor or a device; this answers
    /// GEOMETRY only.</para>
    ///
    /// <para><b>What it can and cannot answer.</b> It resolves LAYOUT — is the rail a row across the
    /// top, do nine tabs clear the touch floor, does the wall still get a viewport, does the drawer
    /// come from the left. It does NOT bind CollectionController, so the wall is EMPTY: no card
    /// tiles are baked and the chip rows the controller populates are empty.</para>
    ///
    /// <para>Delete this file once the swap is verified on a device — it is scaffolding for one
    /// question, not a standing guard. The standing guard is
    /// CollectionDeckDrawerContractTests.</para>
    /// </summary>
    public static class CollectionSwapVerify
    {
        private const string Uxml = "Assets/UI/Collection/CollectionScreen.uxml";

        // Pixel 5a, and the arithmetic DeviceDpRig documents: a 2400x1080 screen at 420dpi is a
        // 914.3 x 411.4 dp panel, which is a 2.625x scale from authored px.
        private const int ScreenW = 2400;
        private const int ScreenH = 1080;
        private const float DeviceDpi = 420f;

        [MenuItem("Emberfall/Debug/Verify Collection Swap (offscreen, device dp)")]
        public static void Run()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Uxml);
            if (tree == null)
            {
                Debug.LogError($"[CollectionSwapVerify] no VisualTreeAsset at {Uxml}");
                return;
            }

            var rt = new RenderTexture(ScreenW, ScreenH, 24) { name = "CollectionSwapVerifyRT" };
            var settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            settings.referenceDpi = 160f * 96f / DeviceDpi;   // the lever; fallbackDpi never applies
            settings.fallbackDpi = 96f;
            settings.targetTexture = rt;
            settings.clearColor = true;
            settings.colorClearValue = new Color(0.02f, 0.03f, 0.05f, 1f);

            var host = new GameObject("CollectionSwapVerifyHost");
            var doc = host.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = tree;

            try
            {
                VisualElement root = doc.rootVisualElement;

                Tick(root);
                Report(root, "CLOSED");

                VisualElement drawer = root.Q<VisualElement>("query-bar");
                drawer?.AddToClassList("col-query--open");
                Tick(root);
                Report(root, "FILTERS OPEN");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(settings);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        /// <summary>
        /// Resolve the layout. <c>EditorApplication.QueuePlayerLoopUpdate()</c> DOES NOT DO THIS in
        /// batchmode edit mode: UI Toolkit's RUNTIME panels tick off the player loop, which is not
        /// running, so every worldBound comes back NaN — measured 2026-08-31, and NaN is what an
        /// unresolved layout reports rather than 0, so a `<= 0` guard would have called it fine.
        /// Both entry points below are internal, hence the reflection; ValidateLayout on the panel
        /// itself is the one that actually settles Yoga.
        /// </summary>
        private static void Tick(VisualElement root)
        {
            Assembly ui = typeof(VisualElement).Assembly;
            var runtime = ui.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");
            MethodInfo update = runtime?.GetMethod("UpdateRuntimePanels",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            object panel = root.panel;
            MethodInfo validate = panel?.GetType().GetMethod("ValidateLayout",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (update == null && validate == null)
                Debug.LogError("[CollectionSwapVerify] neither UpdateRuntimePanels nor "
                               + "ValidateLayout resolved — the layout below will be NaN and the "
                               + "reflection needs re-pointing at this Unity version.");

            for (int i = 0; i < 4; i++)
            {
                update?.Invoke(null, null);
                validate?.Invoke(panel, null);
            }
        }

        private static void Report(VisualElement root, string label)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[CollectionSwapVerify] ===== {label} =====");
            sb.AppendLine($"  panel root            {Fmt(root)}");

            VisualElement rail = root.Q<VisualElement>("class-tab-bar");
            VisualElement body = root.Q<VisualElement>("body");
            VisualElement panel = root.Q<VisualElement>("collection-panel");
            VisualElement scroll = root.Q<ScrollView>("collection-scroll")?.contentViewport;
            VisualElement drawer = root.Q<VisualElement>("query-bar");
            VisualElement deck = root.Q<VisualElement>("deck-drawer");

            sb.AppendLine($"  class-tab-bar         {Fmt(rail)}");
            sb.AppendLine($"  body                  {Fmt(body)}");
            sb.AppendLine($"  collection-panel      {Fmt(panel)}");
            sb.AppendLine($"  wall viewport         {Fmt(scroll)}");
            sb.AppendLine($"  query-bar (filters)   {Fmt(drawer)}");
            sb.AppendLine($"  deck-drawer           {Fmt(deck)}");

            if (rail != null)
            {
                // THE FLOOR IS ON THE TAB'S SHORT AXIS, whichever axis that is. Checking width
                // against 48 and taking the BAR's height for the other one passed the old vertical
                // rail — measured 2026-08-31, it reported OK for tabs that were 129.9 wide and
                // 45.7 TALL, which is the exact finding (audit #9) this swap exists to fix.
                var tabs = rail.Children().ToList();
                float minW = tabs.Count > 0 ? tabs.Min(t => t.worldBound.width) : 0f;
                float maxW = tabs.Count > 0 ? tabs.Max(t => t.worldBound.width) : 0f;
                float minH = tabs.Count > 0 ? tabs.Min(t => t.worldBound.height) : 0f;
                float shortest = tabs.Count > 0
                    ? tabs.Min(t => Mathf.Min(t.worldBound.width, t.worldBound.height)) : 0f;
                sb.AppendLine($"  tabs                  n={tabs.Count} "
                              + $"width {minW.ToString("F1", CultureInfo.InvariantCulture)}"
                              + $"..{maxW.ToString("F1", CultureInfo.InvariantCulture)} "
                              + $"height {minH.ToString("F1", CultureInfo.InvariantCulture)} "
                              + $"-> shortest axis {shortest.ToString("F1", CultureInfo.InvariantCulture)}dp, "
                              + $"floor 48dp: {(shortest >= 48f ? "OK" : "UNDER")}");
                sb.AppendLine($"  rail is a ROW         {(tabs.Count > 1 && tabs[1].worldBound.x > tabs[0].worldBound.x ? "yes" : "NO - still stacked")}");
            }

            if (scroll != null)
            {
                // CardWall.ResolveTileTier's compact branch, run on the viewport this layout leaves.
                float solved = scroll.worldBound.height / 2f - 8f;
                float tileH = Mathf.Max(Mathf.Floor(solved / 16f) * 16f, 96f);
                float tileW = Mathf.Round(tileH * (340f / 477f));
                sb.AppendLine($"  => compact tile       {tileW.ToString("F0", CultureInfo.InvariantCulture)}"
                              + $" x {tileH.ToString("F0", CultureInfo.InvariantCulture)}");
            }

            Debug.Log(sb.ToString());
        }

        private static string Fmt(VisualElement e)
        {
            if (e == null) return "MISSING";
            Rect r = e.worldBound;
            if (float.IsNaN(r.width) || float.IsNaN(r.height)) return "UNRESOLVED (NaN)";
            return string.Format(CultureInfo.InvariantCulture,
                "x={0,7:F1} y={1,7:F1}  w={2,7:F1} h={3,7:F1}", r.x, r.y, r.width, r.height);
        }

    }
}
