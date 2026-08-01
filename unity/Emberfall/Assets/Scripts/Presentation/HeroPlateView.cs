using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// World-space hero plate for one player (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1 HeroAnchor/HeroPowerAnchor).
    /// Portrait + name + HP read-out at HeroAnchor; hero-power disc (cost + name, greyed when
    /// spent/unaffordable) at HeroPowerAnchor. Created procedurally by GameBootstrap — one per
    /// player, same pattern as ManaTrayView. Purely state-driven; ClickableHeroPower (attached
    /// to the anchor by ProceduralBoardSetup) handles hot-seat input separately.
    /// </summary>
    public sealed class HeroPlateView : MonoBehaviour
    {
        private static readonly Color PlateColor   = new Color(0.16f, 0.16f, 0.24f);
        private static readonly Color HpFull       = Color.white;
        private static readonly Color HpDamaged    = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color FrozenTint   = new Color(0.40f, 0.60f, 0.95f);
        // Matches CardPresenter.SelectedColor so hero-as-attacker selection reads
        // identically to minion selection.
        private static readonly Color SelectedTint = new Color(1.00f, 0.85f, 0.10f);
        // Valid-target cue while a targeted spell / hero power / attack is armed.
        private static readonly Color TargetableTint = new Color(0.25f, 0.65f, 0.30f);

        // hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1: the hero cluster owns 2.9 units, of which the portrait is
        // ~1.8. Square. Keep PlateW/PlateH and ClickableHero's collider in step; they are one
        // figure. The socket column beside it is sized by HeroSocketsView.FrameSize.
        private const float PlateW   = 1.8f;
        private const float PlateH   = 1.8f;

        private PlayerId _playerId;
        private bool _selected;
        private bool _targetable;
        private bool _frozen;

        private Renderer     _plate;
        private Renderer     _portrait;
        private TextMeshPro  _hpLabel;
        private MaterialPropertyBlock _mpb;

        /// <summary>Attach to a new GameObject and call Init once before first OnStateUpdated.
        /// Identified by PlayerId, never by seat/visual index — a networked seat-1 local renders
        /// at visual bottom, so index-based state reads swap the players (found live, 2026-07-19).</summary>
        public void Init(PlayerId playerId, Transform heroAnchor,
                         IArtProvider artProvider, CardClass heroClass)
        {
            _playerId  = playerId;
            _mpb       = new MaterialPropertyBlock();

            transform.SetParent(heroAnchor, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            // Plate backing quad — the hero's board presence.
            _plate = MakeQuad(transform, "Plate", Vector3.zero, new Vector3(PlateW, PlateH, 1f));
            MaterialCatalog.Assign(_plate, MaterialCatalog.Plate);
            Tint(_plate, PlateColor);

            // Portrait quad — front of the plate; art resolves by class artId
            // ("art/hero_emberward" → art_hero_emberward.png, dev-docs/specs/card-generation-prompt.md:18).
            _portrait = MakeQuad(transform, "Portrait",
                new Vector3(0f, 0.05f, -0.005f), new Vector3(PlateW * 0.90f, PlateH * 0.78f, 1f));
            MaterialCatalog.Assign(_portrait, MaterialCatalog.CardArt);
            _portrait.gameObject.SetActive(false);
            artProvider?.LoadArt($"art/hero_{heroClass.ToString().ToLowerInvariant()}", SetPortrait);

            // Hero name — above the portrait.
            // Font sizes track the plate: they were set against the old 1.15 plate, so they
            // scale by the same 1.565 the plate grew by, or the text reads small on it.
            var nameLabel = MakeLabel(transform, "Name", new Vector3(0f, PlateH * 0.44f, -0.01f), 2.3f, Color.white);
            nameLabel.text = HeroName(heroClass);

            // HP — bottom-right gem position, the number that must be visible all match.
            _hpLabel = MakeLabel(transform, "HP",
                new Vector3(PlateW * 0.38f, -PlateH * 0.42f, -0.01f), 4.7f, HpFull);

            // The hero-power disc used to be built here as a flat tinted quad with a
            // "(2) Quicken" label. HeroSocketsView owns that anchor now and draws a forged
            // ring with the power's icon in its socket, which is why this no longer needs
            // the power anchor or the card database.
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

            _frozen = hero.IsFrozen;
            ApplyPlateTint();
        }

        /// <summary>Hero-as-attacker selection cue — driven by InputRouter, mirrors
        /// CardPresenter.SetSelectionHighlight. Selection outranks the frozen tint.</summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            ApplyPlateTint();
        }

        /// <summary>Valid-target cue while targeting is armed — driven by InputRouter
        /// (targeting-state visibility). Selection outranks it; it outranks frozen.</summary>
        public void SetTargetable(bool targetable)
        {
            _targetable = targetable;
            ApplyPlateTint();
        }

        private void ApplyPlateTint()
        {
            Tint(_plate, _selected   ? SelectedTint
                       : _targetable ? TargetableTint
                       : _frozen     ? FrozenTint
                       : PlateColor);
        }

        // ── Build helpers (same primitives-only approach as BoardController) ────────

        private static Renderer MakeQuad(Transform parent, string goName, Vector3 localPos, Vector3 localScale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = goName;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale    = localScale;
            Destroy(go.GetComponent<MeshCollider>());
            return go.GetComponent<Renderer>();
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
            return tmp;
        }

        private void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            r.SetPropertyBlock(_mpb);
        }

        // Display names per hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/heroes.md — classes are the M1 pair + the two post-M1 heroes.
        private static string HeroName(CardClass c) => c switch
        {
            CardClass.EMBERWARD  => "Pyra, Emberward",
            CardClass.TIDECALLER => "Maren, Tidecaller",
            CardClass.GRAVEKEEP  => "Sorrow, Gravekeep",
            CardClass.VERDANT    => "Thalen, Verdant",
            _                    => c.ToString(),
        };
    }
}
