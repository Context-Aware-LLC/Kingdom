using System.Collections.Generic;
using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    // World-space card visual (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1).
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
        [SerializeField] private TextMeshPro bodyLabel;
        [SerializeField] private Renderer cardBackground;
        [SerializeField] private Renderer artRenderer;

        // Frame/rarity layer (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md D2) — sprites resolved by name through _ui.
        [SerializeField] private SpriteRenderer frameRenderer;
        [SerializeField] private SpriteRenderer gemRenderer;
        [SerializeField] private SpriteRenderer[] keywordIcons;
        private IUiSpriteProvider _ui;

        private static readonly Color NormalColor   = new Color(0.22f, 0.22f, 0.32f);
        private static readonly Color BuffColor     = new Color(0.20f, 0.80f, 0.25f);
        private static readonly Color DamageColor   = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color ShieldColor   = new Color(0.95f, 0.88f, 0.15f);
        private static readonly Color FrostedColor   = new Color(0.40f, 0.60f, 0.95f);
        private static readonly Color BulwarkColor    = new Color(0.80f, 0.50f, 0.10f);
        private static readonly Color SelectedColor = new Color(1.00f, 0.85f, 0.10f); // gold
        private const float ExhaustedDim = 0.55f;

        private CardDefinition  _def;
        private ICardLiveState  _live;
        private MaterialPropertyBlock _mpb;
        private MaterialPropertyBlock _artMpb;
        private bool _isSelected;
        private readonly List<Keyword> _kwScratch = new();

        // Called by BoardController.CreatePresenter when building the visual hierarchy at runtime.
        public void Setup(TextMeshPro name, TextMeshPro cost, TextMeshPro attack, TextMeshPro health,
                          TextMeshPro durability, TextMeshPro keywords, Renderer bg,
                          Renderer art = null, TextMeshPro body = null)
        {
            nameLabel      = name;
            costLabel      = cost;
            attackLabel    = attack;
            healthLabel    = health;
            durabilityLabel = durability;
            keywordLabel   = keywords;
            cardBackground = bg;
            artRenderer    = art;
            bodyLabel      = body;
        }

        // Frame/rarity layer wiring (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md D2). Separate from Setup() so the
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
            _artTexture = sprite.texture;
            if (_artMpb == null) _artMpb = new MaterialPropertyBlock();
            artRenderer.GetPropertyBlock(_artMpb);
            _artMpb.SetTexture("_BaseMap", _artTexture);
            _artMpb.SetColor("_BaseColor", Color.white);
            artRenderer.SetPropertyBlock(_artMpb);
            artRenderer.gameObject.SetActive(true);
            ApplyArtFit();
        }

        private Texture _artTexture;

        // Cover-fit the illustration to the frame's art window (mock_card_front.py ImageOps.fit,
        // centering (0.5, 0.35)): crop the overflowing texture axis via _BaseMap_ST instead of
        // letting the quad stretch it. Needs both the texture (SetArt) and the rarity (Bind);
        // called from whichever arrives second.
        private void ApplyArtFit()
        {
            if (artRenderer == null || _artTexture == null || _def == null || _artMpb == null) return;
            Vector4 win = FrameRarityCatalog.Sockets(_def.Type, _def.Rarity).ArtWindow;
            float quadAspect = ((win.z - win.x) * BoardController.CardW)
                             / ((win.w - win.y) * BoardController.CardH);
            float texAspect = (float)_artTexture.width / _artTexture.height;
            float su = 1f, sv = 1f;
            if (texAspect > quadAspect) su = quadAspect / texAspect;
            else                        sv = texAspect / quadAspect;
            // 0.35-from-top vertical centering; V runs bottom-up, so 0.65 of the slack goes below.
            _artMpb.SetVector("_BaseMap_ST", new Vector4(su, sv, (1f - su) * 0.5f, (1f - sv) * 0.65f));
            artRenderer.SetPropertyBlock(_artMpb);
        }

        // Card-back mode for projected hidden cards (dev-docs/architecture/determinism-and-projection.md:23): no definition,
        // no live state — hide everything face-side and show the back sprite if the atlas has one.
        public void SetHidden()
        {
            if (nameLabel != null)       nameLabel.gameObject.SetActive(false);
            if (costLabel != null)       costLabel.gameObject.SetActive(false);
            if (attackLabel != null)     attackLabel.gameObject.SetActive(false);
            if (healthLabel != null)     healthLabel.gameObject.SetActive(false);
            if (durabilityLabel != null) durabilityLabel.gameObject.SetActive(false);
            if (keywordLabel != null)    keywordLabel.gameObject.SetActive(false);
            if (bodyLabel != null)       bodyLabel.gameObject.SetActive(false);
            if (artRenderer != null)     artRenderer.gameObject.SetActive(false);
            if (gemRenderer != null)     gemRenderer.gameObject.SetActive(false);
            if (keywordIcons != null)
                foreach (var icon in keywordIcons)
                    if (icon != null) icon.gameObject.SetActive(false);

            // Back sprite if authored ("card_back" atlas key); otherwise the frame stays empty
            // and the dark body quad reads as the back — acceptable until the asset lands.
            if (frameRenderer != null && _ui != null)
            {
                Sprite back = _ui.Get("card_back");
                if (back != null) SetSpritePreserveSize(frameRenderer, back);
            }

            if (cardBackground != null)
            {
                if (_mpb == null) _mpb = new MaterialPropertyBlock();
                cardBackground.GetPropertyBlock(_mpb);
                _mpb.SetColor("_BaseColor", new Color(0.13f, 0.12f, 0.20f));
                cardBackground.SetPropertyBlock(_mpb);
            }
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
            // The BG tint alone is invisible under the art/frame layers — the rim glow
            // (slightly larger than the card face) is the readable selection cue.
            GetComponent<CardMaterialBinder>()?.SetAffordanceGlow(selected);
        }

        // Toss marker for the mulligan picker (C-G1-2b): a distinct red "X" laid over the card
        // face while it is selected for replacement. Built lazily and kept SEPARATE from the gold
        // selection glow above, so "will be tossed" never reads as "selected to play". In front of
        // the art/frame layers so it stays legible.
        public void SetMulliganMark(bool marked)
        {
            if (marked && _mulliganMark == null) BuildMulliganMark();
            if (_mulliganMark != null) _mulliganMark.SetActive(marked);
        }

        private GameObject _mulliganMark;

        private void BuildMulliganMark()
        {
            _mulliganMark = new GameObject("MulliganMark");
            _mulliganMark.transform.SetParent(transform, false);
            _mulliganMark.transform.localPosition = new Vector3(0f, 0f, -0.1f);
            var label = _mulliganMark.AddComponent<TextMeshPro>();
            _mulliganMark.GetComponent<Renderer>().sortingOrder = 20; // above frame (0) and labels (10)
            label.text            = "X";
            label.fontStyle       = FontStyles.Bold;
            label.fontSize        = 9f;
            label.alignment       = TextAlignmentOptions.Center;
            label.color           = new Color(0.92f, 0.15f, 0.12f);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.rectTransform.sizeDelta = new Vector2(2.4f, 3f);
        }

        private void OnDestroy() => Unbind();

        private void OnChanged(CardChange what)
        {
            if ((what & CardChange.Stats)      != 0) RecolorStats();
            if ((what & CardChange.Keywords)   != 0) RebuildKeywords();
            if ((what & CardChange.Auras)      != 0) ApplyAuras();
            if ((what & CardChange.Affordance) != 0) ApplyAuras();
        }

        private void ApplyAll()
        {
            if (_def == null) return;
            if (nameLabel != null) nameLabel.text = _def.Name;
            if (costLabel != null) costLabel.text = _def.Cost.ToString();
            ApplyFrame();
            RecolorStats();
            RebuildKeywords();
            ApplyBodyText();
            ApplyAuras();
        }

        // Rules text band. Presentation is derived from the immutable card definition,
        // so trigger markers are never carried as duplicate keyword data.
        private void ApplyBodyText()
        {
            if (bodyLabel == null) return;
            if (_def == null) { bodyLabel.gameObject.SetActive(false); return; }

            string generatedText = CardTextGenerator.Generate(_def);
            bodyLabel.text = CardTextStyling.Style(generatedText);
            bodyLabel.gameObject.SetActive(generatedText.Length > 0);
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

        // Data-driven frame/rarity assembly (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md §3 ApplyFrame) — stat-plate
        // visibility keyed by type, frame + digit sockets keyed by type AND rarity. No per-card
        // branching.
        private void ApplyFrame()
        {
            // One painted frame per rarity, except spells which have their own pair (§3.1 +
            // FrameRarityCatalog.SpellFrameRarity). Falls back to the empty-sprite placeholder
            // look if the frame asset is missing.
            Sprite frameSprite = null;
            if (frameRenderer != null && _ui != null)
            {
                frameSprite = _ui.Get(FrameRarityCatalog.FrameSprite(_def.Type, _def.Rarity));
                SetSpritePreserveSize(frameRenderer, frameSprite);
            }
            _hasPaintedFrame = frameSprite != null;
            if (_hasPaintedFrame) ApplySockets();

            if (gemRenderer != null)
            {
                // The painted frame carries the rarity read — the gem was the placeholder cue.
                bool showGem = frameSprite == null;
                gemRenderer.gameObject.SetActive(showGem);
                if (showGem)
                {
                    Sprite gem = _ui?.Get(FrameRarityCatalog.GemSprite(_def.Rarity));
                    SetSpritePreserveSize(gemRenderer, gem);
                    // Tint white when a real gem sprite is present; else tint the blank sprite by rarity.
                    gemRenderer.color = gem != null ? Color.white : FrameRarityCatalog.GemColor(_def.Rarity);
                }
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

        // §3.1 socket table (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/card-rendering.md, Sam-calibrated 2026-07-25): move every card-front
        // element onto this rarity's frame geometry. Fractions are top-left-origin on the frame;
        // local space is center-origin with +y up, so x' = (fx-.5)·W, y' = (.5-fy)·H.
        // TMP world text: fontSize = em height in units × 10.
        private void ApplySockets()
        {
            var s = FrameRarityCatalog.Sockets(_def.Type, _def.Rarity);
            const float W = BoardController.CardW, H = BoardController.CardH;
            const float z = -0.01f;

            PlaceDigit(costLabel,       s.Cost,   s.DigitEm, z);
            PlaceDigit(attackLabel,     s.Attack, s.DigitEm, z);
            PlaceDigit(healthLabel,     s.Health, s.DigitEm, z);
            PlaceDigit(durabilityLabel, s.Health, s.DigitEm, z); // weapons: durability owns the health boss

            if (nameLabel != null)
            {
                // Fixed size, no auto-fit; hard left clamp keeps long names off the cost boss.
                nameLabel.enableAutoSizing = false;
                nameLabel.fontSize = FrameRarityCatalog.NameEm * H * 10f;
                nameLabel.color    = FrameRarityCatalog.NameParchment;
                float cx     = FrameRarityCatalog.NameCenterX * W;
                float clampX = FrameRarityCatalog.NameLeftClamp * W;
                float textW  = nameLabel.GetPreferredValues(_def.Name).x;
                if (cx - textW * 0.5f < clampX) cx = clampX + textW * 0.5f;
                nameLabel.rectTransform.localPosition =
                    new Vector3(cx - W * 0.5f, (0.5f - FrameRarityCatalog.NameCenterY) * H, z);
            }

            if (artRenderer != null)
            {
                Vector4 win = s.ArtWindow;
                artRenderer.transform.localPosition = new Vector3(
                    ((win.x + win.z) * 0.5f - 0.5f) * W,
                    (0.5f - (win.y + win.w) * 0.5f) * H, -0.005f);
                artRenderer.transform.localScale = new Vector3(
                    (win.z - win.x) * W, (win.w - win.y) * H, 1f);
                ApplyArtFit();
            }

            if (bodyLabel != null)
            {
                // Rules band: bold dark ink on the frame's parchment (2026-07-25 mobile-
                // legibility pass — regular-weight slab strokes didn't read at board scale).
                bodyLabel.color = FrameRarityCatalog.BodyInk;
                bodyLabel.fontStyle |= FontStyles.Bold;
                bodyLabel.fontSizeMax = FrameRarityCatalog.BodyEm * H * 10f;
                bodyLabel.fontSizeMin = FrameRarityCatalog.BodyEmFloor * H * 10f;
                // Wrap inside the parchment bed, not the card — the creation-time rect
                // (0.90 CardW) let wrapped lines run onto the frame rails.
                bodyLabel.rectTransform.sizeDelta = new Vector2(0.74f * W, 0.21f * H);
                bodyLabel.rectTransform.localPosition =
                    new Vector3(0f, (0.5f - FrameRarityCatalog.BodyCenterY) * H, z);
            }
        }

        private static void PlaceDigit(TextMeshPro label, Vector2 frac, float em, float z)
        {
            if (label == null) return;
            label.enableAutoSizing = false;
            label.fontSize = em * BoardController.CardH * 10f;
            label.rectTransform.sizeDelta = new Vector2(0.5f, 0.35f);
            label.rectTransform.localPosition = new Vector3(
                (frac.x - 0.5f) * BoardController.CardW,
                (0.5f - frac.y) * BoardController.CardH, z);
        }

        // The equipped weapon is no longer rendered as a card, so nothing pushes live weapon
        // stats through here — HeroSocketsView draws them on the weapon ring's stat bosses,
        // including the buff/damage colouring this used to own. Weapon cards in HAND still get
        // their attack and durability from _def on the Bind/refresh path above.

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

        private bool _hasPaintedFrame;

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

            // On a painted frame the §3.1 face carries keywords as the bold body-text lead
            // (keyword-visual-language decision) — the icon strip and text strip would sit
            // on the parchment band twice. Body text is rebuilt on the same change event.
            if (_hasPaintedFrame)
            {
                if (keywordLabel != null) keywordLabel.gameObject.SetActive(false);
                if (keywordIcons != null)
                    foreach (var icon in keywordIcons)
                        if (icon != null) icon.gameObject.SetActive(false);
                ApplyBodyText();
                return;
            }

            // Preferred: one atlas icon per keyword (§4), laid out left-to-right, capped at the
            // strip's slot count. StatusMarkCatalog owns the registry (plan decision 15, EF-044):
            // null means either intentionally absent (s_noIcon — silent) or unknown keyword
            // (warns in the catalog). A registered address missing from the atlas warns here.
            if (keywordIcons != null && keywordIcons.Length > 0 && _ui != null)
            {
                if (keywordLabel != null) keywordLabel.gameObject.SetActive(false);

                int slot = 0;
                foreach (Keyword kw in _kwScratch)
                {
                    if (slot >= keywordIcons.Length) break;
                    string addr = StatusMarkCatalog.KeywordIconAddress(kw);
                    if (addr == null) continue;  // intentionally absent or unknown (catalog warns)
                    Sprite icon = _ui.Get(addr);
                    if (icon == null)
                    {
                        Debug.LogWarning($"[CardPresenter] keyword icon registered but missing from atlas: {addr}");
                        continue;
                    }
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
            else if ((_live.Auras & AuraSet.Frosted) != 0) c = FrostedColor;
            else if ((_live.Auras & AuraSet.Shield) != 0) c = ShieldColor;
            else if ((_live.Auras & AuraSet.Bulwark)  != 0) c = BulwarkColor;
            else                                           c = NormalColor;

            // Exhausted dim (attack affordance): spent/sick minions on your own turn read
            // as "done" — body and art drop to ~55% (selection overrides, so a clicked
            // exhausted minion still lights up while the toast explains the reject).
            bool dim = !_isSelected && _live.AttackReadiness == AttackReadiness.Exhausted;
            if (dim) c *= ExhaustedDim;

            cardBackground.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            cardBackground.SetPropertyBlock(_mpb);

            if (artRenderer != null && artRenderer.gameObject.activeSelf)
            {
                if (_artMpb == null) _artMpb = new MaterialPropertyBlock();
                artRenderer.GetPropertyBlock(_artMpb);
                _artMpb.SetColor("_BaseColor",
                    dim ? new Color(ExhaustedDim, ExhaustedDim, ExhaustedDim, 1f) : Color.white);
                artRenderer.SetPropertyBlock(_artMpb);
            }
        }
    }
}
