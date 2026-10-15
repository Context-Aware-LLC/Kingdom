using System.IO;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// EF-062. Holds <see cref="DeviceLayout"/>'s constants against the three things outside
    /// its own assembly that can silently falsify them: the perspective camera fit, the real
    /// pixel size of the backdrop plate, and the layout constants the board actually builds from.
    ///
    /// <para>WHY A CHECK AND NOT A COMMENT. Every number this file guards is a claim about the
    /// state of the world with a change that will expire it — moving the hero plate outboard,
    /// re-baking BoardPlate_Master at a different width, nudging a camera in the scene. The
    /// project's rule is that such a claim carries a retirement trigger on the line that
    /// falsifies it; this is that trigger made executable, so the commit that breaks the 16:9
    /// floor fails the harness rather than quietly shipping a clipped hero.</para>
    /// </summary>
    public static class DeviceLayoutVerify
    {
        private const string BackdropTex = "Assets/Art/Backgrounds/BoardPlate_Master.png";

        [MenuItem("Kingdom/Verify/Device Layout")]
        public static void Check()
        {
            bool ok = DeviceLayout.SelfCheck(out string report);
            Debug.Log("[DeviceLayoutVerify] DeviceLayout.SelfCheck:\n" + report);
            if (!ok)
                Debug.LogError("[DeviceLayoutVerify] DeviceLayout.SelfCheck FAILED — see the "
                               + "FAIL lines above.");

            ok &= CheckPerspectiveRig();
            ok &= CheckRoyalCouncilRig();
            ok &= CheckBackdropSize();

            // No MinAspect here any more: the live rig frames a narrow screen rather than clipping
            // it, so there is no lower bound to report. MaxAspect still bounds the backdrop art.
            Debug.Log($"[DeviceLayoutVerify] {(ok ? "PASS" : "FAIL")} — design box "
                      + $"{DeviceLayout.DesignHalfWidth:F2} x {DeviceLayout.DesignHalfHeight:F2} on "
                      + $"backdrop covering |x| <= {DeviceLayout.MaxAspect * DeviceLayout.DesignHalfHeight:F4} "
                      + $"(MaxAspect {DeviceLayout.MaxAspect:F5}), perspective playable content remains "
                      + "inside every tested safe area.");
        }

        private static bool CheckPerspectiveRig()
        {
            var go = new GameObject("DeviceLayoutPerspectiveVerify");
            var camera = go.AddComponent<Camera>();
            bool ok = true;

            try
            {
                BoardCameraRig.Configure(camera);
                ok &= !camera.orthographic;
                ok &= Mathf.Abs(camera.fieldOfView - BoardCameraRig.FieldOfView) < 0.0001f;
                ok &= CheckPerspectiveCase(camera, "1920x1080 full", 1920, 1080,
                    new Rect(0f, 0f, 1920f, 1080f));
                ok &= CheckPerspectiveCase(camera, "2400x1080 full", 2400, 1080,
                    new Rect(0f, 0f, 2400f, 1080f));
                ok &= CheckPerspectiveCase(camera, "2400x1080 bottom inset 132", 2400, 1080,
                    new Rect(0f, 132f, 2400f, 948f));
                ok &= CheckPerspectiveCase(camera, "2400x1080 left cutout 96", 2400, 1080,
                    new Rect(96f, 0f, 2304f, 1080f));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            if (!ok)
                Debug.LogError("[DeviceLayoutVerify] perspective camera rig failed its safe-area fit.");
            else
                Debug.Log("[DeviceLayoutVerify]       ok    perspective camera rig fits all cases");
            return ok;
        }

        private static bool CheckPerspectiveCase(
            Camera camera,
            string label,
            int width,
            int height,
            Rect safeArea)
        {
            camera.aspect = (float)width / height;
            BoardCameraRig.FramingSolution solution = BoardCameraRig.ApplySafeArea(
                camera, safeArea, width, height);

            float safeXMin = safeArea.xMin / width;
            float safeXMax = safeArea.xMax / width;
            float safeYMin = safeArea.yMin / height;
            float safeYMax = safeArea.yMax / height;
            float toleranceX = 0.5f / width;
            float toleranceY = 0.5f / height;
            bool ok = true;
            foreach (float z in new[] { BoardCameraRig.DesignFrontZ, BoardCameraRig.DesignBackZ })
            foreach (float y in new[] { -DeviceLayout.DesignHalfHeight, DeviceLayout.DesignHalfHeight })
            foreach (float x in new[] { -DeviceLayout.DesignHalfWidth, DeviceLayout.DesignHalfWidth })
            {
                Vector3 viewport = camera.WorldToViewportPoint(new Vector3(x, y, z));
                ok &= viewport.z > 0f
                      && viewport.x >= safeXMin - toleranceX
                      && viewport.x <= safeXMax + toleranceX
                      && viewport.y >= safeYMin - toleranceY
                      && viewport.y <= safeYMax + toleranceY;
            }

            Debug.Log($"[DeviceLayoutVerify]       {(ok ? "ok" : "FAIL")}  {label} · "
                      + $"retreat {solution.Retreat:F4} · local offset "
                      + $"({solution.CameraLocalOffset.x:F4}, "
                      + $"{solution.CameraLocalOffset.y:F4}, "
                      + $"{solution.CameraLocalOffset.z:F4})");
            return ok;
        }

        private static bool CheckRoyalCouncilRig()
        {
            var root = new GameObject("RoyalCouncilLayoutVerify");
            var camera = new GameObject("Camera fit check").AddComponent<Camera>();
            camera.transform.SetParent(root.transform, false);
            bool ok = true;
            try
            {
                var anchors = root.AddComponent<BoardAnchors>();
                RoyalCouncilBoard table = RoyalCouncilBoard.TryCreate(root.transform, anchors);
                if (table == null)
                {
                    Debug.LogError("[DeviceLayoutVerify] Royal Council prefab is missing.");
                    return false;
                }
                foreach (var sample in new[]
                {
                    (1920, 1080, new Rect(0, 0, 1920, 1080)),
                    (1440, 1080, new Rect(0, 0, 1440, 1080)),
                    (2400, 1080, new Rect(0, 0, 2400, 1080)),
                    (2400, 1080, new Rect(96, 0, 2304, 1080)),
                    (2400, 1080, new Rect(0, 132, 2400, 948))
                })
                {
                    camera.aspect = sample.Item1 / (float)sample.Item2;
                    Rect safe = sample.Item3;
                    RoyalCouncilCamera.Fit(camera, safe, sample.Item1, sample.Item2);
                    bool Inside(Vector3 point, string label)
                    {
                        Vector3 v = camera.WorldToViewportPoint(point);
                        bool inside = v.z > 0 && safe.Contains(new Vector2(v.x * sample.Item1, v.y * sample.Item2));
                        if (!inside) Debug.LogError($"[DeviceLayoutVerify] {label} outside {safe}: viewport {v}");
                        return inside;
                    }
                    // The 5% framing zoom intentionally uses spare space in the original
                    // conservative fit box. Protect actual playable content, including its
                    // depth, rather than requiring every empty corner of that box on screen.
                    foreach (SeatAnchors seat in new[] { anchors.p0, anchors.p1 })
                    {
                        for (int count = 1; count <= 7; count++)
                        {
                            BoardRowLayout.Apply(seat.BoardSlots, count, anchors.BoardSlotSpacing);
                            for (int i = 0; i < count; i++)
                            for (int x = -1; x <= 1; x += 2)
                            for (int y = -1; y <= 1; y += 2)
                                ok &= Inside(seat.BoardSlots[i].TransformPoint(new Vector3(
                                    x * BoardController.CardW * 0.5f, y * BoardController.CardH * 0.5f, 0)), "Board card corner");
                        }
                        for (int count = 1; count <= 10; count++)
                        {
                            RoyalCouncilBoard.ApplyHandLayout(seat, count);
                            for (int i = 0; i < count; i++)
                            for (int x = -1; x <= 1; x += 2)
                            for (int y = -1; y <= 1; y += 2)
                                ok &= Inside(seat.HandSlots[i].TransformPoint(new Vector3(
                                    x * HandFanLayout.HandCardW * 0.5f, y * HandFanLayout.HandCardH * 0.5f, 0)), "Held card corner");
                        }
                        foreach (Transform socket in new[] { seat.HeroAnchor, seat.HeroPowerAnchor, seat.WeaponAnchor })
                        {
                            float radius = (socket == seat.HeroAnchor ? RoyalCouncilBoard.PortraitDiameter : RoyalCouncilBoard.SocketDiameter) * 0.5f;
                            for (int step = 0; step < 16; step++)
                            {
                                float angle = step * Mathf.PI / 8f;
                                ok &= Inside(socket.TransformPoint(new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0)), socket.name);
                            }
                        }
                    }
                }
            }
            finally { Object.DestroyImmediate(root); }
            if (!ok) Debug.LogError("[DeviceLayoutVerify] Royal Council playable-content safe-area fit failed.");
            else Debug.Log("[DeviceLayoutVerify]       ok    Royal Council 5 safe areas, rows 1..7, held hands 1..10 and hero sockets, both seats");
            return ok;
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
