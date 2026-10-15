using System.Collections.Generic;
using System.IO;
using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Renders the death burn across its whole parameter range — _DissolveAmount at 0, 0.25, 0.5,
    /// 0.75, 1.0 — driving the REAL <see cref="CardDeathDissolve"/> on a REAL card built by
    /// BoardController's own CreatePresenter/GetOrCreate path.
    ///
    /// THE SWEEP IS THE POINT, and so is the realness. A dissolve looks plausible at any single
    /// sampled amount; what a sweep catches is a burn that eats the frame before the art, an edge
    /// that vanishes at the extremes, or noise pitched so fine it reads as fizz at 137x194 px. And
    /// a mock card would prove only that the harness agrees with itself — the thing under test is
    /// specifically whether a 13-renderer, 3-material-family hierarchy flattens into one coherent
    /// surface, which no stand-in quad can answer.
    ///
    /// Two scales are written per step: the TRUE board pixel size, which is how Sam will actually
    /// see it, and a 4x blow-up for judging the edge. Plus one capture of the live card before it
    /// is ever touched — comparing that against amount 0.00 is what proves the snapshot is
    /// faithful, rather than merely pretty.
    ///
    /// Enters no play mode and leaves no scene objects behind.
    /// </summary>
    public static class CardDissolvePreview
    {
        // Unity's working directory is the PROJECT folder (unity), not the repo root, so
        // a relative path here lands two levels too deep. Application.dataPath is Assets/.
        private static string OutDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/dissolve-preview"));

        private const float PxPerUnit = 1080f / 9.2f;   // BoardCamera ortho 4.6 => 9.2 units => 1080px

        // A window a little wider than the card so the burn edge is judged against the board plate
        // it will actually sit on, not cropped flush at the card border.
        private const float ViewW = 1.60f;
        private const float ViewH = 2.15f;

        private static readonly float[] Sweep = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        // A minion with real illustration + frame chrome, so the flatten is exercised across art,
        // frame, gem, keyword icons and six TMP labels rather than a bare body quad.
        private const int CardId = 1003;   // ember_cinder_drake

        [MenuItem("Kingdom/Preview/Card Dissolve")]
        public static void Render()
        {
            Directory.CreateDirectory(OutDir);

            var root = new GameObject("__CardDissolvePreviewRoot");
            var written = new List<string>();
            try
            {
                AddBackdrop(root.transform);

                CardPresenter presenter = BuildRealCard(root.transform);
                if (presenter == null)
                {
                    Debug.LogError($"[CardDissolvePreview] could not build a presenter for '{CardId}'.");
                    return;
                }

                Transform visual = presenter.transform.Find("Visual");
                if (visual == null)
                {
                    Debug.LogError("[CardDissolvePreview] card has no 'Visual' child — " +
                                   "CreatePresenter's wrapper changed and CardDeathDissolve will miss it too.");
                    return;
                }

                // Reference frame: the live hierarchy, untouched. If this and dissolve_0.00 differ,
                // the RenderTexture snapshot is lying and nothing after it can be trusted.
                written.AddRange(Capture(root.transform, presenter.transform.position, "dissolve_live_reference"));

                var fx = presenter.gameObject.AddComponent<CardDeathDissolve>();
                if (!fx.Prepare(visual))
                {
                    Debug.LogError("[CardDissolvePreview] CardDeathDissolve.Prepare failed — " +
                                   "check the CardCapture layer exists and M_Dissolve is on disk " +
                                   "(Kingdom/Regenerate/Shared Materials).");
                    return;
                }

                foreach (float amount in Sweep)
                {
                    fx.SetAmount(amount);
                    written.AddRange(Capture(root.transform, presenter.transform.position,
                                             $"dissolve_{amount:0.00}"));
                }

                fx.Cleanup();

                written.Add(RenderAoe());

                Debug.Log($"[CardDissolvePreview] wrote {written.Count} PNGs to {OutDir}\n  " +
                          string.Join("\n  ", written));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Three DIFFERENT cards burning at once, at three different amounts — the board-wipe case.
        /// This is the one that can fail in a way the single-card sweep cannot: every dissolve
        /// shares one static capture camera, and each needs its own RenderTexture and its own
        /// material instance. If the shared camera leaked its target, or the material were the
        /// project asset instead of an instance, all three would show the SAME card at the SAME
        /// amount — and an AoE is exactly when a player is looking hardest.
        /// </summary>
        private static string RenderAoe()
        {
            var root = new GameObject("__CardDissolveAoeRoot");
            try
            {
                AddBackdrop(root.transform);

                int[] ids        = { 1001 /*ember_recruit*/, 1002 /*ember_zealot*/, 1003 /*ember_cinder_drake*/ };
                float[]  amounts = { 0.2f, 0.45f, 0.7f };

                for (int i = 0; i < ids.Length; i++)
                {
                    CardPresenter p = BuildRealCard(root.transform, ids[i], i + 1);
                    if (p == null) continue;
                    p.transform.position = new Vector3((i - 1) * 1.35f, 0f, 0f);

                    Transform visual = p.transform.Find("Visual");
                    var fx = p.gameObject.AddComponent<CardDeathDissolve>();
                    if (fx.Prepare(visual)) fx.SetAmount(amounts[i]);
                }

                var written = Capture(root.transform, Vector3.zero, "dissolve_aoe_3up", wide: true);
                foreach (string s in written) return s;   // one file; wide-only
                return "dissolve_aoe_3up (nothing written)";
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // Drives BoardController itself: the same Initialize the game calls (minus anchors, input
        // and runner, all of which are optional), then the editor-only hook onto GetOrCreate.
        private static CardPresenter BuildRealCard(Transform parent, int cardId = CardId, int instanceId = 1)
        {
            var host = new GameObject("__PreviewBoardController");
            host.transform.SetParent(parent, false);

            var p0 = new PlayerId("preview-p0");
            var p1 = new PlayerId("preview-p1");

            var board = host.AddComponent<BoardController>();
            board.Initialize(CardDatabaseLoader.Load(), null, p0, p1);

            var ci = new CardInstance(new InstanceId(instanceId), cardId, p0)
            {
                Zone = Zone.Board,
                CurrentAttack = 4,
                CurrentHealth = 4,
                MaxHealth = 4,
            };
            return board.EditorBuildCard(ci, p0, Zone.Board);
        }

        // Writes the same frame twice: true board pixels, and 4x for inspecting the edge.
        private static List<string> Capture(Transform parent, Vector3 at, string stem, bool wide = false)
        {
            float viewW = wide ? 4.60f : ViewW;
            float viewH = wide ? 2.40f : ViewH;

            var written = new List<string>();
            foreach (int scale in wide ? new[] { 2 } : new[] { 1, 4 })
            {
                int wPx = Mathf.RoundToInt(viewW * PxPerUnit) * scale;
                int hPx = Mathf.RoundToInt(viewH * PxPerUnit) * scale;

                var camGo = new GameObject("__DissolvePreviewCam");
                camGo.transform.SetParent(parent, false);
                camGo.transform.position = new Vector3(at.x, at.y, at.z - 10f);

                var cam = camGo.AddComponent<Camera>();
                cam.orthographic     = true;
                cam.orthographicSize = viewH * 0.5f;
                cam.aspect           = viewW / viewH;
                cam.clearFlags       = CameraClearFlags.SolidColor;
                cam.backgroundColor  = Color.black;
                cam.nearClipPlane    = 0.1f;
                cam.farClipPlane     = 50f;
                // Everything EXCEPT the capture layer — a leaked capture layer must show up here
                // as a missing card, not be quietly papered over by rendering it anyway.
                cam.cullingMask      = ~(1 << LayerMask.NameToLayer(CardDeathDissolve.CaptureLayerName));

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
                Debug.LogWarning("[CardDissolvePreview] BoardPlate_Master.png missing — burn renders on black.");
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
