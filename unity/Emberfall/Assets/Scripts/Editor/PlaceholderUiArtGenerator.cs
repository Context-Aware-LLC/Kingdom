// =============================================================================
// PlaceholderUiArtGenerator.cs
// -----------------------------------------------------------------------------
// Procedurally generates placeholder UI sprite PNGs for the card frame/rarity
// system (frames, rarity gems, keyword icons) and writes them to disk under
// Assets/Art/UI/. Pure procedural drawing (no external assets, no fonts, no
// Random) so output is deterministic.
//
// NOTE: The project's CardArtPostprocessor force-applies Sprite import settings
// to everything under Assets/Art/UI/. This generator intentionally does NOT set
// any TextureImporter settings; it only writes PNG bytes + AssetDatabase.Refresh.
//
// Emits 12 frames + 4 gems + 16 keyword icons = 32 PNGs.
//
// Changelog:
//   2026-07-11  Initial version. 12 frames (512), 4 gems (128), 16 icons (128).
// =============================================================================
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Editor utility that emits deterministic placeholder UI sprites for the
    /// frame / rarity / keyword-icon system. Invoke via the Emberfall menu or
    /// call <see cref="GenerateAll"/> from other editor code / a RunCommand.
    /// </summary>
    public static class PlaceholderUiArtGenerator
    {
        // ----- Output roots -------------------------------------------------
        private const string FramesDir = "Assets/Art/UI/Frames";
        private const string GemsDir   = "Assets/Art/UI/Gems";
        private const string IconsDir  = "Assets/Art/UI/Icons";

        // ----- Frame geometry (shared across all 12 frames) -----------------
        private const int FrameSize   = 512;
        private const float CornerRadius = 64f;   // outer rounded-rect radius
        private const float HoleInset    = 56f;   // constant inset of illustration window (all frames)
        private const float HoleRadius   = 40f;   // rounded-rect radius of the hole
        private const float BaseBorder   = 48f;   // nominal border thickness

        // ----- Gem / icon geometry ------------------------------------------
        private const int SmallSize   = 128;
        private const float GemRadius = 56f;

        [MenuItem("Emberfall/Generate Placeholder UI Art")]
        public static void Generate()
        {
            GenerateAll();
        }

        /// <summary>
        /// Generates all placeholder PNGs. Returns the number of files written.
        /// Safe to call from batch / RunCommand contexts.
        /// </summary>
        public static int GenerateAll()
        {
            Directory.CreateDirectory(FramesDir);
            Directory.CreateDirectory(GemsDir);
            Directory.CreateDirectory(IconsDir);

            int written = 0;
            written += GenerateFrames();
            written += GenerateGems();
            written += GenerateIcons();

            AssetDatabase.Refresh();
            Debug.Log($"[PlaceholderUiArtGenerator] Wrote {written} placeholder UI PNGs " +
                      $"(frames + gems + icons) under Assets/Art/UI/. Expected 32.");
            return written;
        }

        // ===================================================================
        //  FRAMES
        // ===================================================================

        private static readonly string[] FrameTypes    = { "minion", "spell", "weapon" };
        private static readonly string[] RarityKeys     = { "common", "rare", "epic", "legendary" };

        private static int GenerateFrames()
        {
            int count = 0;
            foreach (string type in FrameTypes)
            {
                Color baseCol = BaseBorderColor(type);
                foreach (string rarity in RarityKeys)
                {
                    Texture2D tex = BuildFrame(baseCol, rarity);
                    string path = $"{FramesDir}/frame_{type}_{rarity}.png";
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    Object.DestroyImmediate(tex);
                    count++;
                }
            }
            return count;
        }

        private static Color BaseBorderColor(string type)
        {
            switch (type)
            {
                case "minion": return new Color(0.30f, 0.34f, 0.42f, 1f); // steel blue-grey
                case "spell":  return new Color(0.28f, 0.24f, 0.44f, 1f); // arcane indigo
                case "weapon": return new Color(0.40f, 0.30f, 0.18f, 1f); // forged bronze
                default:       return new Color(0.30f, 0.34f, 0.42f, 1f);
            }
        }

        /// <summary>
        /// Rarity modulation: returns the final border color plus a thickness
        /// multiplier and whether an ornate second (outer) ring is drawn.
        /// The tint is a blend from the type base toward a rarity accent hue.
        /// </summary>
        private static void RarityStyle(Color baseCol, string rarity,
            out Color borderCol, out float thicknessMul, out bool ornate, out Color accent)
        {
            switch (rarity)
            {
                case "rare":
                    accent = new Color(0.25f, 0.55f, 0.95f, 1f);
                    borderCol = Brighten(Blend(baseCol, accent, 0.55f), 1.12f);
                    thicknessMul = 1.0f;
                    ornate = false;
                    break;
                case "epic":
                    accent = new Color(0.62f, 0.25f, 0.85f, 1f);
                    borderCol = Brighten(Blend(baseCol, accent, 0.62f), 1.22f);
                    thicknessMul = 1.05f;
                    ornate = false;
                    break;
                case "legendary":
                    accent = new Color(0.95f, 0.75f, 0.15f, 1f);
                    borderCol = Brighten(Blend(baseCol, accent, 0.72f), 1.35f);
                    thicknessMul = 1.5f;  // thicker / ornate
                    ornate = true;
                    break;
                case "common":
                default:
                    accent = baseCol;
                    borderCol = Dim(baseCol, 0.85f); // base, dim
                    thicknessMul = 1.0f;
                    ornate = false;
                    break;
            }
        }

        private static Texture2D BuildFrame(Color baseCol, string rarity)
        {
            RarityStyle(baseCol, rarity, out Color borderCol, out float thicknessMul,
                out bool ornate, out Color accent);

            var tex = NewTex(FrameSize, FrameSize);
            var px = new Color[FrameSize * FrameSize];

            float border = BaseBorder * thicknessMul;

            // Outer rounded-rect boundary of the whole card.
            float outerInset = 8f;
            // Inner edge of the border ring (where the border ends).
            float borderInnerInset = outerInset + border;

            // The illustration window (hole). CONSTANT across all 12 frames per
            // spec: inset a fixed HoleInset from each edge regardless of border.
            float holeInset = HoleInset;

            for (int y = 0; y < FrameSize; y++)
            {
                for (int x = 0; x < FrameSize; x++)
                {
                    // Signed coverage for the outer card boundary (1 inside, 0 outside).
                    float outerCov = RoundedRectCoverage(x, y, outerInset, FrameSize, CornerRadius);
                    // Coverage for the border's inner boundary.
                    float borderInnerCov = RoundedRectCoverage(x, y, borderInnerInset, FrameSize,
                        Mathf.Max(4f, CornerRadius - border));
                    // Coverage for the illustration hole (constant geometry).
                    float holeCov = RoundedRectCoverage(x, y, holeInset, FrameSize, HoleRadius);

                    // Border ring = inside outer AND outside border-inner AND outside hole.
                    float ringMask = outerCov * (1f - borderInnerCov);
                    // Ensure the hole is always transparent even if border is thin.
                    ringMask *= (1f - holeCov);

                    Color c = borderCol;

                    if (ringMask > 0.001f)
                    {
                        // Subtle bevel: brighter toward the outer edge, darker inward.
                        float bevel = Mathf.Clamp01(outerCov - borderInnerCov);
                        float shade = Mathf.Lerp(0.82f, 1.12f, bevel);
                        c = Brighten(borderCol, shade);

                        // Legendary ornate flourish: an inner accent pinstripe drawn
                        // ONLY in the outer margin (never intrudes on the hole).
                        if (ornate)
                        {
                            float mid = RoundedRectCoverage(x, y, outerInset + border * 0.62f,
                                FrameSize, Mathf.Max(4f, CornerRadius - border * 0.62f));
                            float midEdge = RoundedRectCoverage(x, y, outerInset + border * 0.72f,
                                FrameSize, Mathf.Max(4f, CornerRadius - border * 0.72f));
                            float pin = Mathf.Clamp01(mid - midEdge);
                            if (pin > 0.001f)
                                c = Brighten(accent, 1.15f);
                        }
                    }

                    int i = y * FrameSize + x;
                    px[i] = new Color(c.r, c.g, c.b, ringMask);
                }
            }

            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        // ===================================================================
        //  GEMS
        // ===================================================================

        private static int GenerateGems()
        {
            int count = 0;
            foreach (string rarity in RarityKeys)
            {
                Color c = GemColor(rarity);
                Texture2D tex = BuildGem(c);
                string path = $"{GemsDir}/gem_{rarity}.png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                count++;
            }
            return count;
        }

        private static Color GemColor(string rarity)
        {
            switch (rarity)
            {
                case "rare":      return new Color(0.25f, 0.55f, 0.95f, 1f);
                case "epic":      return new Color(0.62f, 0.25f, 0.85f, 1f);
                case "legendary": return new Color(0.95f, 0.75f, 0.15f, 1f);
                case "common":
                default:          return new Color(0.55f, 0.55f, 0.58f, 1f);
            }
        }

        private static Texture2D BuildGem(Color col)
        {
            var tex = NewTex(SmallSize, SmallSize);
            var px = new Color[SmallSize * SmallSize];

            float cx = SmallSize * 0.5f;
            float cy = SmallSize * 0.5f;
            // Specular highlight center (upper-left of the gem).
            float sx = cx - GemRadius * 0.35f;
            float sy = cy + GemRadius * 0.35f;

            for (int y = 0; y < SmallSize; y++)
            {
                for (int x = 0; x < SmallSize; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    // Antialiased circle coverage.
                    float cov = Mathf.Clamp01(GemRadius - dist + 0.5f);
                    if (cov <= 0.001f)
                    {
                        px[y * SmallSize + x] = Clear;
                        continue;
                    }

                    // Radial gradient: bright center -> darker rim.
                    float t = Mathf.Clamp01(dist / GemRadius);
                    float shade = Mathf.Lerp(1.25f, 0.55f, t);
                    Color c = Brighten(col, shade);

                    // Small specular highlight.
                    float sdx = x + 0.5f - sx;
                    float sdy = y + 0.5f - sy;
                    float sdist = Mathf.Sqrt(sdx * sdx + sdy * sdy);
                    float spec = Mathf.Clamp01(1f - sdist / (GemRadius * 0.45f));
                    spec *= spec; // tighten
                    c = Color.Lerp(c, Color.white, spec * 0.65f);

                    px[y * SmallSize + x] = new Color(c.r, c.g, c.b, cov);
                }
            }

            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        // ===================================================================
        //  KEYWORD ICONS
        // ===================================================================

        private static readonly string[] Keywords =
        {
            "bulwark", "fervor", "skirmish", "aegis", "herald", "toll",
            "lifedrain", "doublestrike", "shroud", "venom", "arcane_surge",
            "frost", "backdraft", "echo", "rekindle", "silence"
        };

        private static int GenerateIcons()
        {
            int count = 0;
            int n = Keywords.Length;
            for (int k = 0; k < n; k++)
            {
                string kw = Keywords[k];
                // Distinct hue per keyword, evenly spread around the wheel.
                float hue = (float)k / n;
                Color hue1 = Color.HSVToRGB(hue, 0.55f, 0.85f);
                Color hue2 = Color.HSVToRGB((hue + 0.04f) % 1f, 0.65f, 0.55f);
                Texture2D tex = BuildIcon(kw, hue1, hue2, k);
                string path = $"{IconsDir}/icon_kw_{kw}.png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                count++;
            }
            return count;
        }

        private static Texture2D BuildIcon(string keyword, Color backTop, Color backBottom, int index)
        {
            var tex = NewTex(SmallSize, SmallSize);
            var px = new Color[SmallSize * SmallSize];

            float pad = 12f;
            float backRadius = 26f;

            // 1) Rounded-square backing with a subtle vertical gradient.
            for (int y = 0; y < SmallSize; y++)
            {
                float gt = (float)y / SmallSize;
                Color back = Color.Lerp(backBottom, backTop, gt);
                for (int x = 0; x < SmallSize; x++)
                {
                    float cov = RoundedRectCoverage(x, y, pad, SmallSize, backRadius);
                    int i = y * SmallSize + x;
                    if (cov <= 0.001f)
                    {
                        px[i] = Clear;
                        continue;
                    }
                    px[i] = new Color(back.r, back.g, back.b, cov);
                }
            }

            // 2) Foreground glyph: the first 1-2 letters of the keyword, drawn as
            //    simple 3x5 pixel-block letters. Monochrome-ish (near-white).
            string glyph = GlyphFor(keyword);
            Color ink = new Color(0.96f, 0.97f, 1f, 1f);
            DrawText(px, SmallSize, glyph, ink);

            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        private static string GlyphFor(string keyword)
        {
            // Two-letter abbreviations chosen to be visually distinct.
            switch (keyword)
            {
                case "bulwark":     return "BU";
                case "fervor":      return "FE";
                case "skirmish":    return "SK";
                case "aegis":       return "AE";
                case "herald":      return "HE";
                case "toll":        return "TO";
                case "lifedrain":   return "LI";
                case "doublestrike": return "DO";
                case "shroud":      return "SH";
                case "venom":       return "VE";
                case "arcane_surge": return "AS";
                case "frost":       return "FR";
                case "backdraft":   return "BA";
                case "echo":         return "EC";
                case "rekindle":    return "RE";
                case "silence":      return "SI";
                default:             return "??";
            }
        }

        // ===================================================================
        //  DRAWING HELPERS
        // ===================================================================

        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        private static Texture2D NewTex(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return new Color(
                Mathf.Lerp(a.r, b.r, t),
                Mathf.Lerp(a.g, b.g, t),
                Mathf.Lerp(a.b, b.b, t),
                1f);
        }

        private static Color Brighten(Color c, float mul)
        {
            return new Color(
                Mathf.Clamp01(c.r * mul),
                Mathf.Clamp01(c.g * mul),
                Mathf.Clamp01(c.b * mul),
                1f);
        }

        private static Color Dim(Color c, float mul) => Brighten(c, mul);

        /// <summary>
        /// Antialiased coverage (0..1) for a point (x,y) being inside a rounded
        /// rectangle inset by <paramref name="inset"/> from each edge of a
        /// <paramref name="size"/>-square texture, with corner radius
        /// <paramref name="radius"/>. Uses a signed distance for a ~1px soft edge.
        /// </summary>
        private static float RoundedRectCoverage(int x, int y, float inset, int size, float radius)
        {
            float px = x + 0.5f;
            float py = y + 0.5f;

            float minX = inset;
            float minY = inset;
            float maxX = size - inset;
            float maxY = size - inset;

            float r = Mathf.Min(radius, Mathf.Min((maxX - minX), (maxY - minY)) * 0.5f);
            if (r < 0f) r = 0f;

            // Distance from the rounded-rect boundary (negative inside).
            // Half-extent box centered.
            float hx = (maxX - minX) * 0.5f;
            float hy = (maxY - minY) * 0.5f;
            float ccx = (minX + maxX) * 0.5f;
            float ccy = (minY + maxY) * 0.5f;

            float qx = Mathf.Abs(px - ccx) - (hx - r);
            float qy = Mathf.Abs(py - ccy) - (hy - r);

            float outsideDist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) +
                                           Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
            float insideDist = Mathf.Min(Mathf.Max(qx, qy), 0f);
            float sdf = outsideDist + insideDist - r;

            // Convert SDF to ~1px antialiased coverage (negative = inside).
            return Mathf.Clamp01(0.5f - sdf);
        }

        // ----- Minimal 3x5 pixel font (uppercase letters + digits used) -----

        private static void DrawText(Color[] px, int size, string text, Color ink)
        {
            // Each glyph is 3 wide x 5 tall in "cells"; we scale up to fill the
            // icon nicely and center the whole string.
            const int glyphW = 3;
            const int glyphH = 5;
            const int spacing = 1; // cell columns between glyphs

            int cellsWide = text.Length * glyphW + (text.Length - 1) * spacing;
            int cellsTall = glyphH;

            // Scale so the text roughly fills the inner area with margin.
            float avail = size * 0.62f;
            int scale = Mathf.Max(1, Mathf.FloorToInt(Mathf.Min(avail / cellsWide, avail / cellsTall)));

            int totalW = cellsWide * scale;
            int totalH = cellsTall * scale;
            int originX = (size - totalW) / 2;
            int originY = (size - totalH) / 2;

            for (int ci = 0; ci < text.Length; ci++)
            {
                byte[] rows = Font(text[ci]);
                int glyphOriginX = originX + ci * (glyphW + spacing) * scale;

                for (int gy = 0; gy < glyphH; gy++)
                {
                    // rows[0] is the TOP row; texture y=0 is bottom, so flip.
                    byte rowBits = rows[gy];
                    int texRow = glyphH - 1 - gy;
                    for (int gx = 0; gx < glyphW; gx++)
                    {
                        bool on = (rowBits & (1 << (glyphW - 1 - gx))) != 0;
                        if (!on) continue;
                        // Fill the scaled block.
                        int bx0 = glyphOriginX + gx * scale;
                        int by0 = originY + texRow * scale;
                        for (int sy = 0; sy < scale; sy++)
                        {
                            int yy = by0 + sy;
                            if (yy < 0 || yy >= size) continue;
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int xx = bx0 + sx;
                                if (xx < 0 || xx >= size) continue;
                                px[yy * size + xx] = ink;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 3x5 bitmap font. Each returned byte uses the low 3 bits as columns
        /// (bit2=left .. bit0=right). Index 0 = top row. Covers the uppercase
        /// letters used by the keyword glyphs.
        /// </summary>
        private static byte[] Font(char c)
        {
            switch (c)
            {
                case 'A': return new byte[] { 0b010, 0b101, 0b111, 0b101, 0b101 };
                case 'B': return new byte[] { 0b110, 0b101, 0b110, 0b101, 0b110 };
                case 'C': return new byte[] { 0b011, 0b100, 0b100, 0b100, 0b011 };
                case 'D': return new byte[] { 0b110, 0b101, 0b101, 0b101, 0b110 };
                case 'E': return new byte[] { 0b111, 0b100, 0b110, 0b100, 0b111 };
                case 'F': return new byte[] { 0b111, 0b100, 0b110, 0b100, 0b100 };
                case 'H': return new byte[] { 0b101, 0b101, 0b111, 0b101, 0b101 };
                case 'I': return new byte[] { 0b111, 0b010, 0b010, 0b010, 0b111 };
                case 'L': return new byte[] { 0b100, 0b100, 0b100, 0b100, 0b111 };
                case 'O': return new byte[] { 0b010, 0b101, 0b101, 0b101, 0b010 };
                case 'P': return new byte[] { 0b110, 0b101, 0b110, 0b100, 0b100 };
                case 'R': return new byte[] { 0b110, 0b101, 0b110, 0b101, 0b101 };
                case 'S': return new byte[] { 0b011, 0b100, 0b010, 0b001, 0b110 };
                case 'T': return new byte[] { 0b111, 0b010, 0b010, 0b010, 0b010 };
                case 'U': return new byte[] { 0b101, 0b101, 0b101, 0b101, 0b011 };
                case 'W': return new byte[] { 0b101, 0b101, 0b101, 0b111, 0b101 };
                case 'Z': return new byte[] { 0b111, 0b001, 0b010, 0b100, 0b111 };
                default:  return new byte[] { 0b111, 0b101, 0b101, 0b101, 0b111 }; // box fallback
            }
        }
    }
}
#endif
