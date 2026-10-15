using System.Collections.Generic;
using System.IO;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Renders the real <see cref="DeckPileView"/> at its real board position and true 1080p
    /// pixel scale, at representative deck counts, into <c>artifacts/deck-pile-preview/</c>.
    ///
    /// Royal Council uses a separately rendered near-seat and far-seat stack. Count changes only
    /// hover copy and zero-count visibility, so the sweep covers full, low, last-card and empty.
    ///
    /// Enters no play mode and mutates no scene, so it is safe against a live editor session.
    /// </summary>
    public static class DeckPilePreview
    {
        private const float PxPerUnit = 1080f / 9.2f;   // BoardCamera ortho 4.6 => 9.2 units => 1080px

        // ProceduralBoardSetup seat-0 DeckAnchor: xSign -1 * -DeckX, ySign -1 * DeckY.
        private const float DeckX = 7.18f;
        private const float DeckY = -3.53f;

        // A window round the pile with enough margin to show it sitting on the §1.3 baseline and
        // to catch the mana rail's near end for context.
        private const float ViewW = 3.20f;
        private const float ViewH = 3.20f;

        private static readonly int[] Counts = { 30, 3, 1, 0 };

        [MenuItem("Kingdom/Preview/Deck Pile")]
        public static void Render()
        {
            string outDir = Path.Combine(RepoRoot(), "artifacts", "deck-pile-preview");
            Directory.CreateDirectory(outDir);

            var written = new List<string>();
            foreach (int n in Counts)
                written.Add(RenderCase(n, hover: false, outDir));

            // Hover read-out, at a full pile and at a nearly-dead one: the digit has to clear the
            // card-back pattern at both, and it is white-on-black-rim precisely so it can.
            written.Add(RenderCase(30, hover: true, outDir));
            written.Add(RenderCase(3,  hover: true, outDir));

            // One full 1080p board frame so the pile is judged in company — against the mana rail
            // and the backdrop plate, not alone on a card of its own.
            written.Add(RenderCase(30, hover: false, outDir, wide: true));

            written.Add(RenderFlight(outDir));

            Debug.Log($"[DeckPilePreview] wrote {written.Count} PNGs to {outDir}\n  " +
                      string.Join("\n  ", written));
        }

        private static string RenderCase(int count, bool hover, string outDir, bool wide = false)
        {
            float viewW = wide ? 9.2f * (16f / 9f) : ViewW;
            float viewH = wide ? 9.2f             : ViewH;
            int wPx = Mathf.RoundToInt(viewW * PxPerUnit);
            int hPx = Mathf.RoundToInt(viewH * PxPerUnit);

            var root = new GameObject("__DeckPilePreviewRoot");
            try
            {
                AddBackdrop(root.transform);

                DeckPileView view = MakePile(root.transform, "p0", new Vector3(DeckX, DeckY, 0f), count);

                // The wide frame carries BOTH seats, because the stack direction is derived from
                // the anchor and is therefore the one thing a single-seat render cannot check.
                if (wide) MakePile(root.transform, "p1", new Vector3(-DeckX, -DeckY, 0f), count);

                if (hover)
                {
                    Transform label = view.transform.Find("Count");
                    if (label != null) label.gameObject.SetActive(true);
                }

                Vector3 camAt = wide ? new Vector3(0f, 0f, -10f)
                                     : new Vector3(DeckX, DeckY, -10f);
                var cam = BuildCamera(root.transform, camAt, viewW, viewH);

                string tag = wide ? "board" : (hover ? "hover" : "pile");
                return Capture(cam, wPx, hPx,
                    Path.Combine(outDir, $"deck_{tag}_{count:00}.png"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// One full-board frame with the draw flight captured at five points along its curve, all
        /// composited at once — the path from pile to fan slot, which a live match shows for a
        /// quarter of a second and no single still can show at all.
        ///
        /// Both ends are real: the origin is the seat-0 DeckAnchor, the destination is
        /// HandFanLayout's own solve for slot 4 of a 5-card hand, and the poses come from
        /// CardDrawFlight.SampleAt — the shipping easing, not a copy of it.
        /// </summary>
        private static string RenderFlight(string outDir)
        {
            var root = new GameObject("__DeckFlightPreviewRoot");
            try
            {
                AddBackdrop(root.transform);
                MakePile(root.transform, "p0", new Vector3(DeckX, DeckY, 0f), 29);

                // Real fan solve: a 5-card hand, and the card being dealt is the new right-hand one.
                const int hand = 5;
                var slots = new Transform[hand];
                for (int i = 0; i < hand; i++)
                {
                    var s = new GameObject($"__HandSlot{i}");
                    s.transform.SetParent(root.transform, false);
                    slots[i] = s.transform;
                }
                HandFanLayout.Apply(slots, hand, mirrored: false);

                Vector3 from = new Vector3(DeckX, DeckY, 0f);
                float[] ks = { 0f, 0.25f, 0.5f, 0.75f, 1f };
                foreach (float k in ks)
                {
                    var card = MakeCardBack(slots[hand - 1]);
                    CardDrawFlight.Launch(card.transform, from);
                    card.GetComponent<CardDrawFlight>().SampleAt(k);
                    // Later samples in front, so the strip reads as motion toward the hand.
                    card.GetComponentInChildren<SpriteRenderer>().sortingOrder = Mathf.RoundToInt(k * 10f);
                }

                var cam = BuildCamera(root.transform, new Vector3(0f, 0f, -10f), 9.2f * (16f / 9f), 9.2f);
                return Capture(cam, 1920, 1080, Path.Combine(outDir, "deck_flight.png"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // A bare card-back quad standing in for a CardPresenter: the flight only ever moves the
        // presenter's ROOT transform, so its whole visual hierarchy is irrelevant to the path.
        private static GameObject MakeCardBack(Transform parent)
        {
            var root = new GameObject("__FlightCard");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            var go = new GameObject("Back");
            go.transform.SetParent(root.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.sprite   = new EditorUiSpriteProvider().Get("card_back");
            // The flight lands in HAND, so the stand-in is a HAND-sized card (NOR-244: a card in
            // hand draws at HandFanLayout.RestScale). A board-sized quad here would show the flight
            // settling into a slot far smaller than the card that actually arrives in it.
            sr.size     = new Vector2(HandFanLayout.HandCardW, HandFanLayout.HandCardH);
            return root;
        }

        // The shipping component, driven through its own Init/SetCount — the harness computes no
        // layer arithmetic of its own. Init parents the view under the anchor, which is under
        // root, so the whole tree dies with root in the caller's finally.
        private static DeckPileView MakePile(Transform root, string seat, Vector3 at, int count)
        {
            var anchor = new GameObject($"__DeckAnchor_{seat}");
            anchor.transform.SetParent(root, false);
            anchor.transform.position = at;

            var view = new GameObject($"__DeckPile_{seat}").AddComponent<DeckPileView>();
            view.Init(new Core.Contracts.PlayerId($"preview-{seat}"), anchor.transform, isLocalSeat: seat == "p0");
            view.SetCount(count);
            return view;
        }

        private static void AddBackdrop(Transform parent)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Art/Backgrounds/BoardPlate_Master.png");
            if (tex == null)
            {
                Debug.LogWarning("[DeckPilePreview] BoardPlate_Master.png missing — pile renders on black.");
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

        private static Camera BuildCamera(Transform parent, Vector3 pos, float viewW, float viewH)
        {
            var go = new GameObject("__DeckPreviewCam");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;

            var cam = go.AddComponent<Camera>();
            cam.orthographic     = true;
            cam.orthographicSize = viewH * 0.5f;
            cam.aspect           = viewW / viewH;
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

        // Application.dataPath is <repo>/unity/Assets.
        private static string RepoRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
    }
}
