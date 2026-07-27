using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Spawns the full-board backdrop plate behind every play element.
    /// The master (tooling/scripts/build_board_master.py) is a 16:9 plate whose
    /// decorated ruin ring sits at the 16:9 frame, extended with mirrored bleed to
    /// the 20:9 span (±10.2 world units at ortho 4.6, board-and-vfx.md §1.1) — so
    /// every supported landscape aspect crops it and none letterboxes. There is no
    /// portrait orientation. Swapping the board art is overwriting
    /// BoardPlate_Master.png; nothing here changes.
    /// </summary>
    public static class BoardBackdrop
    {
        private const string EditorPath = "Assets/Art/Backgrounds/BoardPlate_Master.png";
        private const string Address = "backgrounds/BoardPlate_Master";

        private const float VisibleHeight = 9.2f; // 2 × BoardCamera orthographic size 4.6

        // Cards sit at z ≤ 0 stepping toward the camera (negative z); +5 is safely
        // behind everything. The sprite is transparent-queue, so it depth-tests
        // behind the opaque card bodies and the sorting order settles the rest.
        private const float BehindZ = 5f;
        private const int SortOrder = -1000;

        public static void Create(Transform parent)
        {
            var tex = LoadAsset<Texture2D>(EditorPath, Address);
            if (tex == null) return;

            var go = new GameObject("BoardBackdrop");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, BehindZ);

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
