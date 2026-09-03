using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Turns arbitrary 2:3 pack artwork into a recognisable sealed booster wrapper and hands the
    /// result to UI Toolkit.
    ///
    /// <para><b>Why this bakes instead of assigning a material to the element.</b> UI Toolkit's
    /// background image accepts a Sprite, Texture2D or RenderTexture, but not a caller-supplied
    /// Material. The wrapper therefore renders once through <c>SG_PackWrapper</c>, caches that
    /// RenderTexture by source sprite, and assigns the finished image. It is still one reusable
    /// shader; it simply runs when art enters the UI rather than on every UI repaint.</para>
    ///
    /// <para><b>The source art is never altered.</b> Its full frame is compressed into the printed
    /// body while the shader supplies the top and bottom heat seals, crimp ridges, serrated cut,
    /// side folds and foil response. A missing shader or unsupported RenderTexture falls back to
    /// the original Sprite, so packaging art never disappears because the treatment failed.</para>
    ///
    /// <para>The cache is intentionally process-wide. The store, vault, opening beat and main menu
    /// are different scenes displaying the same small set of art; baking again at every scene seam
    /// would pay the GPU allocation repeatedly. Subsystem registration clears stale references when
    /// Enter Play Mode skips a domain reload.</para>
    /// </summary>
    public static class PackWrapperBaker
    {
        // 256 x 384 matches the authored 2:3 art and is at least native size on every current pack
        // surface, including the 260dp tear beat. The torn outputs retain that pixel density: their
        // heights are the 41.7% / 66.7% pieces PackTearAnimator lays over one another.
        private const int BakeWidth = 256;
        private const int BakeHeight = 384;
        private const int TornTopHeight = 160;
        private const int TornBottomHeight = 256;
        private const string ShaderResource = "SG_PackWrapper";

        private static readonly int SourceRectId = Shader.PropertyToID("_SourceRect");
        private static readonly int PieceModeId = Shader.PropertyToID("_PieceMode");
        private static readonly Dictionary<Sprite, BakedPack> Cache = new();

        private static Material s_material;
        private static bool s_reportedUnavailable;

        private enum Piece
        {
            Whole = 0,
            TornTop = 1,
            TornBottom = 2
        }

        private sealed class BakedPack
        {
            public RenderTexture Whole { get; set; }
            public RenderTexture TornTop { get; set; }
            public RenderTexture TornBottom { get; set; }

            public RenderTexture Get(Piece piece)
            {
                return piece switch
                {
                    Piece.TornTop => TornTop,
                    Piece.TornBottom => TornBottom,
                    _ => Whole
                };
            }

            public void Set(Piece piece, RenderTexture texture)
            {
                switch (piece)
                {
                    case Piece.TornTop: TornTop = texture; break;
                    case Piece.TornBottom: TornBottom = texture; break;
                    default: Whole = texture; break;
                }
            }
        }

        /// <summary>
        /// Paint <paramref name="target"/> with the wrapped form of <paramref name="source"/>.
        /// Returns true when the shader treatment was applied; false means the original Sprite was
        /// used as the safe fallback (or either argument was absent).
        /// </summary>
        public static bool ApplyTo(VisualElement target, Sprite source)
        {
            if (target == null || source == null) return false;

            if (!TryBake(source, Piece.Whole, out RenderTexture wrapped))
            {
                target.style.backgroundImage = new StyleBackground(source);
                return false;
            }

            target.style.backgroundImage =
                new StyleBackground(Background.FromRenderTexture(wrapped));
            // Cropping the outer 10% would crop away the very crimps that make this read as a
            // wrapper. Make the whole silhouette visible regardless of a consumer's older USS.
            target.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            return true;
        }

        /// <summary>
        /// Paint two independently animatable elements with complementary torn views of the same
        /// wrapper. The top owns the top heat crimp, the bottom owns the bottom crimp, and both are
        /// transparent away from one shared irregular tear line. No partial assignment occurs when
        /// either bake fails, so an opening controller can retain its existing placeholder safely.
        /// </summary>
        public static bool ApplyTornHalves(
            VisualElement top,
            VisualElement bottom,
            Sprite source)
        {
            if (top == null || bottom == null || source == null) return false;
            if (!TryBake(source, Piece.TornTop, out RenderTexture tornTop)) return false;
            if (!TryBake(source, Piece.TornBottom, out RenderTexture tornBottom)) return false;

            top.style.backgroundImage =
                new StyleBackground(Background.FromRenderTexture(tornTop));
            bottom.style.backgroundImage =
                new StyleBackground(Background.FromRenderTexture(tornBottom));
            top.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            bottom.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            return true;
        }

        private static bool TryBake(Sprite source, Piece piece, out RenderTexture wrapped)
        {
            wrapped = null;
            if (source == null || source.texture == null)
            {
                ReportUnavailable("the source sprite has no texture");
                return false;
            }

            if (Cache.TryGetValue(source, out BakedPack cached))
            {
                if (cached.Get(piece) != null)
                {
                    wrapped = cached.Get(piece);
                    return true;
                }
            }

            cached ??= new BakedPack();

            Material material = Material();
            if (material == null) return false;

            int height = piece switch
            {
                Piece.TornTop => TornTopHeight,
                Piece.TornBottom => TornBottomHeight,
                _ => BakeHeight
            };
            var texture = new RenderTexture(
                BakeWidth,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default)
            {
                name = source.name + "_PackWrapper_" + piece,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };

            if (!texture.Create())
            {
                Release(texture);
                ReportUnavailable($"RenderTexture.Create failed at {BakeWidth}x{height}");
                return false;
            }

            Rect rect = source.textureRect;
            Texture2D sourceTexture = source.texture;
            material.SetVector(SourceRectId, new Vector4(
                rect.x / sourceTexture.width,
                rect.y / sourceTexture.height,
                rect.width / sourceTexture.width,
                rect.height / sourceTexture.height));
            material.SetFloat(PieceModeId, (float)piece);

            Graphics.Blit(sourceTexture, texture, material, 0);
            cached.Set(piece, texture);
            Cache[source] = cached;
            wrapped = texture;
            return true;
        }

        private static Material Material()
        {
            if (s_material != null) return s_material;

            // A Resources reference is deliberate. A shader reached only through Shader.Find is
            // invisible to the build stripper and can work in-editor while vanishing on device.
            Shader shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null)
            {
                ReportUnavailable($"Resources/{ShaderResource}.shader did not load");
                return null;
            }

            s_material = new Material(shader)
            {
                name = "PackWrapperBaker",
                hideFlags = HideFlags.HideAndDontSave
            };
            return s_material;
        }

        private static void ReportUnavailable(string reason)
        {
            if (s_reportedUnavailable) return;
            s_reportedUnavailable = true;
            Debug.LogWarning($"[PackWrapper] {reason}; displaying the original pack art.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            foreach (BakedPack pack in Cache.Values)
                Release(pack);
            Cache.Clear();

            DestroyObject(s_material);
            s_material = null;
            s_reportedUnavailable = false;
        }

        private static void Release(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            DestroyObject(texture);
        }

        private static void Release(BakedPack pack)
        {
            if (pack == null) return;
            Release(pack.Whole);
            Release(pack.TornTop);
            Release(pack.TornBottom);
        }

        private static void DestroyObject(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }
    }
}
