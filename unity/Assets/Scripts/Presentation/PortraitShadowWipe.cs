using System.Collections;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// A Nightveil spell's cast on its caster: black at 80% wiped across the portrait oval, left
    /// to right, then faded as the shadow leaves (Sam, 2026-10-14).
    ///
    /// <para>No art and no mask: the fill is a white square sprite tinted black, and its own
    /// geometry is rebuilt each frame of the wipe to the part of the oval the edge has
    /// reached. <see cref="PortraitCastEffect"/> uses the same oval as a mask, in rows.</para>
    /// </summary>
    internal sealed class PortraitShadowWipe
    {
        private const float Opacity = 0.8f;
        private const float HoldSeconds = 0.10f;
        private const float FadeSeconds = 0.30f;
        /// <summary>Vertical slices the fill is cut into. The oval's edge is this smooth.</summary>
        private const int Columns = 24;
        private const int Pixels = 4;

        private readonly GameObject _root;
        private readonly SpriteRenderer _fill;
        private readonly Sprite _sprite;
        private readonly Vector2[] _verts = new Vector2[(Columns + 1) * 2];
        private readonly ushort[] _tris = new ushort[Columns * 6];

        /// <param name="plate">The hero plate; the wipe is built as its child.</param>
        /// <param name="centre">The portrait's centre, in the plate's local space.</param>
        /// <param name="width">The portrait oval's width, in the plate's local units.</param>
        /// <param name="height">The portrait oval's height, in the plate's local units.</param>
        public PortraitShadowWipe(Transform plate, Vector3 centre, float width, float height,
                                  int sortingOrder)
        {
            _root = new GameObject("ShadowWipe");
            _root.transform.SetParent(plate, false);
            // A hair nearer the camera than the portrait, which is opaque and writes depth.
            _root.transform.localPosition = centre + new Vector3(0f, 0f, -0.01f);
            _root.transform.localScale = new Vector3(width, height, 1f);

            var tex = new Texture2D(Pixels, Pixels, TextureFormat.RGBA32, false) { name = "ShadowWipe" };
            var white = new Color32[Pixels * Pixels];
            for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(white);
            tex.Apply();
            // One sprite unit square, so the root's scale IS the portrait's size.
            _sprite = Sprite.Create(tex, new Rect(0, 0, Pixels, Pixels), new Vector2(0.5f, 0.5f), Pixels);

            _fill = _root.AddComponent<SpriteRenderer>();
            _fill.sprite = _sprite;
            _fill.sortingOrder = sortingOrder;

            for (int i = 0; i < Columns; i++)
            {
                int v = i * 2, t = i * 6;
                _tris[t + 0] = (ushort)v;       _tris[t + 1] = (ushort)(v + 1); _tris[t + 2] = (ushort)(v + 2);
                _tris[t + 3] = (ushort)(v + 1); _tris[t + 4] = (ushort)(v + 3); _tris[t + 5] = (ushort)(v + 2);
            }

            _root.SetActive(false);
        }

        internal static float TotalSeconds(float wipeSeconds) => wipeSeconds + HoldSeconds + FadeSeconds;

        /// <summary>Wait <paramref name="delaySeconds"/>, wipe over <paramref name="wipeSeconds"/>,
        /// hold, fade. Run by the plate; restarting it mid-play is safe.</summary>
        public IEnumerator Play(float delaySeconds, float wipeSeconds)
        {
            _root.SetActive(false);
            if (delaySeconds > 0f) yield return new WaitForSeconds(delaySeconds);

            _fill.color = new Color(0f, 0f, 0f, Opacity);
            Reveal(0f);
            _root.SetActive(true);

            for (float e = 0f; e < wipeSeconds; e += Time.deltaTime)
            {
                Reveal(Mathf.SmoothStep(0f, 1f, e / wipeSeconds));
                yield return null;
            }
            Reveal(1f);

            yield return new WaitForSeconds(HoldSeconds);

            for (float e = 0f; e < FadeSeconds; e += Time.deltaTime)
            {
                _fill.color = new Color(0f, 0f, 0f, Opacity * (1f - e / FadeSeconds));
                yield return null;
            }

            _root.SetActive(false);
        }

        /// <summary>Stop showing anything, now. For a plate being disabled mid-wipe.</summary>
        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        // Cut the fill to the part of the oval left of the advancing edge.
        private void Reveal(float fraction)
        {
            // Never zero: a sprite with no area has no geometry to override.
            float extent = Mathf.Clamp(fraction, 0.02f, 1f);
            for (int i = 0; i <= Columns; i++)
            {
                float x = extent * i / Columns;               // 0 at the left, 1 at the right
                float fromCentre = x * 2f - 1f;
                float half = 0.5f * Mathf.Sqrt(Mathf.Max(0f, 1f - fromCentre * fromCentre));
                _verts[i * 2]     = new Vector2(x * Pixels, (0.5f - half) * Pixels);
                _verts[i * 2 + 1] = new Vector2(x * Pixels, (0.5f + half) * Pixels);
            }
            _sprite.OverrideGeometry(_verts, _tris);
        }
    }
}
