using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Renders the freeze cue across its whole parameter range — _FrostAmount at 0, 0.25, 0.5,
    /// 0.75, 1.0 — over a REAL card built by BoardController's own CreatePresenter path.
    ///
    /// THE SWEEP IS THE POINT. A frost overlay looks plausible at any single sampled amount; what
    /// a sweep catches is a cue that is invisible below 0.5, or opaque enough at 1.0 to bury the
    /// stats a frozen minion still needs to show. Two scales per step: TRUE board pixel size,
    /// which is how it will actually be seen, and 4x for judging the ice edge.
    ///
    /// THIS HARNESS BUILDS THE OVERLAY QUAD ITSELF, because nothing else does yet. As of
    /// 2026-07-26 CardMaterialBinder pushes _FrostAmount at the BODY renderer, which wears
    /// M_CardBody (SG_CardComposite) — a shader that does not declare _FrostAmount — and no
    /// renderer anywhere wears M_Frost. So the freeze cue does not appear in game no matter what
    /// this render shows. What this answers is the question that has to come first: what does
    /// SG_Frost actually draw, and is it worth wiring? The quad below is also the shape the fix
    /// takes — a sibling of CreatePresenter's GlowOverlay, one step nearer the camera.
    ///
    /// Enters no play mode and leaves no scene objects behind.
    /// </summary>
    public static class CardFrostPreview
    {
        // Unity's working directory is the PROJECT folder (unity/Emberfall), not the repo root, so
        // a relative path here lands two levels too deep. Application.dataPath is Assets/.
        private static string OutDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../../../artifacts/frost-preview"));

        private const float PxPerUnit = 1080f / 9.2f;   // BoardCamera ortho 4.6 => 9.2 units => 1080px

        // Wider than the card so the ice edge is judged against the board plate it will sit on.
        private const float ViewW = 1.60f;
        private const float ViewH = 2.15f;

        private static readonly float[] Sweep = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        // A minion with real illustration + frame chrome, so the overlay is judged over art,
        // parchment and stat bosses rather than a flat quad.
        private const string CardId = "cinder_drake";

        [MenuItem("Emberfall/Preview Card Frost (offscreen)")]
        public static void Render()
        {
            Directory.CreateDirectory(OutDir);

            var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_Frost.mat");
            if (mat == null)
            {
                Debug.LogError("[CardFrostPreview] M_Frost.mat missing — run " +
                               "Emberfall/Materials/Create Shared Materials.");
                return;
            }
            if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
            {
                Debug.LogError("[CardFrostPreview] M_Frost resolves to the error shader. A Shader " +
                               "Graph's shader is a generated sub-asset — a hand-authored .mat " +
                               "cannot know its fileID. Re-author via Create Shared Materials.");
                return;
            }

            var root = new GameObject("__CardFrostPreviewRoot");
            var written = new List<string>();
            try
            {
                AddBackdrop(root.transform);

                CardPresenter presenter = BuildRealCard(root.transform);
                if (presenter == null)
                {
                    Debug.LogError($"[CardFrostPreview] could not build a presenter for '{CardId}'.");
                    return;
                }

                // Reference: the card with no overlay at all. Comparing this against amount 0.00
                // is what proves the overlay is inert when it should be, rather than merely faint.
                written.AddRange(Capture(root.transform, presenter.transform.position, "frost_no_overlay"));

                Renderer overlay = AddFrostOverlay(presenter.transform, mat);

                // One instance, not the project asset: a shared material would make every frozen
                // minion on the board show the last one's amount.
                var mpb = new MaterialPropertyBlock();
                int frostAmount = Shader.PropertyToID("_FrostAmount");   // ShaderIds is internal
                foreach (float amount in Sweep)
                {
                    overlay.GetPropertyBlock(mpb);
                    mpb.SetFloat(frostAmount, amount);
                    overlay.SetPropertyBlock(mpb);
                    written.AddRange(Capture(root.transform, presenter.transform.position,
                                             $"frost_{amount:0.00}"));
                }

                Debug.Log($"[CardFrostPreview] wrote {written.Count} PNGs to {Path.GetFullPath(OutDir)}\n  " +
                          string.Join("\n  ", written));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// The quad CreatePresenter does not build yet: card-sized, one step nearer the camera than
        /// the glow overlay, wearing M_Frost. Sibling in every respect to the GlowOverlay above it.
        /// </summary>
        private static Renderer AddFrostOverlay(Transform card, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "FrostOverlay";
            go.transform.SetParent(card.Find("Visual") ?? card, false);
            // BoardController.CardW/CardH are internal to the runtime assembly; this harness lives
            // in the Editor one. Same numbers, and the sweep below is what would catch a drift.
            go.transform.localScale    = new Vector3(1.17f, 1.65f, 1f);
            go.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            Object.DestroyImmediate(go.GetComponent<MeshCollider>());

            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            return r;
        }

        // Drives BoardController itself: the same Initialize the game calls (minus anchors, input
        // and runner, all of which are optional), then the editor-only hook onto GetOrCreate.
        private static CardPresenter BuildRealCard(Transform parent)
        {
            var host = new GameObject("__PreviewBoardController");
            host.transform.SetParent(parent, false);

            var p0 = new PlayerId("preview-p0");
            var p1 = new PlayerId("preview-p1");

            var board = host.AddComponent<BoardController>();
            board.Initialize(CardDatabaseLoader.Load(), null, p0, p1);

            var ci = new CardInstance(new InstanceId(1), CardId, p0)
            {
                Zone = Zone.Board,
                CurrentAttack = 4,
                CurrentHealth = 4,
                MaxHealth = 4,
            };
            return board.EditorBuildCard(ci, p0, Zone.Board);
        }

        // Writes the same frame twice: true board pixels, and 4x for inspecting the ice edge.
        private static List<string> Capture(Transform parent, Vector3 at, string stem)
        {
            var written = new List<string>();
            foreach (int scale in new[] { 1, 4 })
            {
                int wPx = Mathf.RoundToInt(ViewW * PxPerUnit) * scale;
                int hPx = Mathf.RoundToInt(ViewH * PxPerUnit) * scale;

                var camGo = new GameObject("__FrostPreviewCam");
                camGo.transform.SetParent(parent, false);
                camGo.transform.position = new Vector3(at.x, at.y, at.z - 10f);

                var cam = camGo.AddComponent<Camera>();
                cam.orthographic     = true;
                cam.orthographicSize = ViewH * 0.5f;
                cam.aspect           = ViewW / ViewH;
                cam.clearFlags       = CameraClearFlags.SolidColor;
                cam.backgroundColor  = Color.black;
                cam.nearClipPlane    = 0.1f;
                cam.farClipPlane     = 50f;

                string suffix = scale == 1 ? "true" : $"{scale}x";
                written.Add(Write(cam, wPx, hPx, Path.Combine(OutDir, $"{stem}_{suffix}.png")));
                Object.DestroyImmediate(camGo);
            }
            return written;
        }

        private static string Write(Camera cam, int wPx, int hPx, string path)
        {
            var rt = new RenderTexture(wPx, hPx, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            rt.Create();

            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(wPx, hPx, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, wPx, hPx), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            File.WriteAllBytes(path, tex.EncodeToPNG());

            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            return Path.GetFileName(path);
        }

        private static void AddBackdrop(Transform parent)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/Backgrounds/BoardPlate_Master.png");
            if (tex == null)
            {
                Debug.LogWarning("[CardFrostPreview] BoardPlate_Master.png missing — frost renders on black.");
                return;
            }

            var go = new GameObject("__Backdrop");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(0f, 0f, 5f);   // BoardBackdrop.BehindZ
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                      new Vector2(0.5f, 0.5f), tex.height / 9.2f);
            sr.sortingOrder = -1000;                           // BoardBackdrop.SortOrder
        }
    }
}
