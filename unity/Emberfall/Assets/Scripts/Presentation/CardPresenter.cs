using System.Collections.Generic;
using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    // World-space card visual (board-and-vfx.md §1, interfaces.md §5.1).
    // Subscribes to ICardLiveState.Changed; never polls per-frame.
    // For Phase C step 2: primitive Unity visuals only — no shaders yet.
    // Setup(…) is called by BoardController when creating presenters at runtime.
    public sealed class CardPresenter : MonoBehaviour, ICardPresenter
    {
        // Populated via Setup() (runtime) or serialized refs in a prefab (editor).
        [SerializeField] private TextMeshPro nameLabel;
        [SerializeField] private TextMeshPro costLabel;
        [SerializeField] private TextMeshPro attackLabel;
        [SerializeField] private TextMeshPro healthLabel;
        [SerializeField] private TextMeshPro durabilityLabel;
        [SerializeField] private TextMeshPro keywordLabel;
        [SerializeField] private Renderer cardBackground;
        [SerializeField] private Renderer artRenderer;

        // Frame/rarity layer (frame-rarity-system.md D2) — sprites resolved by name through _ui.
        [SerializeField] private SpriteRenderer frameRenderer;
        [SerializeField] private SpriteRenderer gemRenderer;
        [SerializeField] private SpriteRenderer[] keywordIcons;
        private IUiSpriteProvider _ui;

        private static readonly Color NormalColor   = new Color(0.22f, 0.22f, 0.32f);
        private static readonly Color BuffColor     = new Color(0.20f, 0.80f, 0.25f);
        private static readonly Color DamageColor   = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color ShieldColor   = new Color(0.95f, 0.88f, 0.15f);
        private static readonly Color FrozenColor   = new Color(0.40f, 0.60f, 0.95f);
        private static readonly Color TauntColor    = new Color(0.80f, 0.50f, 0.10f);
        private static readonly Color SelectedColor = new Color(1.00f, 0.85f, 0.10f); // gold

        private CardDefinition  _def;
        private ICardLiveState  _live;
        private MaterialPropertyBlock _mpb;
        private MaterialPropertyBlock _artMpb;
        private bool _isSelected;
        private readonly List<Keyword> _kwScratch = new();

        // Called by BoardController.CreatePresenter when building the visual hierarchy at runtime.
        public void Setup(TextMeshPro name, TextMeshPro cost, TextMeshPro attack, TextMeshPro health,
                          TextMeshPro durability, TextMeshPro keywords, Renderer bg,
                          Renderer art = null)
        {
            nameLabel      = name;
            costLabel      = cost;
            attackLabel    = attack;
            healthLabel    = health;
            durabilityLabel = durability;
            keywordLabel   = keywords;
            cardBackground = bg;
            artRenderer    = art;
        }

        // Frame/rarity layer wiring (frame-rarity-system.md D2). Separate from Setup() so the
        // sprite composition is optional — a card still renders (art + labels) without it.
        public void SetupFrameLayer(SpriteRenderer frame, SpriteRenderer gem,
                                    SpriteRenderer[] icons, IUiSpriteProvider ui)
        {
            frameRenderer = frame;
            gemRenderer   = gem;
            keywordIcons  = icons;
            _ui           = ui;
        }

        // Called by BoardController once the IArtProvider resolves this card's illustration.
        // Null sprite (art not generated yet) keeps the art quad hidden — placeholder look.
        public void SetArt(Sprite sprite)
        {
            if (artRenderer == null) return;
            if (sprite == null)
            {
                artRenderer.gameObject.SetActive(false);
                return;
            }
            if (_artMpb == null) _artMpb = new MaterialPropertyBlock();
            artRenderer.GetPropertyBlock(_artMpb);
            _artMpb.SetTexture("_BaseMap", sprite.texture);
            _artMpb.SetColor("_BaseColor", Color.white);
            artRenderer.SetPropertyBlock(_artMpb);
            artRenderer.gameObject.SetActive(true);
        }

        public void Bind(CardDefinition staticDef, ICardLiveState liveState)
        {
            _def  = staticDef;
            _live = liveState;
            _live.Changed += OnChanged;
            ApplyAll();
        }

        public void Unbind()
        {
            if (_live == null) return;
            _live.Changed -= OnChanged;
            _live = null;
            _def  = null;
        }

        // Called by InputRouter to highlight this card as the active selection.
        public void SetSelectionHighlight(bool selected)
        {
            _isSelected = selected;
            ApplyAuras();
        }

        private void OnDestroy() => Unbind();

        private void OnChanged(CardChange what)
        {
            if ((what & CardChange.Stats)    != 0) RecolorStats();
            if ((what & CardChange.Keywords) != 0) RebuildKeywords();
            if ((what & CardChange.Auras)    != 0) ApplyAuras();
        }

        private void ApplyAll()
        {
            if (_def == null) return;
            if (nameLabel != null) nameLabel.text = _def.Name;
            if (costLabel != null) costLabel.text = _def.Cost.ToString();
            ApplyFrame();
            RecolorStats();
            RebuildKeywords();
            ApplyAuras();
        }

        // Assigning a sprite to a Sliced-mode SpriteRenderer resets its .size to the sprite's
        // natural PPU dimensions — clobbering the world-space size BoardController set at build
        // time (a 1092px frame @ 100 PPU became a 10.9-unit-wide card). Preserve it across the swap.
        private static void SetSpritePreserveSize(SpriteRenderer sr, Sprite sprite)
        {
            Vector2 size = sr.size;
            sr.sprite = sprite;
            sr.size = size;
        }

        // Data-driven frame/rarity assembly (frame-rarity-system.md §3 ApplyFrame) — stat-plate
        // visibility keyed by type, gem tint keyed by rarity. No per-card branching.
        private void ApplyFrame()
        {
            // Templated frame + gem sprite swap by type+rarity (§3 ApplyFrame). Falls back to a
            // gem color tint if the atlas sprite is missing (placeholders not generated yet).
            if (frameRenderer != null && _ui != null)
                SetSpritePreserveSize(frameRenderer, _ui.Get(FrameRarityCatalog.FrameSprite(_def.Type, _def.Rarity)));

            if (gemRenderer != null)
            {
                Sprite gem = _ui?.Get(FrameRarityCatalog.GemSprite(_def.Rarity));
                SetSpritePreserveSize(gemRenderer, gem);
                // Tint white when a real gem sprite is present; else tint the blank sprite by rarity.
                gemRenderer.color = gem != null ? Color.white : FrameRarityCatalog.GemColor(_def.Rarity);
            }

            bool showAttack     = FrameRarityCatalog.ShowAttack(_def.Type);
            bool showHealth     = FrameRarityCatalog.ShowHealth(_def.Type);
            bool showDurability = FrameRarityCatalog.ShowDurability(_def.Type);

            if (attackLabel != null) attackLabel.gameObject.SetActive(showAttack);
            if (healthLabel != null) healthLabel.gameObject.SetActive(showHealth);
            if (durabilityLabel != null)
            {
                durabilityLabel.gameObject.SetActive(showDurability);
                if (showDurability) durabilityLabel.text = (_def.Durability ?? 0).ToString();
            }

            // Weapon attack is static while in hand (no live minion stats to track); set it directly.
            if (showAttack && attackLabel != null && _def.Type == CardType.WEAPON)
                attackLabel.text = (_def.Attack ?? 0).ToString();
        }

        private void RecolorStats()
        {
            if (_live == null || _def.Type != CardType.MINION) return;

            // In hand the core hasn't materialized instance stats yet (SummonMinion sets them;
            // MaxHealth ≥ 1 for any live minion) — show the printed definition stats instead of
            // live zeros, which read as "0/0 damaged" red.
            if (_live.MaxHealth == 0)
            {
                if (attackLabel != null) { attackLabel.text = (_def.Attack ?? 0).ToString(); attackLabel.color = Color.white; }
                if (healthLabel != null) { healthLabel.text = (_def.Health ?? 0).ToString(); healthLabel.color = Color.white; }
                return;
            }

            if (attackLabel != null)
            {
                int baseAtk = _def?.Attack ?? _live.Attack;
                attackLabel.text  = _live.Attack.ToString();
                attackLabel.color = _live.Attack > baseAtk ? BuffColor
                                  : _live.Attack < baseAtk ? DamageColor
                                  : Color.white;
            }

            if (healthLabel != null)
            {
                int baseHp = _def?.Health ?? _live.MaxHealth;
                healthLabel.text  = _live.Health.ToString();
                healthLabel.color = _live.Health < _live.MaxHealth ? DamageColor
                                  : _live.MaxHealth > baseHp      ? BuffColor
                                  : Color.white;
            }
        }

        private void RebuildKeywords()
        {
            if (_def == null) return;

            // Printed keywords (static definition) are the base — a card shows its keywords in hand,
            // where the runtime instance carries none. Live-granted keywords (board) merge on top.
            _kwScratch.Clear();
            foreach (Keyword k in _def.Keywords) _kwScratch.Add(k);
            if (_live != null)
                foreach (Keyword k in _live.Keywords)
                    if (!_kwScratch.Contains(k)) _kwScratch.Add(k);

            // Preferred: one atlas icon per keyword (§4), laid out left-to-right, capped at the
            // strip's slot count. SILENCE is an effect, not a face keyword, so KeywordIcon() skips it.
            if (keywordIcons != null && keywordIcons.Length > 0 && _ui != null)
            {
                if (keywordLabel != null) keywordLabel.gameObject.SetActive(false);

                int slot = 0;
                foreach (Keyword kw in _kwScratch)
                {
                    if (slot >= keywordIcons.Length) break;
                    Sprite icon = _ui.Get(FrameRarityCatalog.KeywordIcon(kw));
                    if (icon == null) continue;
                    SetSpritePreserveSize(keywordIcons[slot], icon);
                    keywordIcons[slot].gameObject.SetActive(true);
                    slot++;
                }
                for (int i = slot; i < keywordIcons.Length; i++)
                    keywordIcons[i].gameObject.SetActive(false);
                return;
            }

            // Fallback: text keyword strip (icons not wired / atlas absent).
            if (keywordLabel == null) return;
            var sb = new StringBuilder();
            foreach (Keyword kw in _kwScratch)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(kw.ToString());
            }
            keywordLabel.text = sb.ToString();
        }

        private void ApplyAuras()
        {
            if (_live == null || cardBackground == null) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            Color c;
            if      (_isSelected)                          c = SelectedColor;
            else if ((_live.Auras & AuraSet.Frozen) != 0) c = FrozenColor;
            else if ((_live.Auras & AuraSet.Shield) != 0) c = ShieldColor;
            else if ((_live.Auras & AuraSet.Taunt)  != 0) c = TauntColor;
            else                                           c = NormalColor;

            cardBackground.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            cardBackground.SetPropertyBlock(_mpb);
        }
    }
}
