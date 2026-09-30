using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The menu's button chrome on a world-space board button: the same two sprites
    /// <c>theme.uss</c> puts on every <c>.unity-button</c> (slate by default, ember for the primary
    /// action), drawn as a 9-sliced <see cref="SpriteRenderer"/> so the board's buttons read as the
    /// same family as the menus' (Sam, 2026-09-30: "we need chrome on the in game buttons").
    ///
    /// <para><b>Scaled so the caps keep the art's proportions.</b> A sliced sprite's borders are
    /// fixed in world units (64 px at 100 PPU = 0.64 units a side), which is taller than a board
    /// button. So the renderer is scaled until the sprite's native height IS the button height,
    /// and only the middle stretches sideways — the art looks exactly as authored, just wider.
    /// The borders come from the sprites' import settings, which the USS slices match.</para>
    /// </summary>
    internal static class BoardButtonChrome
    {
        public const string PrimaryKey   = "ui_button_primary_ember";
        public const string SecondaryKey = "ui_button_secondary_slate";

        /// <summary>Label colours from theme.uss: `.emb-primary`'s ink on ember, a cool white on slate.</summary>
        public static readonly Color PrimaryInk   = new Color(250f / 255f, 230f / 255f, 193f / 255f);
        public static readonly Color SecondaryInk = new Color(0.93f, 0.94f, 0.96f);

        private static IUiSpriteProvider s_sprites;

        private static IUiSpriteProvider Sprites
        {
            get
            {
#if UNITY_EDITOR
                return s_sprites ??= new EditorUiSpriteProvider();
#else
                return s_sprites ??= new AddressableUiSpriteProvider();
#endif
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_sprites = null;

        /// <summary>A chrome plate of <paramref name="size"/> world units under
        /// <paramref name="parent"/>, centred, at <paramref name="sortingOrder"/>. Put the label
        /// one order above it.</summary>
        public static SpriteRenderer Create(Transform parent, Vector2 size, bool primary, int sortingOrder)
        {
            var go = new GameObject("Chrome");
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = sortingOrder;
            Apply(sr, size, primary);
            if (sr.sprite == null)
                Debug.LogWarning("[BoardButtonChrome] button chrome sprite missing — button draws bare.");
            return sr;
        }

        /// <summary>Swap between ember and slate (the end-turn button changes with the turn).</summary>
        public static void Apply(SpriteRenderer sr, Vector2 size, bool primary)
        {
            if (sr == null) return;
            Sprite sprite = Sprites.Get(primary ? PrimaryKey : SecondaryKey);
            SpriteMaterials.Set(sr, sprite);
            if (sprite == null) return;

            // Assigning a sprite resets .size, so size after Set.
            float nativeH = sprite.bounds.size.y;
            float s = nativeH > 0f ? size.y / nativeH : 1f;
            sr.transform.localScale = new Vector3(s, s, 1f);
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = new Vector2(size.x / s, size.y / s);
        }
    }
}
