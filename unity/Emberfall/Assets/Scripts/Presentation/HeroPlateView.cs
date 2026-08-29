using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// World-space hero portrait for one player. The authored ember frame is the face; hero art
    /// is clipped to its transparent oval opening. Created procedurally by
    /// NetworkedGameBootstrap — one per player.
    /// </summary>
    public sealed class HeroPlateView : MonoBehaviour
    {
        private static readonly Color PlateColor   = Color.white;
        private static readonly Color HpFull       = Color.white;
        private static readonly Color HpDamaged    = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color FrostedTint   = new Color(0.40f, 0.60f, 0.95f);
        // Matches CardPresenter.SelectedColor so hero-as-attacker selection reads
        // identically to minion selection.
        private static readonly Color SelectedTint = new Color(1.00f, 0.85f, 0.10f);
        // Valid-target cue: cyan to match CardMaterialBinder's target glow.
        private static readonly Color TargetableTint = new Color(0.25f, 0.90f, 1.00f);

        // hero-frame.png is 1024x1536, so this keeps its native 2:3 aspect. Keep these and
        // ClickableHero's collider in step; together they are one authored figure.
        internal const float PlateW = 1.60f;
        internal const float PlateH = 2.40f;
        private const float PortraitW = 1.16f;
        private const float PortraitH = 1.58f;
        private const string FrameSpriteKey = "hero-frame";
        private const int FrameOrder = 8;
        private const int PortraitOrder = 9;
        private const int TextOrder = 10;
        private const int OvalSegments = 64;

        private PlayerId _playerId;
        private bool _selected;
        private bool _targetable;
        private bool _frosted;

        private Renderer     _plate;
        private Renderer     _portrait;
        private TextMeshPro  _hpLabel;
        private MaterialPropertyBlock _mpb;

        /// <summary>Attach to a new GameObject and call Init once before first OnStateUpdated.
        /// Identified by PlayerId, never by seat/visual index — a networked seat-1 local renders
        /// at visual bottom, so index-based state reads swap the players (found live, 2026-07-19).</summary>
        public void Init(PlayerId playerId, Transform heroAnchor,
                         IArtProvider artProvider, CardClass heroClass,
                         IUiSpriteProvider uiSprites = null)
        {
            _playerId  = playerId;
            _mpb       = new MaterialPropertyBlock();

            transform.SetParent(heroAnchor, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

#if UNITY_EDITOR
            uiSprites ??= new EditorUiSpriteProvider();
#else
            uiSprites ??= new AddressableUiSpriteProvider();
#endif

            // The clipped portrait fills the export's transparent opening. Its geometry sits a
            // hair nearer the camera, so stray near-transparent pixels cannot haze the artwork.
            _plate = MakeFrame(transform, uiSprites?.Get(FrameSpriteKey));
            _portrait = MakePortrait(transform);
            _portrait.gameObject.SetActive(false);
            artProvider?.LoadArt($"hero_{heroClass.ToString().ToLowerInvariant()}", SetPortrait);

            // HP sits inside the portrait's right edge. The two enlarged sockets now own the
            // entire lower skirt, so the old bottom-right position would land on the power frame.
            _hpLabel = MakeLabel(transform, "HP",
                new Vector3(0.48f, 0.12f, -0.02f), 4.7f, HpFull);
        }

        private void SetPortrait(Sprite sprite)
        {
            if (_portrait == null || sprite == null) return;
            _portrait.GetPropertyBlock(_mpb);
            _mpb.SetTexture("_BaseMap", sprite.texture);
            _mpb.SetColor("_BaseColor", Color.white);
            _portrait.SetPropertyBlock(_mpb);
            _portrait.gameObject.SetActive(true);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_hpLabel == null) return;

            PlayerState ps   = state.Players[state.SeatOf(_playerId)];
            HeroInstance hero = ps.Hero;

            _hpLabel.text  = hero.CurrentHealth.ToString();
            _hpLabel.color = hero.CurrentHealth < hero.MaxHealth ? HpDamaged : HpFull;

            _frosted = hero.IsFrosted();
            ApplyPlateTint();
        }

        /// <summary>Hero-as-attacker selection cue — driven by InputRouter, mirrors
        /// CardPresenter.SetSelectionHighlight. Selection outranks the frosted tint.</summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            ApplyPlateTint();
        }

        /// <summary>Valid-target cue while targeting is armed — driven by InputRouter
        /// (targeting-state visibility). Selection outranks it; it outranks frosted.</summary>
        public void SetTargetable(bool targetable)
        {
            _targetable = targetable;
            ApplyPlateTint();
        }

        private void ApplyPlateTint()
        {
            Tint(_plate, _selected   ? SelectedTint
                       : _targetable ? TargetableTint
                       : _frosted     ? FrostedTint
                       : PlateColor);
        }

        // ── Build helpers ───────────────────────────────────────────────────────

        private static SpriteRenderer MakeFrame(Transform parent, Sprite sprite)
        {
            var go = new GameObject("Frame");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = FrameOrder;
            if (sprite != null)
            {
                Vector2 native = sprite.bounds.size;
                go.transform.localScale = new Vector3(PlateW / native.x, PlateH / native.y, 1f);
            }
            else
            {
                Debug.LogWarning($"[HeroPlateView] no UI sprite '{FrameSpriteKey}' — hero frame renders empty.");
            }
            return sr;
        }

        private static Renderer MakePortrait(Transform parent)
        {
            var go = new GameObject("Portrait");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, -0.02f, -0.01f);
            go.transform.localScale = new Vector3(PortraitW, PortraitH, 1f);

            go.AddComponent<MeshFilter>().sharedMesh = OvalMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sortingOrder = PortraitOrder;
            MaterialCatalog.Assign(mr, MaterialCatalog.CardArt);
            return mr;
        }

        private static Mesh _oval;

        private static Mesh OvalMesh()
        {
            if (_oval != null) return _oval;

            var verts = new Vector3[OvalSegments + 1];
            var uvs = new Vector2[OvalSegments + 1];
            var tris = new int[OvalSegments * 3];
            float uSpan = PortraitW / PortraitH; // crop square art instead of stretching a face

            verts[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < OvalSegments; i++)
            {
                float a = 2f * Mathf.PI * i / OvalSegments;
                float x = Mathf.Cos(a) * 0.5f;
                float y = Mathf.Sin(a) * 0.5f;
                verts[i + 1] = new Vector3(x, y, 0f);
                uvs[i + 1] = new Vector2(0.5f + x * uSpan, y + 0.5f);
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = 1 + (i + 1) % OvalSegments;
                tris[i * 3 + 2] = 1 + i;
            }

            _oval = new Mesh { name = "HeroPortraitOval" };
            _oval.SetVertices(verts);
            _oval.SetUVs(0, uvs);
            _oval.SetTriangles(tris, 0);
            _oval.RecalculateBounds();
            return _oval;
        }

        private static TextMeshPro MakeLabel(Transform parent, string goName, Vector3 localPos, float fontSize, Color color)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize  = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = color;
            tmp.rectTransform.sizeDelta = new Vector2(PlateW, fontSize * 0.25f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode     = TextOverflowModes.Ellipsis;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax      = fontSize;
            tmp.fontSizeMin      = fontSize * 0.45f;
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = TextOrder;
            return tmp;
        }

        private void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            if (r is SpriteRenderer sr)
            {
                sr.color = c;
                return;
            }
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            r.SetPropertyBlock(_mpb);
        }
    }
}
