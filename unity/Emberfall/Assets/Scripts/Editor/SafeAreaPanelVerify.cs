using System.Collections.Generic;
using System.Text;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Editor
{
    /// <summary>
    /// #237. Proves every meta-screen still opts into the safe-area treatment, and that
    /// <see cref="SafeAreaPanel.ApplyInsets"/> puts the numbers where it says it does.
    ///
    /// <para><b>Why a check rather than a look.</b> The whole failure this closes is invisible
    /// off-device: <c>Screen.safeArea</c> is the full screen in the Editor, on desktop, and on any
    /// emulator with the bars hidden, so a screen that has silently lost its treatment renders
    /// pixel-identical to one that has it. The first report is a player whose shard counter is half
    /// off the side of a phone. Both regressions this catches — a new screen without
    /// <c>emb-root</c>, and a scrim that lost <c>emb-bleed</c> in an unrelated UXML edit — look
    /// exactly like nothing at all until then.</para>
    ///
    /// <para><b>What it does NOT cover, stated plainly.</b> It asserts the values this project
    /// writes, not Yoga's response to them. That padding on the root actually displaces an
    /// <c>position: absolute</c> child, and that a negative margin puts a bleed layer back on the
    /// panel edge, are UI Toolkit's behaviour and are only observable in a rendered panel. This
    /// project has no PlayMode test rig to assert them in. They need a device or Editor pass.</para>
    /// </summary>
    public static class SafeAreaPanelVerify
    {
        /// <summary>
        /// Every runtime screen UXML. Deliberately the same list-and-forget shape as
        /// <see cref="UxmlIntegrityCheck"/>'s: add a screen when you build one. Forgetting costs a
        /// screen that is unreadable at the edges on a cutout phone and perfect everywhere else.
        /// </summary>
        private static readonly string[] Screens =
        {
            "Assets/UI/MainMenu/MainMenuScreen.uxml",
            "Assets/UI/CharacterRoster/CharacterRosterScreen.uxml",
            "Assets/UI/Collection/CollectionScreen.uxml",
            "Assets/UI/PackReveal/PackRevealScreen.uxml",
            "Assets/UI/Shop/ShopScreen.uxml",
            "Assets/UI/MatchSettings/Resources/MatchSettingsScreen.uxml",
        };

        // A deliberately asymmetric probe: four different values, so a transposed pair or a
        // sign error cannot pass. Left is the Pixel 5a's real 136 px cutout.
        private static readonly DeviceLayout.PanelInsets Probe =
            new DeviceLayout.PanelInsets(136f, 7f, 23f, 61f);

        [MenuItem("Emberfall/UI/Check Safe-Area Panels")]
        public static void Check()
        {
            bool ok = Run(out string report);
            Debug.Log("[SafeAreaPanelVerify]\n" + report);
            if (!ok)
                Debug.LogError("[SafeAreaPanelVerify] FAILED — see the report above.");
        }

        public static bool Run(out string report)
        {
            var sb = new StringBuilder();
            bool allGood = true;
            int totalBleed = 0;

            sb.AppendLine($"  probe insets {Probe}");

            foreach (string path in Screens)
            {
                var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                if (asset == null)
                {
                    sb.AppendLine($"  FAIL  {path} — no VisualTreeAsset at this path");
                    allGood = false;
                    continue;
                }

                VisualElement tree = asset.CloneTree();
                VisualElement root = tree.Q<VisualElement>(className: SafeAreaPanel.RootClass);
                if (root == null)
                {
                    sb.AppendLine($"  FAIL  {path} — no .{SafeAreaPanel.RootClass} element, so "
                                  + "SafeAreaPanel will skip this screen and it ships uninset");
                    allGood = false;
                    continue;
                }

                List<VisualElement> bleed =
                    root.Query<VisualElement>(className: SafeAreaPanel.BleedClass).ToList();

                SafeAreaPanel.ApplyInsets(root, Probe);

                bool rootOk = Px(root.style.paddingLeft) == Probe.Left
                              && Px(root.style.paddingTop) == Probe.Top
                              && Px(root.style.paddingRight) == Probe.Right
                              && Px(root.style.paddingBottom) == Probe.Bottom;
                if (!rootOk) allGood = false;

                sb.AppendLine($"  {(rootOk ? "ok  " : "FAIL")}  {Name(path)} — root padding "
                              + $"L{Px(root.style.paddingLeft)} T{Px(root.style.paddingTop)} "
                              + $"R{Px(root.style.paddingRight)} B{Px(root.style.paddingBottom)}");

                // A screen with no bleed layer at all is legal but worth naming: it means nothing
                // on it paints to the panel edge, which is unusual enough to be worth a look.
                if (bleed.Count == 0)
                    sb.AppendLine($"        note: no .{SafeAreaPanel.BleedClass} layers — nothing "
                                  + "on this screen paints past the safe area");

                foreach (VisualElement layer in bleed)
                {
                    totalBleed++;
                    bool layerOk = Px(layer.style.marginLeft) == -Probe.Left
                                   && Px(layer.style.marginTop) == -Probe.Top
                                   && Px(layer.style.marginRight) == -Probe.Right
                                   && Px(layer.style.marginBottom) == -Probe.Bottom
                                   && Px(layer.style.paddingLeft) == Probe.Left
                                   && Px(layer.style.paddingTop) == Probe.Top
                                   && Px(layer.style.paddingRight) == Probe.Right
                                   && Px(layer.style.paddingBottom) == Probe.Bottom;
                    if (!layerOk) allGood = false;

                    sb.AppendLine($"        {(layerOk ? "ok  " : "FAIL")}  bleed "
                                  + $"'{layer.name ?? "(unnamed)"}' margin "
                                  + $"L{Px(layer.style.marginLeft)} T{Px(layer.style.marginTop)} "
                                  + $"R{Px(layer.style.marginRight)} B{Px(layer.style.marginBottom)}"
                                  + $" + padding L{Px(layer.style.paddingLeft)}");
                }
            }

            sb.AppendLine($"  {Screens.Length} screens, {totalBleed} full-bleed layers");
            sb.AppendLine("  NOT covered here: that Yoga actually displaces an absolutely "
                          + "positioned child by the parent's padding, and that the negative "
                          + "margin puts a bleed layer back on the panel edge. Rendered-panel "
                          + "behaviour; needs a device or Editor pass.");

            report = sb.ToString().TrimEnd();
            return allGood;
        }

        private static string Name(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }

        /// <summary>Read an inline length back as a plain float. The tree is detached, so
        /// <c>resolvedStyle</c> has no panel to resolve against — the inline
        /// <see cref="IStyle"/> is the only readable copy, and it is the one this code wrote.</summary>
        private static float Px(StyleLength length) => length.value.value;
    }
}
