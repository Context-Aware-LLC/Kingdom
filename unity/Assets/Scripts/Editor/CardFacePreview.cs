using System.Collections.Generic;
using System.IO;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using UnityEngine;
using UnityEditor;

namespace Kingdom.Editor
{
    /// <summary>
    /// Bakes real cards through the real <see cref="CardFaceBaker"/> and composites them into one
    /// contact sheet at the desktop collection-wall geometry — 620 × 870 faces, 5 per row, on the wall's
    /// own 20px gutter and dark surface (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.9,
    /// hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/design-system.md §2.1) — written to <c>artifacts/card-face-preview/</c>.
    ///
    /// THE GEOMETRY IS THE POINT. What this sheet answers is one question the .NET tests and every
    /// full-size render cannot: is a card FACE legible as a 620 × 870 tile, sitting next to four
    /// others, at the density a scrolling wall actually has. Thumbnails butted together with no
    /// gutter answer a different question and answer it wrong — that exact mistake was made once on
    /// this project already (see the collection mock, 2026-07-26), and the sheet that settled it had
    /// to composite the real frame at the real tile size before anything could be judged.
    ///
    /// THE SPREAD IS COVERAGE, THEN WORST CASE. Every rarity is a different painted frame with its
    /// own socket table, and every card type shows a different set of stat digits — so all four
    /// rarities and all three types are taken first. The remainder is filled with the set's LONGEST
    /// rules text, because the body band is where the 18px-equivalent floor is actually contested
    /// (§2.9 puts the wall body at 20.4px, dropping to 14.7px once the fixed wrap box overflows).
    ///
    /// Enters no play mode and leaves no scene objects behind.
    /// </summary>
    public static class CardFacePreview
    {
        // A REVIEW RESOLUTION, not a layout size — this sheet is looked at on a desk, so it is
        // rendered far larger than any device draws a tile. It used to read CardWall's largest
        // fixed tier; those were deleted 2026-09-01 once it was shown no device could ever select
        // one, and the number is inlined here rather than reintroducing a constant nothing else
        // wants. 2 complete rows, no more than 5 per row — two rather than three because the sheet
        // is judged on density and legibility and a third identical row adds no information.
        private const int FaceW  = 620;
        private const int FaceH  = 870;
        private const int Columns = 5;
        private const int Rows    = 2;
        private const int Cards   = Columns * Rows;

        // --space-5 (20px) is the wall's inter-card gap; --space-12 (48px) is the desktop screen
        // gutter, standing in here for the wall's outer margin (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/design-system.md §2.1).
        private const int Gutter = 20;
        private const int Margin = 48;

        // --surface-1, the screen background the wall sits on (UI/Common/theme.uss).
        private static readonly Color Surface1 = new Color32(20, 25, 34, 255);

        [MenuItem("Kingdom/Preview/Card Faces")]
        public static void Render()
        {
            ICardDatabase db = CardDatabaseLoader.Load();
            RenderSpread(db, PickSpread(db), "card_faces");
        }

        /// <summary>
        /// The same sheet, restricted to an explicit id list — for looking at one import batch
        /// rather than a representative spread. Ids that are not in the database are reported
        /// rather than skipped: a typo'd id and a row that failed to merge look identical
        /// otherwise, and the second one is the failure this is meant to catch.
        /// </summary>
        public static void RenderIds(IEnumerable<int> cardIds, string stem)
        {
            ICardDatabase db = CardDatabaseLoader.Load();
            var spread = new List<CardDefinition>();
            var missing = new List<int>();
            foreach (int id in cardIds)
            {
                if (db.TryGet(id, out CardDefinition def)) spread.Add(def);
                else                                       missing.Add(id);
            }
            if (missing.Count > 0)
                Debug.LogError($"[CardFacePreview] not in the database: {string.Join(", ", missing)}");
            RenderSpread(db, spread, stem);
        }

        private static void RenderSpread(ICardDatabase db, List<CardDefinition> spread, string stem)
        {
            string outDir = Path.Combine(RepoRoot(), "artifacts", "card-face-preview");
            Directory.CreateDirectory(outDir);

            if (spread.Count == 0)
            {
                Debug.LogError("[CardFacePreview] nothing to render.");
                return;
            }

            // Pixels are lifted out of each face inside the bake callback, because the faces are
            // RenderTextures owned by the baker and the baker is destroyed in the finally below.
            var faces = new List<Color[]>(spread.Count);
            var ids   = new List<int>(spread.Count);

            var host = new GameObject("__CardFacePreviewHost");
            CardFaceBaker baker = null;
            try
            {
                baker = host.AddComponent<CardFaceBaker>();
                baker.Initialize(db);
                baker.SetFaceSize(FaceW, FaceH);

                foreach (CardDefinition def in spread)
                {
                    CardDefinition captured = def;
                    baker.Bake(captured, face =>
                    {
                        if (face == null)
                        {
                            Debug.LogWarning($"[CardFacePreview] '{captured.Id}' baked to nothing — " +
                                             $"check the {CardSnapshot.CaptureLayerName} layer exists in TagManager.");
                            return;
                        }
                        faces.Add(ReadBack(face, FaceW, FaceH));
                        ids.Add(captured.Id);
                    });
                }
            }
            finally
            {
                // Release the RenderTextures BEFORE the host goes, rather than leaving it to
                // OnDestroy: edit mode has to use DestroyImmediate, and doing that from inside
                // another object's teardown is the kind of thing Unity is entitled to refuse.
                if (baker != null) baker.ClearCache();
                Object.DestroyImmediate(host);
            }

            if (faces.Count == 0)
            {
                Debug.LogError("[CardFacePreview] nothing baked — no sheet written.");
                return;
            }

            string path = Path.Combine(outDir, $"{stem}_{FaceW}x{FaceH}_{Columns}up.png");
            WriteSheet(faces, path);

            Debug.Log($"[CardFacePreview] wrote {path}\n" +
                      $"  {faces.Count} faces at {FaceW}x{FaceH}, {Columns} per row, {Gutter}px gutter\n  " +
                      string.Join("\n  ", ids));
        }

