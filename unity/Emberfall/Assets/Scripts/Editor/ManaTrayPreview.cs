using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Renders the mana grid through the real <see cref="ManaTrayView"/> for a spread of ManaPool
    /// states and writes one PNG per state to <c>artifacts/mana-tray-preview/</c>, at the pixel
    /// size the rail actually occupies on a 1080p screen. Sibling in spirit to
    /// <see cref="TurnClockVerify"/> and <see cref="B3TransportVerify"/>: a named one-click check
    /// of a slice the .NET tests cannot show.
    ///
    /// Why the states below and not a single sample: the rail was previously signed off from one
    /// offscreen render at Max = 10, which is the only cap at which the old binder's divide-by-Max
    /// happened to be correct. Every earlier turn drew the wrong number of lit crystals and no
    /// screenshot existed to show it. The Max &lt; 10 rows are the regression.
    ///
    /// Reads nothing and enters no play mode, so it is safe to run against a live editor session.
    /// </summary>
    public static class ManaTrayPreview
    {
        private const float PxPerUnit = 1080f / 9.2f;   // BoardCamera ortho 4.6 => 9.2 units => 1080px
        private const float RailX = 5.76f;              // ProceduralBoardSetup seat-0 ManaTrayAnchor
        private const float RailY = -3.35f;
        private const float ViewW = 1.22f;              // grid 0.92 plus a margin either side
        private const float ViewH = 2.60f;              // grid 2.30 plus a margin above and below

        private readonly struct Case
        {
            public readonly string Name;
            public readonly int Max, Available, Spent, Locked, Temporary;

            public Case(string name, int max, int available, int spent, int locked, int temporary)
            {
                Name = name; Max = max; Available = available;
                Spent = spent; Locked = locked; Temporary = temporary;
            }
        }

        private static readonly Case[] Cases =
        {
            new Case("turn01-fresh",      1,  1, 0, 0, 0),
            new Case("turn04-fresh",      4,  4, 0, 0, 0),
            new Case("turn04-half-spent", 4,  1, 3, 0, 0),
            new Case("cap-half-spent",   10,  5, 5, 0, 0),
            new Case("all-spent",         6,  0, 6, 0, 0),
            new Case("backdraft-2locked",  6,  4, 0, 2, 0),
            // Temporary is a SUBSET of Available — GAIN_MANA's default branch raises both
            // (ActionHandlers.cs). A rail that counted them separately lit one orb too many.
            new Case("coin-temp",         3,  4, 0, 0, 1),
            new Case("cap-full",         10, 10, 0, 0, 0),
        };

        [MenuItem("Emberfall/Preview Mana Tray (offscreen)")]
        public static void Render()
        {
            string outDir = Path.Combine(RepoRoot(), "artifacts", "mana-tray-preview");
            Directory.CreateDirectory(outDir);

            var written = new List<string>();
            foreach (Case c in Cases)
                written.Add(RenderCase(c, outDir));

            Debug.Log($"[ManaTrayPreview] wrote {written.Count} PNGs to {outDir}\n  " +
                      string.Join("\n  ", written));
        }

        private static string RenderCase(Case c, string outDir)
        {
            int wPx = Mathf.RoundToInt(ViewW * PxPerUnit);
            int hPx = Mathf.RoundToInt(ViewH * PxPerUnit);

            var root = new GameObject("__ManaTrayPreviewRoot");
            try
            {
                AddBackdrop(root.transform);

                var anchor = new GameObject("__ManaTrayAnchor");
                anchor.transform.SetParent(root.transform, false);
                anchor.transform.position = new Vector3(RailX, RailY, 0f);

                // The real component against a real GameState — this renders what the shipping
                // binder computes, never a re-derivation of its arithmetic in the harness.
                GameState state = BuildState(c, out PlayerId localId);
                var view = new GameObject("__ManaTray").AddComponent<ManaTrayView>();
                view.Init(localId, anchor.transform);
                view.OnStateUpdated(state);
                view.transform.SetParent(root.transform, true);

                var cam = BuildCamera(root.transform);
                return Capture(cam, wPx, hPx, Path.Combine(outDir,
                    $"mana_{c.Name}_max{c.Max}_a{c.Available}_s{c.Spent}_l{c.Locked}_t{c.Temporary}.png"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static GameState BuildState(Case c, out PlayerId localId)
        {
            var p0Id = new PlayerId("preview-p0");
            var p1Id = new PlayerId("preview-p1");
            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(1), p0Id, CardClass.EMBERWARD, 30));
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(2), p1Id, CardClass.TIDECALLER, 30));

            p0.Mana.Max       = c.Max;
            p0.Mana.Available = c.Available;
            p0.Mana.Spent     = c.Spent;
            p0.Mana.Locked    = c.Locked;
            p0.Mana.Temporary = c.Temporary;

            localId = p0Id;
            return new GameState(new MatchId("mana-preview"), new DeterministicRandom(1UL), p0, p1);
        }

        private static void AddBackdrop(Transform parent)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/Backgrounds/BoardPlate_Master.png");
            if (tex == null)
            {
                Debug.LogWarning("[ManaTrayPreview] BoardPlate_Master.png missing — rail renders on black.");
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

        private static Camera BuildCamera(Transform parent)
        {
            var go = new GameObject("__ManaPreviewCam");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(RailX, RailY, -10f);

            var cam = go.AddComponent<Camera>();
            cam.orthographic     = true;
            cam.orthographicSize = ViewH * 0.5f;
            cam.aspect           = ViewW / ViewH;
            cam.clearFlags       = CameraClearFlags.SolidColor;
            cam.backgroundColor  = Color.black;
            cam.nearClipPlane    = 0.1f;
            cam.farClipPlane     = 50f;
            return cam;
        }

        private static string Capture(Camera cam, int wPx, int hPx, string path)
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

        // Application.dataPath is <repo>/unity/Emberfall/Assets.
        private static string RepoRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
    }
}
