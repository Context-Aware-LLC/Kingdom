using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Editor
{
    /// <summary>
    /// Points the Editor's Game View at the reference device's resolution and measures what the
    /// live panel actually laid out, so a layout question can be answered here instead of by
    /// building an APK.
    ///
    /// <para><b>WAS `DeviceDpRig`, AND THE RENAME IS THE POINT (#349).</b> This file used to exist
    /// because the Editor could not reproduce the device: the panels ran ConstantPhysicalSize
    /// against <c>Screen.dpi</c>, which is ~420 on the phone and 96 in the Editor, so one 1080-tall
    /// screen was a 411dp panel on the device and an 1800dp panel here — 4.4x more vertical budget
    /// than the phone had, with nothing warning you. The rig's job was to close that gap by
    /// swapping every UIDocument onto a clone carrying <c>referenceDpi = 160 * 96 / 420</c>.</para>
    ///
    /// <para><b>THAT GAP IS CLOSED IN THE ASSET NOW, AND THE CLONE IS DELETED.</b> The panels run
    /// ScaleWithScreenSize against 1920x1080 matched on HEIGHT, so a panel is 1080 authored px tall
    /// in the Editor, on the phone, on a tablet and on a desktop — identically, with no dpi in the
    /// arithmetic anywhere. <c>referenceDpi</c> is still 160 in the asset and is INERT;
    /// ScaleWithScreenSize does not read it, so writing it achieved nothing. Setting the Game View
    /// to 2400x1080 is now the whole of the rig: the same resolution gives the same panel.</para>
    ///
    /// <para><b>What is left is the half that always mattered</b> — <see cref="Measure"/> and
    /// <see cref="MeasureWallPacking"/>, which read <c>worldBound</c> off the live tree. They were
    /// never about dp and are unaffected.</para>
    ///
    /// <para><b>Do not judge shape from a Game View capture.</b> The capture letterboxes to
    /// 1280x720, so a 2400x1080 view comes back horizontally squashed and circles read as ovals.
    /// Measure <c>worldBound</c>.</para>
    ///
    /// <para>RETIREMENT TRIGGER: if <c>KingdomPanelSettings</c> leaves ScaleWithScreenSize, or its
    /// reference resolution stops being 1920x1080 matched on height, then the Editor stops
    /// reproducing the device by resolution alone and this file needs its scale half back. The
    /// deleted version is in git at 2026-09-03.</para>
    /// </summary>
    public static class DeviceLayoutRig
    {
        /// <summary>Pixel 5a, the reference device.</summary>
        public const int DeviceWidth = 2400;
        public const int DeviceHeight = 1080;

        private const string SizeLabel = "Kingdom Device (2400x1080)";

        [MenuItem("Kingdom/Dev/Game View → Pixel 5a")]
        public static void Apply()
        {
            Debug.Log($"[DeviceLayoutRig] {SetGameViewSize()}. The panel matches the device by "
                    + "resolution alone — ScaleWithScreenSize, so there is no dpi to correct.");
        }

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

        // ---- Measurement -----------------------------------------------------------------------

        /// <summary>
        /// Dumps the live panel's authored-px size and the worldBound of every element matching one
        /// of the given class or name selectors. Returns text, so it reads the same through
        /// <c>unity command eval</c> as it does in the console.
        /// </summary>
        public static string Measure(params string[] selectors)
        {
            var sb = new StringBuilder();
            var docs = UnityEngine.Object.FindObjectsByType<UIDocument>();
            if (docs.Length == 0) return "no UIDocument in the scene (are you in play mode?)";

            foreach (var doc in docs)
            {
                VisualElement root = doc.rootVisualElement;
                if (root == null) continue;
                sb.AppendLine($"--- {doc.gameObject.name}   panelSettings={doc.panelSettings?.name}");
                sb.AppendLine($"    Screen {Screen.width}x{Screen.height}  Screen.dpi={Screen.dpi}");
                float ph = root.worldBound.height;
                // AUTHORED px, NOT dp. A panel height that is not ~1080 means the panel is not on
                // ScaleWithScreenSize/1920x1080/match-height and every number below is off-scale.
                sb.AppendLine($"    panel  {root.worldBound.width:F1} x {ph:F1} authored px   scale={(Screen.height / Mathf.Max(1f, ph)):F3}x");

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
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>())
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
                sb.AppendLine($"    viewport      {vp.width,7:F1} x {vp.height,7:F1} px");
                sb.AppendLine($"    tile          {t0.width,7:F1} x {t0.height,7:F1} px   aspect={t0.width / t0.height:F3}");
                sb.AppendLine($"    columns       {cols}    rows laid out {rows.Count}    rows fully in view {inView.Count}");
                sb.AppendLine($"    tiles in view {tilesInView}  of {tiles.Count} laid out");
                sb.AppendLine($"    row span      {rowSpan,7:F1} px of {vp.width,7:F1} = {100f * rowSpan / vp.width,5:F1}%  (cards + gutters)");
                sb.AppendLine($"    card width    {cardWidth,7:F1} px of {vp.width,7:F1} = {100f * cardWidth / vp.width,5:F1}%  (cards only)");
                sb.AppendLine($"    CARD AREA     {100f * tilesInView * t0.width * t0.height / (vp.width * vp.height),5:F1}%  of the viewport is card");
            }
            return sb.Length == 0 ? "no .db-grid in any live panel" : sb.ToString();
        }
    }
}