        // THE EMBERWARD/TIDECALLER BATCH MENU ITEM WAS REMOVED 2026-09-04 (Sam, auditing the
        // Kingdom menu: "so many of those are one offs and have no future use"). It was a fixed
        // list of the twelve cards imported on 2026-07-26, kept to prove that VANGUARD and
        // LIFEDRAIN pills and the first weapon face rendered at all. They have rendered for weeks,
        // both keywords ship on more cards than those two, and the id list could only ever go
        // stale. RenderIds below is the general form and outlived it — call it from a scratch
        // menu item with whatever ids the question is actually about.

        // ── Spread ──────────────────────────────────────────────────────────────────

        private static List<CardDefinition> PickSpread(ICardDatabase db)
        {
            var all = new List<CardDefinition>(db.AllCollectible());
            all.Sort((a, b) => a.Id.CompareTo(b.Id));   // deterministic across runs

            var picked = new List<CardDefinition>();
            var seen   = new HashSet<int>();

            void Take(CardDefinition d)
            {
                if (d != null && picked.Count < Cards && seen.Add(d.Id)) picked.Add(d);
            }

            // Coverage: one card per painted frame, one per card type. HERO_POWER is excluded —
            // it is never a wall tile and has no collectible row.
            foreach (Rarity r in new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY })
                Take(all.Find(d => d.Rarity == r));
            foreach (CardType t in new[] { CardType.MINION, CardType.SPELL, CardType.WEAPON })
                Take(all.Find(d => d.Type == t));

            // Worst case: longest name + rules text first. Ties break on id so the sheet is the
            // same sheet every run and two renders can be diffed.
            var byLength = new List<CardDefinition>(all);
            byLength.Sort((a, b) =>
            {
                int la = a.Name.Length + a.Text.Length;
                int lb = b.Name.Length + b.Text.Length;
                return la == lb ? a.Id.CompareTo(b.Id) : lb.CompareTo(la);
            });
            foreach (CardDefinition d in byLength) Take(d);

            return picked;
        }

        // ── Composite ───────────────────────────────────────────────────────────────

        private static void WriteSheet(List<Color[]> faces, string path)
        {
            int rows = Mathf.CeilToInt(faces.Count / (float)Columns);
            int sheetW = Margin * 2 + Columns * FaceW + (Columns - 1) * Gutter;
            int sheetH = Margin * 2 + rows    * FaceH + (rows    - 1) * Gutter;

            var sheet = new Color[sheetW * sheetH];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = Surface1;

            for (int i = 0; i < faces.Count; i++)
            {
                int col = i % Columns;
                int row = i / Columns;

                // Texture space is bottom-up; the wall reads top-down, so row 0 is the TOP row.
                int x0 = Margin + col * (FaceW + Gutter);
                int y0 = sheetH - Margin - (row + 1) * FaceH - row * Gutter;

                Color[] face = faces[i];
                for (int y = 0; y < FaceH; y++)
                {
                    int dstRow = (y0 + y) * sheetW + x0;
                    int srcRow = y * FaceW;
                    for (int x = 0; x < FaceW; x++)
                    {
                        Color src = face[srcRow + x];
                        Color dst = sheet[dstRow + x];
                        // The capture clears to transparent, so the frame's rounded corners and
                        // any un-drawn margin have to composite onto the wall, not punch a hole.
                        sheet[dstRow + x] = new Color(
                            src.r * src.a + dst.r * (1f - src.a),
                            src.g * src.a + dst.g * (1f - src.a),
                            src.b * src.a + dst.b * (1f - src.a), 1f);
                    }
                }
            }

            var tex = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false);
            tex.SetPixels(sheet);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        private static Color[] ReadBack(Texture face, int w, int h)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = face as RenderTexture;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            Color[] pixels = tex.GetPixels();
            Object.DestroyImmediate(tex);
            return pixels;
        }

        // Application.dataPath is <repo>/unity/Assets.
        private static string RepoRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
    }
}
