using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>Verifies playable Royal Council content across device safe areas.</summary>
    public static class DeviceLayoutVerify
    {
        [MenuItem("Kingdom/Verify/Royal Council Layout")]
        public static void Check()
        {
            CheckRoyalCouncilRig();
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
    }
}
