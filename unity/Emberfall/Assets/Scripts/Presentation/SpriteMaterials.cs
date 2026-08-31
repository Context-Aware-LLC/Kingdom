using System.Collections.Generic;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Assigns a sprite to a <see cref="SpriteRenderer"/> together with a material keyed by that
    /// sprite's TEXTURE. Every runtime-built sprite renderer in the presentation layer goes through
    /// here, and the material is the whole reason.
    ///
    /// <para><b>THE BUG THIS EXISTS TO PREVENT.</b> A <c>SpriteRenderer</c> added with
    /// <c>AddComponent</c> gets Unity's ONE shared <c>Sprite-Unlit-Default</c> instance, and URP's
    /// 2D path batches every renderer sharing a material into a single draw with a SINGLE texture
    /// bound. On the card front the frame, the four sockets and the rarity gem all had that one
    /// material, so all six drew whichever texture won the bind — <c>frame_card</c> — each sampling
    /// it across its own 0..1 UVs. The result is a MINIATURE OF THE WHOLE CARD painted inside every
    /// socket: at board size a smudge, at collection-wall tile size indistinguishable from "the
    /// stat sockets were never implemented", which is how it was reported.</para>
    ///
    /// <para><b>Nothing about the scene graph is wrong when this happens, and that is what costs
    /// the day.</b> Dumping the renderers shows each holding its own correct sprite, correct size,
    /// correct position, active and enabled. The sprite provider resolves every key to the right
    /// texture. The addressable entries are present and prewarm reports no misses. It is right up
    /// until the draw call. Measured 2026-08-31 by capturing the same card twice one frame apart —
    /// as-is, then with a private material per renderer — in one process.</para>
    ///
    /// <para><b>Keyed by TEXTURE, not by renderer.</b> Per-renderer materials fix it too and are
    /// how it was proved, but the collection wall builds ~90 cards and six materials each is 540
    /// instances for one screen. Per texture there are under a dozen for the whole game, and
    /// batching still works INSIDE each group — which is what keeps three stat sockets one draw
    /// instead of three. Same argument as <c>BoardController.s_statLabelMaterial</c>.</para>
    ///
    /// <para>The material is cloned from whatever Unity already handed the renderer, so the shader
    /// stays the render pipeline's own sprite shader. Deliberately NOT a <c>Shader.Find</c>: a
    /// shader reached only by name is invisible to the build stripper and comes back missing in a
    /// player (issue #236) while looking perfect in the editor.</para>
    ///
    /// <para>RETIREMENT TRIGGER: if these sprites are ever packed into one Sprite Atlas they
    /// genuinely share a texture, the batch becomes correct on its own, and this cache should be
    /// deleted rather than left keyed on a texture that no longer varies.</para>
    /// </summary>
    public static class SpriteMaterials
    {
        private static readonly Dictionary<Texture, Material> s_byTexture = new();

        /// <summary>Sets <paramref name="sprite"/> on <paramref name="sr"/> and gives the renderer
        /// the material shared by everything drawing that sprite's texture. A null sprite clears
        /// the renderer and leaves its material alone — there is nothing to key on.</summary>
        public static void Set(SpriteRenderer sr, Sprite sprite)
        {
            if (sr == null) return;
            sr.sprite = sprite;
            if (sprite == null || sprite.texture == null) return;

            if (!s_byTexture.TryGetValue(sprite.texture, out Material mat) || mat == null)
            {
                if (sr.sharedMaterial == null) return;
                mat = new Material(sr.sharedMaterial) { name = "SpriteMat_" + sprite.texture.name };
                s_byTexture[sprite.texture] = mat;
            }
            sr.sharedMaterial = mat;
        }

        /// <summary>As <see cref="Set"/>, but restores <c>sr.size</c> afterwards. Assigning a
        /// sprite RESETS <c>.size</c> to that sprite's native PPU extents, so a sliced renderer
        /// sized before its sprite draws at native world size — 14.6 x 20.5 units for the card
        /// frame, which fills the screen with one card.</summary>
        public static void SetPreservingSize(SpriteRenderer sr, Sprite sprite)
        {
            if (sr == null) return;
            Vector2 size = sr.size;
            Set(sr, sprite);
            sr.size = size;
        }
    }
}
