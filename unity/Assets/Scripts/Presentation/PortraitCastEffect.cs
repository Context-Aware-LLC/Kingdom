using System.Collections;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The cast a hero power shows on its caster's portrait: the class's effect art, revealed from
    /// the bottom of the portrait to the top, then faded (Sam, 2026-10-04).
    ///
    /// <para><b>The reveal and the oval are one mask.</b> The art is a full 2:3 sheet and has to be
    /// cut to the portrait's oval AND wiped upward. Both are the same shape — the part of the oval
    /// below a rising line — so a single <see cref="SpriteMask"/> carries it, and its sprite's
    /// geometry is rebuilt each frame of the rise. No shader, and so nothing for the build
    /// stripper to lose.</para>
    ///
    /// <para>A class with no art in <see cref="SpriteKeyFor"/> has no cast: <see cref="TryBuild"/>
    /// returns null and the plate does nothing.</para>
    /// </summary>
    internal sealed class PortraitCastEffect
    {
        private const float HoldSeconds = 0.10f;
        private const float FadeSeconds = 0.30f;
        /// <summary>Horizontal slices the mask is cut into. The oval's edge is this smooth.</summary>
        private const int Rows = 24;
        /// <summary>The mask sprite is this many pixels square; its geometry is in those pixels.</summary>
        private const int MaskPixels = 4;

        private readonly GameObject _root;
        private readonly SpriteRenderer _art;
        private readonly Sprite _maskSprite;
        private readonly Vector2[] _verts = new Vector2[(Rows + 1) * 2];
        private readonly ushort[] _tris = new ushort[Rows * 6];

        /// <summary>The art each class casts with. A class added here needs a row in
        /// <c>HeroPowerBeatClassifier.TryLookFor</c> too, or nothing ever asks for it.</summary>
        private static string SpriteKeyFor(CardClass heroClass) => heroClass switch
        {
            CardClass.EMBERWARD => "effect_hero_cast_emberward",
            CardClass.TEMPEST   => "effect_hero_cast_tempest",
            _                   => null,
        };

        /// <param name="plate">The hero plate; the effect is built as its child.</param>
        /// <param name="centre">The portrait's centre, in the plate's local space.</param>
        /// <param name="width">The portrait oval's width, in the plate's local units.</param>
        /// <param name="height">The portrait oval's height, in the plate's local units.</param>
        public static PortraitCastEffect TryBuild(Transform plate, CardClass heroClass,
                                                  IUiSpriteProvider sprites, Vector3 centre,
                                                  float width, float height, int sortingOrder)
        {
            string key = SpriteKeyFor(heroClass);
            Sprite art = key != null ? sprites?.Get(key) : null;
            if (art == null) return null;
            return new PortraitCastEffect(plate, art, centre, width, height, sortingOrder);
        }

        private PortraitCastEffect(Transform plate, Sprite art, Vector3 centre,
                                   float width, float height, int sortingOrder)
        {
            _root = new GameObject("CastEffect");
            _root.transform.SetParent(plate, false);
            // A hair nearer the camera than the portrait, which is opaque and writes depth.
            _root.transform.localPosition = centre + new Vector3(0f, 0f, -0.01f);

            var artGo = new GameObject("Art");
            artGo.transform.SetParent(_root.transform, false);
            _art = artGo.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(_art, art);
            _art.sortingOrder = sortingOrder;
            _art.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            // As wide as the portrait, and standing on its bottom edge: the art is taller than the
            // oval, and what overflows is the top, where the flames have thinned out anyway.
            Bounds native = art.bounds;
            float scale = width / native.size.x;
            artGo.transform.localScale = new Vector3(scale, scale, 1f);
            artGo.transform.localPosition = new Vector3(
                -native.center.x * scale,
                -height * 0.5f - native.min.y * scale,
                0f);

            var tex = new Texture2D(MaskPixels, MaskPixels, TextureFormat.RGBA32, false)
            {
                name = "PortraitCastMask",
            };
            var white = new Color32[MaskPixels * MaskPixels];
            for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(white);
            tex.Apply();
            // One sprite unit square, so the mask object's scale IS the portrait's size.
            _maskSprite = Sprite.Create(tex, new Rect(0, 0, MaskPixels, MaskPixels),
                                        new Vector2(0.5f, 0.5f), MaskPixels);

            var maskGo = new GameObject("Mask");
            maskGo.transform.SetParent(_root.transform, false);
            maskGo.transform.localScale = new Vector3(width, height, 1f);
            maskGo.AddComponent<SpriteMask>().sprite = _maskSprite;

            for (int i = 0; i < Rows; i++)
            {
                int v = i * 2, t = i * 6;
                _tris[t + 0] = (ushort)v;       _tris[t + 1] = (ushort)(v + 2); _tris[t + 2] = (ushort)(v + 1);
                _tris[t + 3] = (ushort)(v + 1); _tris[t + 4] = (ushort)(v + 2); _tris[t + 5] = (ushort)(v + 3);
            }

            _root.SetActive(false);
        }

        /// <summary>Wait <paramref name="delaySeconds"/>, rise over <paramref name="riseSeconds"/>,
        /// hold, fade. Run by the plate; restarting it mid-play is safe.</summary>
        public IEnumerator Play(float delaySeconds, float riseSeconds)
        {
            _root.SetActive(false);
            if (delaySeconds > 0f) yield return new WaitForSeconds(delaySeconds);

            _art.color = Color.white;
            Reveal(0f);
            _root.SetActive(true);

            for (float e = 0f; e < riseSeconds; e += Time.deltaTime)
            {
                Reveal(e / riseSeconds);
                yield return null;
            }
            Reveal(1f);

            yield return new WaitForSeconds(HoldSeconds);

            for (float e = 0f; e < FadeSeconds; e += Time.deltaTime)
            {
                _art.color = new Color(1f, 1f, 1f, 1f - e / FadeSeconds);
                yield return null;
            }

            _root.SetActive(false);
        }

        /// <summary>Stop showing anything, now. For a plate being disabled mid-cast.</summary>
        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        /// <summary>Cut the mask to the part of the oval below <paramref name="fraction"/> of its
        /// height: 0 is nothing, 1 is the whole portrait.</summary>
        private void Reveal(float fraction)
        {
            // Never zero: a sprite with no area has no geometry to override.
            float top = Mathf.Clamp(fraction, 0.02f, 1f);
            for (int i = 0; i <= Rows; i++)
            {
                float y = top * i / Rows;                     // 0 at the bottom of the oval, 1 at the top
                float fromCentre = y * 2f - 1f;
                float half = 0.5f * Mathf.Sqrt(Mathf.Max(0f, 1f - fromCentre * fromCentre));
                _verts[i * 2]     = new Vector2((0.5f - half) * MaskPixels, y * MaskPixels);
                _verts[i * 2 + 1] = new Vector2((0.5f + half) * MaskPixels, y * MaskPixels);
            }
            _maskSprite.OverrideGeometry(_verts, _tris);
        }
    }
}
