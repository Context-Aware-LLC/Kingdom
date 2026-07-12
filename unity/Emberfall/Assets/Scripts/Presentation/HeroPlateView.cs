using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// World-space hero plate for one player (board-and-vfx.md §1 HeroAnchor/HeroPowerAnchor).
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
        private static readonly Color PowerReady   = new Color(0.30f, 0.55f, 0.85f);
        private static readonly Color PowerSpent   = new Color(0.22f, 0.22f, 0.30f);

        private const float PlateW = 1.15f;
        private const float PlateH = 1.15f;

        private int _seatIndex;
        private ICardDatabase _db;

        private Renderer     _plate;
        private Renderer     _portrait;
        private TextMeshPro  _hpLabel;
        private Renderer     _powerDisc;
        private TextMeshPro  _powerLabel;
        private MaterialPropertyBlock _mpb;

        /// <summary>Attach to a new GameObject and call Init once before first OnStateUpdated.</summary>
        public void Init(int seatIndex, Transform heroAnchor, Transform powerAnchor,
                         ICardDatabase db, IArtProvider artProvider, CardClass heroClass)
        {
            _seatIndex = seatIndex;
            _db        = db;
            _mpb       = new MaterialPropertyBlock();

            transform.SetParent(heroAnchor, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            // Plate backing quad — the hero's board presence.
            _plate = MakeQuad(transform, "Plate", Vector3.zero, new Vector3(PlateW, PlateH, 1f));
            Tint(_plate, PlateColor);

            // Portrait quad — front of the plate; art resolves by class artId
            // ("art/hero_emberward" → art_hero_emberward.png, pipeline.md §7 naming).
            _portrait = MakeQuad(transform, "Portrait",
                new Vector3(0f, 0.05f, -0.005f), new Vector3(PlateW * 0.90f, PlateH * 0.78f, 1f));
            _portrait.gameObject.SetActive(false);
            artProvider?.LoadArt($"art/hero_{heroClass.ToString().ToLowerInvariant()}", SetPortrait);

            // Hero name — above the portrait.
            var nameLabel = MakeLabel(transform, "Name", new Vector3(0f, PlateH * 0.44f, -0.01f), 1.5f, Color.white);
            nameLabel.text = HeroName(heroClass);

            // HP — bottom-right gem position, the number that must be visible all match.
            _hpLabel = MakeLabel(transform, "HP",
                new Vector3(PlateW * 0.38f, -PlateH * 0.42f, -0.01f), 3.0f, HpFull);

            // Hero-power disc — its own anchor so ClickableHeroPower's collider stays clean.
            if (powerAnchor != null)
            {
                _powerDisc = MakeQuad(powerAnchor, "PowerDisc", Vector3.zero, new Vector3(0.62f, 0.62f, 1f));
                _powerLabel = MakeLabel(powerAnchor, "PowerLabel", new Vector3(0f, 0f, -0.01f), 0.9f, Color.white);
                _powerLabel.rectTransform.sizeDelta = new Vector2(0.60f, 0.55f);
            }
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

            PlayerState ps   = state.Players[_seatIndex];
            HeroInstance hero = ps.Hero;

            _hpLabel.text  = hero.CurrentHealth.ToString();
            _hpLabel.color = hero.CurrentHealth < hero.MaxHealth ? HpDamaged : HpFull;

            Tint(_plate, hero.IsFrozen ? FrozenTint : PlateColor);

            if (_powerDisc != null)
            {
                CardDefinition powerDef = _db?.Get(hero.HeroPowerId);
                int  cost   = powerDef?.Cost ?? 2;
                bool usable = !ps.HeroPowerUsedThisTurn
                              && ps.Mana.Available >= cost
                              && state.ActivePlayer == ps.Id
                              && state.Phase == TurnPhase.Main;
                Tint(_powerDisc, usable ? PowerReady : PowerSpent);
                if (_powerLabel != null)
                    _powerLabel.text = $"({cost}) {powerDef?.Name ?? "Power"}";
            }
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

        // Display names per heroes.md — classes are the M1 pair + the two post-M1 heroes.
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
