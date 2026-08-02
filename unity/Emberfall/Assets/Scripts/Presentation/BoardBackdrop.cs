using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Spawns the full-board backdrop plate behind every play element.
    /// The master (tooling/scripts/build_board_master.py) is a 16:9 plate whose
    /// decorated ruin ring sits at the 16:9 frame, extended with mirrored bleed to
    /// the 20:9 span (±10.2 world units at ortho 4.6, hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1) — so
    /// every supported landscape aspect crops it and none letterboxes. There is no
    /// portrait orientation. Swapping the board art is overwriting
    /// BoardPlate_Master.png; nothing here changes.
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

        // Cards sit at z ≤ 0 stepping toward the camera (negative z); +5 is safely
        // behind everything. The sprite is transparent-queue, so it depth-tests
        // behind the opaque card bodies and the sorting order settles the rest.
        private const float BehindZ = 5f;
        private const int SortOrder = -1000;

        // The live plate, so SetVisibleHeight can reach it. A destroyed GameObject compares
        // equal to null under Unity's overload, so a scene unload clears this on its own and a
        // second Create simply replaces it.
        private static Transform s_instance;

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
        }

        public static void Create(Transform parent)
        {
            var tex = LoadAsset<Texture2D>(EditorPath, Address);
            if (tex == null) return;

            var go = new GameObject("BoardBackdrop");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, BehindZ);
            s_instance = go.transform;

            // SpriteRenderer, not a quad primitive: the default sprite material
            // honors alpha and survives URP builds (LoadingOverlay precedent).
            var sr = go.AddComponent<SpriteRenderer>();
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
