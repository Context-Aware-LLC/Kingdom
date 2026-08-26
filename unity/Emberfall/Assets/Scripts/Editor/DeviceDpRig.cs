using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Editor
{
    /// <summary>
    /// Makes the Editor's Game View reproduce the PHONE's dp budget, so a layout question that is
    /// about running out of vertical dp can be answered here instead of by building an APK.
    ///
    /// <para><b>Why this exists at all.</b> <c>PanelSettings</c> runs ConstantPhysicalSize against
    /// <c>Screen.dpi</c>, which is ~420 on the phone and <b>96</b> in the Editor. From the same
    /// 1080-tall screen that is a <b>411dp</b> panel on the device and an <b>1800dp</b> panel here
    /// — the Editor hands a layout <b>4.4x more vertical budget than the phone has</b>, and
    /// nothing warns you. Every bug that is about running out of dp is invisible at default
    /// settings. Three wrong guesses on <c>feat/character-create-scene</c> are attributable to it.</para>
    ///
    /// <para><b><c>fallbackDpi</c> is NOT the lever.</b> <c>Screen.dpi</c> is non-zero (96) in the
    /// Editor, so the fallback never applies; setting it only dirties a tracked asset.
    /// <c>referenceDpi</c> is the lever:</para>
    /// <code>
    /// referenceDpi = 160 * 96 / 420 = 36.571
    ///   -&gt; Screen 2400x1080, panel 914.3 x 411.4, scale 2.625x   (device measures 2.61x, 411dp)
    /// </code>
    ///
    /// <para><b>The clone is load-bearing.</b> A play-mode write to a tracked ScriptableObject
    /// serialises back out to disk — doing this the direct way once dirtied
    /// <c>m_FallbackDpi 160 -&gt; 420</c> in the committed PanelSettings. This never touches the
    /// asset; it swaps each UIDocument onto an in-memory copy that dies with play mode.</para>
    ///
    /// <para><b>Do not judge shape from a Game View capture.</b> The capture letterboxes to
    /// 1280x720, so a 2400x1080 view comes back horizontally squashed and circles read as ovals.
    /// Measure <c>worldBound</c>.</para>
    ///
    /// <para>RETIREMENT TRIGGER: if the project stops running ConstantPhysicalSize, or
    /// <c>ScreenClass</c> stops keying on the panel's short axis in dp, the arithmetic above is
    /// wrong and this file is misleading rather than merely stale. Delete it with that change.</para>
    /// </summary>
    public static class DeviceDpRig
    {
        /// <summary>Pixel 5a, the reference device.</summary>
        public const int DeviceWidth = 2400;
        public const int DeviceHeight = 1080;
        public const float DeviceDpi = 420f;

        /// <summary>The Editor's own reported dpi. Read, not assumed — a HiDPI monitor changes it.</summary>
        private static float EditorDpi => Screen.dpi > 0f ? Screen.dpi : 96f;

        private const string SizeLabel = "Emberfall Device (2400x1080)";

        [MenuItem("Emberfall/Debug/Device DP Rig/Apply (Pixel 5a, 411dp)")]
        public static void Apply()
        {
            string sized = SetGameViewSize();
            int swapped = SwapPanelsOntoClones();
            Debug.Log($"[DeviceDpRig] {sized}; {swapped} panel(s) cloned to referenceDpi {ReferenceDpi():F3}.");
        }

        /// <summary>referenceDpi that makes THIS Editor produce the device's panel scale.</summary>
        public static float ReferenceDpi() => 160f * EditorDpi / DeviceDpi;

        // ---- Game View sizing ----------------------------------------------------------------
        //
        // Setting `selectedSizeIndex` alone DOES NOT TAKE. The size dropdown itself calls
        // SizeSelectionCallback(index, null), and that is the only path that actually resizes the
        // view. Both are internal, hence the reflection.

        public static string SetGameViewSize()
        {
            Assembly ed = typeof(UnityEditor.EditorWindow).Assembly;
            Type sizesType = ed.GetType("UnityEditor.GameViewSizes");
            Type singleton = ed.GetType("UnityEditor.ScriptableSingleton`1").MakeGenericType(sizesType);
            object sizes = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);

            // THE GROUP MUST BE THE CURRENT ONE, NOT Standalone. Game View sizes are per build
            // target, and the indices are per group -- asking for Standalone while the build target
            // is Android returns an index that means something else entirely in the group the Game
            // View is actually showing. Measured: it silently produced a PORTRAIT 1080x2160 panel
            // on a landscape-locked game, which reads as a layout bug rather than a rig bug.
            object group = sizesType.GetProperty("currentGroup").GetValue(sizes);

            int index = FindSizeIndex(group);
            if (index < 0)
            {
                Type sizeType = ed.GetType("UnityEditor.GameViewSize");
                Type kindType = ed.GetType("UnityEditor.GameViewSizeType");
                object size = Activator.CreateInstance(sizeType,
                    Enum.Parse(kindType, "FixedResolution"), DeviceWidth, DeviceHeight, SizeLabel);
                group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
                index = FindSizeIndex(group);
            }

            Type gameViewType = ed.GetType("UnityEditor.GameView");
            MethodInfo select = gameViewType.GetMethod("SizeSelectionCallback",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            // Resources.FindObjectsOfTypeAll returns EVERY Game View window, and this machine has
            // had two. Drive all of them or you resize the one you are not looking at.
            var views = Resources.FindObjectsOfTypeAll(gameViewType);
            foreach (var v in views) select.Invoke(v, new object[] { index, null });
            return $"Game View size index {index} applied to {views.Length} window(s)";
        }

        private static int FindSizeIndex(object group)
        {
            MethodInfo getTotal = group.GetType().GetMethod("GetTotalCount");
            MethodInfo getSize = group.GetType().GetMethod("GetGameViewSize");
            int total = (int)getTotal.Invoke(group, null);
            for (int i = 0; i < total; i++)
            {
                object s = getSize.Invoke(group, new object[] { i });
                int w = (int)s.GetType().GetProperty("width").GetValue(s);
                int h = (int)s.GetType().GetProperty("height").GetValue(s);
                if (w == DeviceWidth && h == DeviceHeight) return i;
            }
            return -1;
        }

        // ---- Panel scale -----------------------------------------------------------------------

        /// <summary>
        /// Swaps every live UIDocument onto an in-memory clone of its PanelSettings carrying the
        /// device referenceDpi. Play mode only — outside it there is nothing to swap and a write
        /// would reach the asset.
        /// </summary>
        public static int SwapPanelsOntoClones()
        {
            if (!Application.isPlaying) return 0;
            var seen = new Dictionary<PanelSettings, PanelSettings>();
            int n = 0;
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                PanelSettings src = doc.panelSettings;
                if (src == null) continue;
                if (src.name.EndsWith("(DeviceDpRig)")) { n++; continue; }
                if (!seen.TryGetValue(src, out PanelSettings clone))
                {
                    clone = UnityEngine.Object.Instantiate(src);
                    clone.name = src.name + " (DeviceDpRig)";
                    clone.referenceDpi = ReferenceDpi();
                    seen[src] = clone;
                }
                doc.panelSettings = clone;
                n++;
            }
            return n;
        }

        // ---- Measurement -----------------------------------------------------------------------

        /// <summary>
        /// Dumps the live panel's dp budget and the worldBound of every element matching one of the
        /// given class or name selectors. Returns text, so it reads the same through
        /// <c>unity command eval</c> as it does in the console.
        /// </summary>
        public static string Measure(params string[] selectors)
        {
            var sb = new StringBuilder();
            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            if (docs.Length == 0) return "no UIDocument in the scene (are you in play mode?)";

            foreach (var doc in docs)
            {
                VisualElement root = doc.rootVisualElement;
                if (root == null) continue;
                sb.AppendLine($"--- {doc.gameObject.name}   panelSettings={doc.panelSettings?.name}");
                sb.AppendLine($"    Screen {Screen.width}x{Screen.height}  Screen.dpi={Screen.dpi}  referenceDpi={doc.panelSettings?.referenceDpi:F3}");
                float ph = root.worldBound.height;
                sb.AppendLine($"    panel  {root.worldBound.width:F1} x {ph:F1} dp   scale={(Screen.height / Mathf.Max(1f, ph)):F3}x");

                VisualElement embRoot = root.Q(className: "emb-root");
                if (embRoot != null)
                    sb.AppendLine($"    .emb-root classes: {string.Join(" ", embRoot.GetClasses())}");

                foreach (string sel in selectors)
                {
                    List<VisualElement> hits = sel.StartsWith("#")
                        ? root.Query(name: sel.Substring(1)).ToList()
                        : root.Query(className: sel.TrimStart('.')).ToList();
                    if (hits.Count == 0) { sb.AppendLine($"    {sel,-32} (0 matches)"); continue; }
                    for (int i = 0; i < hits.Count && i < 4; i++)
                    {
                        Rect b = hits[i].worldBound;
                        sb.AppendLine($"    {sel,-32} [{i}] {b.width,7:F1} x {b.height,7:F1}  at ({b.x,7:F1}, {b.y,7:F1})  {hits[i].resolvedStyle.display}");
                    }
                    if (hits.Count > 4) sb.AppendLine($"    {sel,-32} ... {hits.Count} total");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// How much of the card wall's viewport is actually card. This is the #185 question: a wall
        /// can be "5 columns" and still spend half its width on outer breathing room.
        /// </summary>
        public static string MeasureWallPacking()
        {
            var sb = new StringBuilder();
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                VisualElement root = doc.rootVisualElement;
                if (root == null) continue;
                VisualElement grid = root.Q(className: "db-grid");
                if (grid == null) continue;
                VisualElement scroll = root.Q("collection-scroll") ?? grid.parent;

                List<VisualElement> tiles = grid.Query(className: "card-tile").ToList()
                    .Where(t => t.resolvedStyle.display != DisplayStyle.None).ToList();
                if (tiles.Count == 0) { sb.AppendLine("db-grid found but 0 visible tiles"); continue; }

                Rect vp = (scroll ?? grid).worldBound;
                Rect t0 = tiles[0].worldBound;

                var rows = tiles.GroupBy(t => Mathf.RoundToInt(t.worldBound.y))
                                .OrderBy(g => g.Key).ToList();
                int cols = rows.Max(r => r.Count());
                var inView = rows.Where(r => r.Key + t0.height <= vp.yMax + 0.5f).ToList();
                int tilesInView = inView.Sum(r => r.Count());

                float rowSpan = rows[0].Max(t => t.worldBound.xMax) - rows[0].Min(t => t.worldBound.x);
                float cardWidth = rows[0].Sum(t => t.worldBound.width);

                sb.AppendLine($"--- wall packing ({doc.gameObject.name})");
                sb.AppendLine($"    viewport      {vp.width,7:F1} x {vp.height,7:F1} dp");
                sb.AppendLine($"    tile          {t0.width,7:F1} x {t0.height,7:F1} dp   aspect={t0.width / t0.height:F3}");
                sb.AppendLine($"    columns       {cols}    rows laid out {rows.Count}    rows fully in view {inView.Count}");
                sb.AppendLine($"    tiles in view {tilesInView}  of {tiles.Count} laid out");
                sb.AppendLine($"    row span      {rowSpan,7:F1} dp of {vp.width,7:F1} = {100f * rowSpan / vp.width,5:F1}%  (cards + gutters)");
                sb.AppendLine($"    card width    {cardWidth,7:F1} dp of {vp.width,7:F1} = {100f * cardWidth / vp.width,5:F1}%  (cards only)");
                sb.AppendLine($"    CARD AREA     {100f * tilesInView * t0.width * t0.height / (vp.width * vp.height),5:F1}%  of the viewport is card");
            }
            return sb.Length == 0 ? "no .db-grid in any live panel" : sb.ToString();
        }
    }
}
