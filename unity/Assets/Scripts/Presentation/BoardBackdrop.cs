using System.Collections.Generic;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Spawns the full-board backdrop plate behind every play element.
    /// The master (tooling/scripts/build_board_master.py) spans the complete 20:9
    /// board-art contract (±10.2 world units at ortho 4.6,
    /// hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1),
    /// either authored directly at that ratio or extended from a legacy 16:9 plate.
    /// Every supported landscape aspect crops it and none letterboxes. There is no
    /// portrait orientation. Swapping the board art preserves the runtime address, but a composition
    /// that moves painted UI furniture must also update the measured artwork UVs registered by
    /// <c>ProceduralBoardSetup</c>, <c>EndTurnButtonView</c> and <c>TurnClockView</c>.
    /// </summary>
    public static class BoardBackdrop
    {
        private const string EditorPath = "Assets/Art/Backgrounds/BoardPlate_Master.png";
        private const string Address = "backgrounds/BoardPlate_Master";

        // 2 × BoardCamera orthographic size 4.6 — the height the plate is AUTHORED against.
        // SafeAreaCamera raises the camera's ortho size to clear a device's insets (EF-062),
        // and the plate has to grow with it or the zoom-out uncovers its edges; SetVisibleHeight
        // below is how that happens. DeviceLayout.DesignHalfHeight is the same number and the
        // two are asserted equal by DeviceLayoutVerify.
        private const float VisibleHeight = 9.2f;

        // Cards sit at z <= 0 stepping toward the camera (negative z). The board art is the
        // physical-looking surface immediately behind them, not a distant sky plate; +0.65 keeps
        // it behind every live element while limiting perspective drift between art and anchors.
        private const float BehindZ = 0.65f;
        private const int SortOrder = -1000;

        // The live plate, so SetVisibleHeight can reach it. A destroyed GameObject compares
        // equal to null under Unity's backdraft, so a scene unload clears this on its own and a
        // second Create simply replaces it.
        private static Transform s_instance;
        private static SpriteRenderer s_renderer;

        private readonly struct ArtworkAnchor
        {
            public readonly Transform Target;
            public readonly Vector2 Uv;
            public readonly float ForegroundZ;

            public ArtworkAnchor(Transform target, Vector2 uv, float foregroundZ)
            {
                Target = target;
                Uv = uv;
                ForegroundZ = foregroundZ;
            }
        }

        // UI fixtures in the generated board are part of the texture, while the camera fits and
        // cover-scales that texture per device. Their live transforms therefore cannot be stable
        // world constants. Keep the authored UV and re-project it onto the gameplay plane after
        // every camera/backdrop fit.
        private static readonly List<ArtworkAnchor> ArtworkAnchors = new();

        public static void RegisterArtworkAnchor(
            Transform target,
            Vector2 artworkUv,
            float foregroundZ = 0f)
        {
            if (target == null) return;

            for (int i = ArtworkAnchors.Count - 1; i >= 0; i--)
            {
                if (ArtworkAnchors[i].Target == null || ArtworkAnchors[i].Target == target)
                    ArtworkAnchors.RemoveAt(i);
            }

            ArtworkAnchors.Add(new ArtworkAnchor(
                target,
                new Vector2(Mathf.Clamp01(artworkUv.x), Mathf.Clamp01(artworkUv.y)),
                foregroundZ));
            AlignArtworkAnchors(Camera.main);
        }

        /// <summary>
        /// Rescale the plate to span <paramref name="visibleHeight"/> world units, matching
        /// whatever orthographicSize SafeAreaCamera solved for the device's safe area (EF-062).
        /// A no-op at the authored 9.2, and safe to call before the backdrop exists.
        /// </summary>
        public static void SetVisibleHeight(float visibleHeight)
        {
            if (s_instance == null || visibleHeight <= 0f) return;
            float k = visibleHeight / VisibleHeight;
            s_instance.localScale = new Vector3(k, k, 1f);
            AlignArtworkAnchors(Camera.main);
        }

        /// <summary>
        /// Size the board-art plane to cover the full perspective viewport. Safe-area fitting may
        /// retreat or offset the camera, so a fixed world-size sprite can otherwise expose an edge.
        /// A uniform cover scale preserves the art's aspect ratio and spends any excess as crop.
        /// </summary>
        public static void FitToCamera(Camera camera)
        {
            if (s_instance == null || s_renderer == null || s_renderer.sprite == null || camera == null)
                return;
            if (camera.orthographic)
            {
                SetVisibleHeight(camera.orthographicSize * 2f);
                return;
            }

            var plane = new Plane(Vector3.forward, new Vector3(0f, 0f, BehindZ));
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;

            foreach (Vector2 corner in new[]
                     {
                         new Vector2(0f, 0f), new Vector2(1f, 0f),
                         new Vector2(0f, 1f), new Vector2(1f, 1f)
                     })
            {
                Ray ray = camera.ViewportPointToRay(new Vector3(corner.x, corner.y, 0f));
                if (!plane.Raycast(ray, out float distance)) return;
                Vector3 point = ray.GetPoint(distance);
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
            }

            Vector2 spriteSize = s_renderer.sprite.bounds.size;
            float cover = Mathf.Max((maxX - minX) / spriteSize.x, (maxY - minY) / spriteSize.y);
            s_instance.SetPositionAndRotation(
                new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, BehindZ),
                Quaternion.identity);
            s_instance.localScale = new Vector3(cover, cover, 1f);
            AlignArtworkAnchors(camera);
        }

        private static void AlignArtworkAnchors(Camera camera)
        {
            if (camera == null || s_instance == null || s_renderer == null
                || s_renderer.sprite == null)
                return;

            Bounds bounds = s_renderer.sprite.bounds;
            for (int i = ArtworkAnchors.Count - 1; i >= 0; i--)
            {
                ArtworkAnchor registration = ArtworkAnchors[i];
                if (registration.Target == null)
                {
                    ArtworkAnchors.RemoveAt(i);
                    continue;
                }

                var localArtworkPoint = new Vector3(
                    Mathf.Lerp(bounds.min.x, bounds.max.x, registration.Uv.x),
                    Mathf.Lerp(bounds.min.y, bounds.max.y, registration.Uv.y),
                    0f);
                Vector3 artworkPoint = s_instance.TransformPoint(localArtworkPoint);
                Vector3 viewport = camera.WorldToViewportPoint(artworkPoint);
                if (viewport.z <= 0f) continue;

                Ray ray = camera.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
                var foreground = new Plane(
                    Vector3.forward,
                    new Vector3(0f, 0f, registration.ForegroundZ));
                if (foreground.Raycast(ray, out float distance))
                    registration.Target.position = ray.GetPoint(distance);
            }
        }

        public static void Create(Transform parent)
        {
            ArtworkAnchors.Clear();
            var tex = LoadAsset<Texture2D>(EditorPath, Address);
            if (tex == null) return;

            var go = new GameObject("BoardBackdrop");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, BehindZ);
            s_instance = go.transform;

            // SpriteRenderer, not a quad primitive: the default sprite material
            // honors alpha and survives URP builds (LoadingOverlay precedent).
            var sr = go.AddComponent<SpriteRenderer>();
            s_renderer = sr;
            sr.sprite = Sprite.Create(tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), pixelsPerUnit: tex.height / VisibleHeight);
            sr.sortingOrder = SortOrder;
        }

        private static T LoadAsset<T>(string editorPath, string address) where T : Object
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(editorPath);
#else
            // Build path: needs the asset in an Addressables group at this address
            // (MaterialCatalog precedent). Registered in the Backgrounds group 2026-07-26.
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<T>(address);
            var asset = handle.WaitForCompletion();
#endif
            if (asset == null)
                Debug.LogWarning($"[BoardBackdrop] asset '{address}' not found — backdrop skipped.");
            return asset;
        }
    }
}
