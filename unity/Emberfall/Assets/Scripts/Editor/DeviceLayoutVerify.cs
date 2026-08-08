using System.IO;
using System.Text.RegularExpressions;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// EF-062. Holds <see cref="DeviceLayout"/>'s constants against the three things outside
    /// its own assembly that can silently falsify them: the authored orthographicSize in both
    /// board scenes, the real pixel size of the backdrop plate, and the layout constants the
    /// board actually builds from.
    ///
    /// <para>WHY A CHECK AND NOT A COMMENT. Every number this file guards is a claim about the
    /// state of the world with a change that will expire it — moving the hero plate outboard,
    /// re-baking BoardPlate_Master at a different width, nudging a camera in the scene. The
    /// project's rule is that such a claim carries a retirement trigger on the line that
    /// falsifies it; this is that trigger made executable, so the commit that breaks the 16:9
    /// floor fails the harness rather than quietly shipping a clipped hero.</para>
    ///
    /// <para>Reads the scenes as TEXT rather than opening them. Opening a scene in
    /// -nographics batchmode mutates the project and the harness is a read-only check.</para>
    /// </summary>
    public static class DeviceLayoutVerify
    {
        private const string BoardScene = "Assets/Scenes/Board.unity";
        private const string NetworkedScene = "Assets/Scenes/BoardNetworked.unity";
        private const string BackdropTex = "Assets/Art/Backgrounds/BoardPlate_Master.png";

        [MenuItem("Emberfall/Verify Device Layout (EF-062)")]
        public static void Check()
        {
            bool ok = DeviceLayout.SelfCheck(out string report);
            Debug.Log("[DeviceLayoutVerify] DeviceLayout.SelfCheck:\n" + report);
            if (!ok)
                Debug.LogError("[DeviceLayoutVerify] DeviceLayout.SelfCheck FAILED — see the "
                               + "FAIL lines above.");

            ok &= CheckSceneOrtho(BoardScene);
            ok &= CheckSceneOrtho(NetworkedScene);
            ok &= CheckBackdropSize();

            Debug.Log($"[DeviceLayoutVerify] {(ok ? "PASS" : "FAIL")} — supported landscape "
                      + $"aspect {DeviceLayout.MinAspect:F5}..{DeviceLayout.MaxAspect:F5}, "
                      + $"card in hand {DeviceLayout.HandCardPixelWidth(1080, DeviceLayout.DesignHalfHeight):F1}"
                      + $"x{DeviceLayout.HandCardPixelHeight(1080, DeviceLayout.DesignHalfHeight):F1} px "
                      + "at 1080p.");
        }

        private static bool CheckSceneOrtho(string scenePath)
        {
            string full = Path.Combine(Directory.GetParent(Application.dataPath).FullName, scenePath);
            if (!File.Exists(full))
            {
                Debug.LogError($"[DeviceLayoutVerify] scene not found: {scenePath}");
                return false;
            }

            // First match wins. Both board scenes hold exactly one camera today, and if a
            // second is ever added this reads the first one in the file — which fails loudly
            // and sends someone to look, rather than passing on the wrong camera.
            var m = Regex.Match(File.ReadAllText(full), @"^\s*orthographic size:\s*([\d.]+)\s*$",
                                RegexOptions.Multiline);
            if (!m.Success)
            {
                Debug.LogError($"[DeviceLayoutVerify] no 'orthographic size:' in {scenePath} — "
                               + "the board camera is not orthographic, or the scene format "
                               + "changed. DeviceLayout's entire model assumes an ortho camera.");
                return false;
            }

            float authored = float.Parse(m.Groups[1].Value,
                                         System.Globalization.CultureInfo.InvariantCulture);
            if (Mathf.Abs(authored - DeviceLayout.DesignHalfHeight) > 0.0001f)
            {
                Debug.LogError($"[DeviceLayoutVerify] {scenePath} authors orthographic size "
                               + $"{authored} but DeviceLayout.DesignHalfHeight is "
                               + $"{DeviceLayout.DesignHalfHeight}. Every pixel figure in the "
                               + "device-layout spec is derived from that number — change both "
                               + "or neither.");
                return false;
            }

            Debug.Log($"[DeviceLayoutVerify]       ok    {scenePath} orthographic size "
                      + $"{authored} == DesignHalfHeight");
            return true;
        }

        private static bool CheckBackdropSize()
        {
            // Read the PNG's IHDR off disk rather than asking the imported Texture2D. An import
            // maxTextureSize cap would shrink Texture2D.width and make the plate look like it
            // covers LESS than it does — the failure would read as a real coverage regression
            // and send someone re-baking art that is already correct. Art/UI is capped at 2048
            // by CardArtPostprocessor, so this is a live hazard, not a hypothetical one.
            string full = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                                       BackdropTex);
            if (!PngSize(full, out int w, out int h))
            {
                Debug.LogError($"[DeviceLayoutVerify] could not read PNG dimensions from "
                               + $"{BackdropTex} — the backdrop plate is missing or is not a "
                               + "PNG, so MaxAspect cannot be verified.");
                return false;
            }

            if (w != DeviceLayout.BackdropTexWidth || h != DeviceLayout.BackdropTexHeight)
            {
                Debug.LogError($"[DeviceLayoutVerify] BoardPlate_Master is {w}x{h}, but "
                               + $"DeviceLayout says {DeviceLayout.BackdropTexWidth}x"
                               + $"{DeviceLayout.BackdropTexHeight}. Re-baking the plate changes "
                               + $"MaxAspect to {w / (float)h:F5} — update DeviceLayout.MaxAspect "
                               + "and the device-layout spec in the same commit.");
                return false;
            }

            Debug.Log($"[DeviceLayoutVerify]       ok    BoardPlate_Master {w}x{h} == "
                      + $"MaxAspect {DeviceLayout.MaxAspect:F5}");
            return true;
        }

        // PNG IHDR: 8-byte signature, then a 4-byte length, "IHDR", then width and height as
        // big-endian uint32. Fixed by the format, so 24 bytes is always enough.
        private static bool PngSize(string path, out int width, out int height)
        {
            width = height = 0;
            if (!File.Exists(path)) return false;

            var head = new byte[24];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(head, 0, head.Length) != head.Length) return false;
            }

            if (head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G')
                return false;

            width  = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return width > 0 && height > 0;
        }
    }
}
