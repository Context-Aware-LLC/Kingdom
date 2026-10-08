using System.Collections.Generic;
using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Emberfall.Presentation
{
    // World-space card visual (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1).
    // Subscribes to ICardLiveState.Changed; never polls per-frame.
    // For Phase C step 2: primitive Unity visuals only — no shaders yet.
    // Setup(…) is called by BoardController when creating presenters at runtime.
    public sealed class CardPresenter : MonoBehaviour, ICardPresenter, IPointerHoverTarget
    {
        // Populated via Setup() (runtime) or serialized refs in a prefab (editor).
        [SerializeField] private TextMeshPro nameLabel;
        [SerializeField] private TextMeshPro costLabel;
        [SerializeField] private TextMeshPro attackLabel;
        [SerializeField] private TextMeshPro healthLabel;
        [SerializeField] private TextMeshPro durabilityLabel;
        [SerializeField] private TextMeshPro keywordLabel;
        [SerializeField] private TextMeshPro bodyLabel;
        [SerializeField] private GameObject tribeBanner;
        [SerializeField] private TextMeshPro tribeLabel;
        [SerializeField] private SpriteRenderer tribePanelRenderer;
        [SerializeField] private Renderer cardBackground;
        [SerializeField] private Renderer artRenderer;

        // Frame/rarity layer (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md D2) — sprites resolved by name through _ui.
        [SerializeField] private SpriteRenderer frameRenderer;
        [SerializeField] private SpriteRenderer gemRenderer;
        // The three digit sockets and the gem bezel are composited over the frame rather than
        // painted into it (2026-08-19) — that is what lets one frame serve every card type, since
        // a spell just leaves the two stat sockets undrawn.
        [SerializeField] private SpriteRenderer costSocketRenderer;
        [SerializeField] private SpriteRenderer attackSocketRenderer;
        [SerializeField] private SpriteRenderer powerSwordRenderer;
        [SerializeField] private SpriteRenderer healthSocketRenderer;
        [SerializeField] private SpriteRenderer gemSocketRenderer;
        private IUiSpriteProvider _ui;

        // Tooltip banner (top-of-card, on hover) — see BoardController.CreatePresenter for layout.
        [SerializeField] private GameObject tooltipBanner;
        [SerializeField] private TextMeshPro tooltipLabel;

        private static readonly Color NormalColor   = new Color(0.22f, 0.22f, 0.32f);
        private static readonly Color BuffColor     = HealthInk.Buffed;
        private static readonly Color DamageColor   = new Color(0.90f, 0.20f, 0.20f);
        private static readonly Color ShieldColor   = new Color(0.95f, 0.88f, 0.15f);
        private static readonly Color FrostedColor   = new Color(0.40f, 0.60f, 0.95f);
        private static readonly Color BulwarkColor    = new Color(0.80f, 0.50f, 0.10f);
        private static readonly Color SelectedColor = new Color(1.00f, 0.85f, 0.10f); // gold
        private const float ExhaustedDim = 0.55f;

        private CardDefinition  _def;
        private ICardLiveState  _live;
        private ICardDatabase _cardDatabase;
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
        // The SpriteRenderer[] icons parameter was removed 2026-09-04 with the keyword-icon
        // feature. It is a positional signature change with one caller (BoardController).
        public void SetupFrameLayer(SpriteRenderer frame, SpriteRenderer gem,
                                    IUiSpriteProvider ui,
                                    SpriteRenderer costSocket = null, SpriteRenderer attackSocket = null,
                                    SpriteRenderer healthSocket = null, SpriteRenderer gemSocket = null,
                                    SpriteRenderer powerSword = null)
        {
            frameRenderer        = frame;
            gemRenderer          = gem;
            _ui                  = ui;
            costSocketRenderer   = costSocket;
            attackSocketRenderer = attackSocket;
            powerSwordRenderer   = powerSword;
            healthSocketRenderer = healthSocket;
            gemSocketRenderer    = gemSocket;
        }

        internal void SetupTribe(GameObject banner, TextMeshPro label, SpriteRenderer panel)
        {
            tribeBanner = banner;
            tribeLabel = label;
            tribePanelRenderer = panel;
        }

        // Wired by BoardController.CreatePresenter; banner starts inactive and is toggled by
        // OnPointerEnter/OnPointerExit below, only when the bound card carries a non-empty Tooltip.
        internal void SetupTooltip(GameObject banner, TextMeshPro label)
        {
            tooltipBanner = banner;
            tooltipLabel  = label;
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
            Vector4 win = FrameRarityCatalog.ArtWindow;
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
            if (tribeBanner != null)     tribeBanner.SetActive(false);
            if (artRenderer != null)     artRenderer.gameObject.SetActive(false);
            if (gemRenderer != null)     gemRenderer.gameObject.SetActive(false);
            if (tooltipBanner != null)   tooltipBanner.SetActive(false);

            // Back sprite if authored ("card_back" atlas key); otherwise the frame stays empty
            // and the dark body quad reads as the back — acceptable until the asset lands.
            if (frameRenderer != null && _ui != null)
            {
                Sprite back = _ui.Get("card_back");
                if (back != null) SpriteMaterials.SetPreservingSize(frameRenderer, back);
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
            => Bind(staticDef, liveState, null);

        public void Bind(CardDefinition staticDef, ICardLiveState liveState,
                         ICardDatabase cardDatabase)
        {
            _def  = staticDef;
            _live = liveState;
            _cardDatabase = cardDatabase;
            // A rebound presenter is showing a different card: its first number is not a change.
            Health().Forget();
            _live.Changed += OnChanged;
            ApplyAll();
        }

        public void Unbind()
        {
            HideTooltip();
            _health?.Forget();
            if (_live == null) return;
            _live.Changed -= OnChanged;
            _live = null;
            _def  = null;
            _cardDatabase = null;
        }

        // Immediate on-card hover cue — the card's original/full title (CardDefinition.Tooltip),
        // for a card whose printed Name was shortened to fit the face. Deliberately NOT gated by
        // HoverRevealModel's delayed board-card focus gesture (EF-049, CardRevealTrigger): that
        // machinery exists to make a deliberate "let me
        // read this" read undo the click it also armed, which does not apply here — this is a
        // passive label with no side effect on the pending press, so it can just track the raw
        // pointer the same way HandCardHover's lift already does on this root.
        void IPointerHoverTarget.OnPointerEnter()
        {
            if (_def == null) return;
            // A finger enters only by pressing, so on touch this banner would flash up under every
            // press and cover the mana cost. Touch has the full inspect surface; this immediate
            // banner remains a desktop-hover affordance only.
            bool touchPressed = Touchscreen.current?.primaryTouch.press.isPressed == true;
            if (!ShouldShowDirectHover(touchPressed))
            {
                HideTooltip();
                return;
            }
            if (!string.IsNullOrEmpty(_def.Tooltip) && tooltipBanner != null)
            {
                if (tooltipLabel != null) tooltipLabel.text = _def.Tooltip;
                tooltipBanner.SetActive(true);
            }
        }

        internal static bool ShouldShowDirectHover(bool touchPressed) => !touchPressed;

        void IPointerHoverTarget.OnPointerExit() => HideTooltip();

        private void HideTooltip()
        {
            if (tooltipBanner != null) tooltipBanner.SetActive(false);
        }

        // Called by InputRouter to highlight this card as the active selection.
        // Sets _isSelected for the BG tint and exhausted-dim override; no rim glow (GH#173).
        public void SetSelectionHighlight(bool selected)
        {
            _isSelected = selected;
            ApplyAuras();
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
            BoardController.ApplyUiFont(label);
        }

        private void OnDestroy() => Unbind();

        // A disabled presenter's coroutines die with it; without this the number stays swollen.
        private void OnDisable() => _health?.Settle();

        private HealthNumber _health;
        private HealthNumber Health() => _health ??= new HealthNumber(this);

        private void OnChanged(CardChange what)
        {
            if ((what & CardChange.Stats)      != 0) RecolorStats();
            if ((what & CardChange.Keywords)   != 0) RebuildKeywords();
            if ((what & CardChange.Auras)      != 0) ApplyAuras();
            if ((what & CardChange.Affordance) != 0) ApplyAuras();
            if ((what & CardChange.Text)       != 0) ApplyBodyText();
        }

        private void ApplyAll()
        {
            if (_def == null) return;
            if (nameLabel != null) nameLabel.text = _def.Name;
            if (costLabel != null) costLabel.text = _def.Cost.ToString();
            ApplyFrame();
            ApplyTribe();
            RecolorStats();
            RebuildKeywords();
            ApplyBodyText();
            ApplyAuras();
        }

        // Rules text band. The face is READ, never re-derived: card text is authored and the rules
        // come off it, so rendering CardTextGenerator here at display time would run the pipeline
        // backwards. CardCi's divergence check is what keeps the authored text honest.
        private void ApplyBodyText()
        {
            if (bodyLabel == null) return;
            if (_def == null) { bodyLabel.gameObject.SetActive(false); return; }

            int spellDamageBonus = _live != null ? _live.SpellDamageBonus : 0;
            string rulesText = CardTextGenerator.GenerateEffective(_def,
                _cardDatabase ?? CardTextGenerator.NoPool.Instance, spellDamageBonus);
            bodyLabel.text = CardTextStyling.Style(rulesText);
            bodyLabel.gameObject.SetActive(rulesText.Length > 0);
        }

        private void ApplyTribe()
        {
            if (tribeBanner == null || tribeLabel == null) return;
            bool visible = _def != null && _def.Type == CardType.MINION
                && _def.Tribe.HasValue && _def.Tribe.Value != Tribe.NONE;
            tribeBanner.SetActive(visible);
            if (!visible)
            {
                tribeLabel.text = string.Empty;
                return;
            }

            string name = _def.Tribe.Value.ToString().ToLowerInvariant();
            tribeLabel.text = char.ToUpperInvariant(name[0]) + name.Substring(1);
            Sprite panel = _ui?.Get(FrameRarityCatalog.TribePanelSprite);
            if (tribePanelRenderer != null)
            {
                SpriteMaterials.SetPreservingSize(tribePanelRenderer, panel);
                tribePanelRenderer.gameObject.SetActive(panel != null);
            }
        }

        // Assigning a sprite to a Sliced-mode SpriteRenderer resets its .size to the sprite's
        // natural PPU dimensions — clobbering the world-space size BoardController set at build
        // time (a 1092px frame @ 100 PPU became a 10.9-unit-wide card). Preserve it across the swap.
        // Sprite assignment goes through SpriteMaterials so each texture gets its own
        // material. Read that class before touching any `.sprite =` on this card: sharing
        // one material across the frame, the sockets and the gem made all six draw the
        // FRAME, and nothing about the scene graph looks wrong when it happens.

        // Data-driven frame/rarity assembly (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md §3 ApplyFrame) — stat-plate
        // visibility keyed by type; the frame sprite is keyed by type+rarity but its sockets are
        // one shared layout (FrameRarityCatalog). No per-card branching.
        private void ApplyFrame()
        {
            // ONE frame for every card — no rarity axis, no type axis. Falls back to the
            // empty-sprite placeholder look if the frame asset is missing.
            Sprite frameSprite = null;
            if (frameRenderer != null && _ui != null)
            {
                frameSprite = _ui.Get(FrameRarityCatalog.FrameSprite);
                SpriteMaterials.SetPreservingSize(frameRenderer, frameSprite);
            }
            _hasPaintedFrame = frameSprite != null;
            if (_hasPaintedFrame) ApplySockets();

            // The socket housings ride on top of the frame. Type decides which of them exist at
            // all: a spell draws the cost socket only, and the frame behind the two stat sockets
            // is plain rail, so leaving them undrawn is the finished look rather than a hole.
            ApplySocketSprites();

            if (gemRenderer != null)
            {
                // The gem is now the rarity read on every card, painted frame or not. It used to
                // be suppressed whenever a frame resolved ("the painted frame carries the rarity")
                // — which, once every rarity had real frame art, meant it was suppressed always
                // and Sam's gem art was drawing nowhere (2026-08-19). The frames were built with
                // an empty socket waiting for it the whole time.
                string gemKey = FrameRarityCatalog.GemSprite(_def.Rarity);
                Sprite gem = _ui?.Get(gemKey);
                gemRenderer.gameObject.SetActive(true);
                SpriteMaterials.Set(gemRenderer, gem);
                // Tint white when a real gem sprite is present; else tint the blank sprite by rarity.
                gemRenderer.color = gem != null ? Color.white : FrameRarityCatalog.GemColor(_def.Rarity);

                // SAY SO WHEN THE GEM DOES NOT RESOLVE. Without this the card front is simply
                // built without its rarity read and nothing anywhere reports it — the same silent
                // shape #295 named for the stat sockets ("No log, no exception. The digits simply
                // sit bare, which is why nothing reported it. The same shape hides the gem
                // bezel."), which is how #341 reached a device unnoticed. Once per rarity: a wall
                // of ninety cards must not print ninety lines.
                if (gem == null && s_reportedMissingGems.Add(gemKey))
                    Debug.LogWarning($"[CardPresenter] no sprite for '{gemKey}' — every {_def.Rarity} " +
                                     "card front is being built WITHOUT its rarity gem, on the board " +
                                     "and in the collection alike. The renderer is left tinted by " +
                                     "rarity, which is the fallback look, not the finished one.");

                PlaceGem(gem);
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

        // Unified socket geometry (FrameRarityCatalog, 2026-08-09): move every card-front element
        // onto the one shared frame layout — same fractions for every rarity, so there is no
        // per-card lookup here any more. Fractions are top-left-origin on the frame; local space
        // is center-origin with +y up, so x' = (fx-.5)·W, y' = (.5-fy)·H. TMP world text:
        // fontSize = em height in units × 10.
        private void ApplySockets()
        {
            const float W = BoardController.CardW, H = BoardController.CardH;
            const float z = -0.01f;

            PlaceDigit(costLabel,       FrameRarityCatalog.CostSocket,      FrameRarityCatalog.CostDigitEm, z);
            PlaceDigit(attackLabel,     FrameRarityCatalog.StatSocketLeft,  FrameRarityCatalog.StatDigitEm, z);
            PlaceDigit(healthLabel,     FrameRarityCatalog.StatSocketRight, FrameRarityCatalog.StatDigitEm, z);
            PlaceDigit(durabilityLabel, FrameRarityCatalog.StatSocketRight, FrameRarityCatalog.StatDigitEm, z); // weapons: durability owns the right boss

            if (nameLabel != null)
            {
                // The title owns this whole band, so consume it before truncating. Fixed sizing
                // sent every over-wide name straight to Ellipsis even though a small reduction
                // would keep the complete title legible (Sam's mobile audit, 2026-09-01).
                nameLabel.enableAutoSizing = true;
                nameLabel.fontSizeMax = FrameRarityCatalog.NameEm * H * 10f;
                nameLabel.fontSizeMin = FrameRarityCatalog.NameEmFloor * H * 10f;
                nameLabel.fontSize     = nameLabel.fontSizeMax;
                nameLabel.overflowMode = TextOverflowModes.Ellipsis;
                nameLabel.color        = FrameRarityCatalog.NameParchment;
                nameLabel.rectTransform.localPosition = new Vector3(
                    (FrameRarityCatalog.NameCenterX - 0.5f) * W,
                    (0.5f - FrameRarityCatalog.NameCenterY) * H, z);
            }

            if (artRenderer != null)
            {
                Vector4 win = FrameRarityCatalog.ArtWindow;
                artRenderer.transform.localPosition = new Vector3(
                    ((win.x + win.z) * 0.5f - 0.5f) * W,
                    (0.5f - (win.y + win.w) * 0.5f) * H, -0.005f);
                artRenderer.transform.localScale = new Vector3(
                    (win.z - win.x) * W, (win.w - win.y) * H, 1f);
                ApplyArtFit();
            }

            if (bodyLabel != null)
            {
                // Rules band: dark ink on the frame's parchment. NOT bold as a whole any more:
                // the weight is baked into the face (GoogleSansFlex-Card, cut by
                // tooling/scripts/instance_font.py), and TMP's faux bold is left to the
                // keywords CardTextStyling wraps in <b> (Sam, 2026-09-28). Bolding the label
                // would bold every word and erase the keyword contrast.
                bodyLabel.color = FrameRarityCatalog.BodyInk;
                bodyLabel.fontStyle &= ~FontStyles.Bold;                bodyLabel.fontSizeMax = FrameRarityCatalog.BodyEm * H * 10f;
                bodyLabel.fontSizeMin = FrameRarityCatalog.BodyEmFloor * H * 10f;
                // The new parchment is centered on the card, so unlike the old frame this needs
                // no horizontal shift to stay off the rails.
                bodyLabel.rectTransform.sizeDelta = new Vector2(FrameRarityCatalog.BodyWidth * W, FrameRarityCatalog.BodyHeight * H);
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

        /// <summary>Sets the gem into the frame's upper-right socket, sized by HEIGHT with width
        /// following the sprite's own aspect ratio.
        ///
        /// <para>Aspect matters because the gems are not one shape: the rare is a square princess
        /// cut, the common and legendary are ovals, the epic is round. Sizing both axes to the
        /// same number would squash whichever ones disagree with it, and it would do so silently —
        /// a stretched gem still looks like a gem.</para>
        ///
        /// <para>It follows that the gem PNGs must be trimmed to their art, since the sprite rect
        /// is all this can see and transparent padding inside it renders as size. They are:
        /// <c>tooling/scripts/normalize_gem_art.py</c> squares them off their content. Re-run it
        /// after any gem is re-exported — an export tool pads, and padding here reads as a gem
        /// that is mysteriously too small.</para></summary>
        private void PlaceGem(Sprite gem)
        {
            if (gemRenderer == null) return;

            const float W = BoardController.CardW, H = BoardController.CardH;

            float height = FrameRarityCatalog.GemHeight * H;
            float aspect = gem != null && gem.rect.height > 0f
                ? gem.rect.width / gem.rect.height
                : 1f;

            gemRenderer.size = new Vector2(height * aspect, height);
            gemRenderer.transform.localPosition = new Vector3(
                (FrameRarityCatalog.GemSocket.x - 0.5f) * W,
                (0.5f - FrameRarityCatalog.GemSocket.y) * H,
                -0.012f);
        }

        /// <summary>Composites the three digit sockets and the gem bezel over the frame.
        ///
        /// <para>These used to be painted into the frame art, which is why there were eight
        /// frames: a spell needed a copy with the two stat housings removed, and every rarity
        /// needed its own metal. Lifting them out collapsed that to one frame plus the socket
        /// sprites (2026-08-19) — one housing in four interior colours, see
        /// <see cref="FrameRarityCatalog.StatSocketSprite"/> — and rarity now reads from the gem
        /// alone.</para>
        ///
        /// <para>Sizing is by the sprite's CANVAS span, not its disc: each socket PNG centres its
        /// disc with a transparent margin so that drawing it at canvas span lands it concentric on
        /// the socket centre. Width and height scale off CardW and CardH independently so a socket
        /// distorts exactly as the frame does rather than staying circular against a stretched
        /// card.</para></summary>
        private void ApplySocketSprites()
        {
            const float W = BoardController.CardW, H = BoardController.CardH;

            // A separate sword runs diagonally behind the circular well; only hilt and point show.
            // Stat frames share a larger display size with inset corner anchors for capture safety.
            Sprite cost   = _hasPaintedFrame ? _ui?.Get(FrameRarityCatalog.CostSocketSprite) : null;
            Sprite attack = _hasPaintedFrame
                ? _ui?.Get(_def.Type == CardType.MINION ? FrameRarityCatalog.MinionAttackSocketSprite
                                                       : FrameRarityCatalog.AttackSocketSprite)
                : null;
            Sprite right  = _hasPaintedFrame
                ? _ui?.Get(FrameRarityCatalog.ShowHealth(_def.Type) ? FrameRarityCatalog.HealthSocketSprite
                                                                    : FrameRarityCatalog.WeaponToughnessSocketSprite)
                : null;
            Sprite sword = _hasPaintedFrame && FrameRarityCatalog.ShowAttack(_def.Type)
                ? _ui?.Get(_def.Type == CardType.MINION ? FrameRarityCatalog.MinionPowerSwordSprite
                                                       : FrameRarityCatalog.WeaponPowerSwordSprite)
                : null;
            Sprite bezel = _hasPaintedFrame ? _ui?.Get(FrameRarityCatalog.GemSocketSprite) : null;

            var statSize = new Vector2(
                FrameRarityCatalog.StatSocketPx / FrameRarityCatalog.MasterW * W,
                FrameRarityCatalog.StatSocketPx / FrameRarityCatalog.MasterH * H);
            var bezelSize = new Vector2(
                FrameRarityCatalog.GemSocketPx / FrameRarityCatalog.MasterW * W,
                FrameRarityCatalog.GemSocketPx / FrameRarityCatalog.MasterH * H);

            // Cost is on every card; the two stat sockets follow the same visibility rules as the
            // digits that sit in them, so a weapon keeps both (attack + durability).
            PlaceSocket(costSocketRenderer,   cost,   FrameRarityCatalog.CostSocket,      statSize,  true);
            PlaceSocket(attackSocketRenderer, attack, FrameRarityCatalog.StatSocketLeft,  statSize,
                        FrameRarityCatalog.ShowAttack(_def.Type));
            PlaceSocket(powerSwordRenderer, sword, FrameRarityCatalog.StatSocketLeft,
                        Vector2.Scale(statSize, FrameRarityCatalog.PowerSwordScale),
                        FrameRarityCatalog.ShowAttack(_def.Type), z: -0.0085f);
            if (powerSwordRenderer != null)
            {
                powerSwordRenderer.transform.localRotation =
                    Quaternion.Euler(0f, 0f, FrameRarityCatalog.PowerSwordAngle);
                powerSwordRenderer.transform.localPosition += new Vector3(
                    statSize.x * FrameRarityCatalog.PowerSwordOffset.x,
                    statSize.y * FrameRarityCatalog.PowerSwordOffset.y, 0f);
            }
            PlaceSocket(healthSocketRenderer, right,  FrameRarityCatalog.StatSocketRight, statSize,
                        FrameRarityCatalog.ShowHealth(_def.Type) || FrameRarityCatalog.ShowDurability(_def.Type));
            PlaceSocket(gemSocketRenderer,    bezel, FrameRarityCatalog.GemSocket,       bezelSize, true, z: -0.011f);
        }

        // z −0.009 sits the socket in front of the frame (−0.006) and the frost overlay (−0.008)
        // but behind every digit (−0.010): a frosted minion still has to be readable, and the ice
        // covers the illustration, never the numbers. The gem bezel goes to −0.011 so the gem
        // itself (−0.012) sets into it.
        private static void PlaceSocket(SpriteRenderer sr, Sprite sprite, Vector2 frac,
                                        Vector2 size, bool show, float z = -0.009f)
        {
            if (sr == null) return;
            bool visible = show && sprite != null;
            sr.gameObject.SetActive(visible);
            if (!visible) return;

            SpriteMaterials.Set(sr, sprite);
            sr.size   = size;
            sr.transform.localPosition = new Vector3(
                (frac.x - 0.5f) * BoardController.CardW,
                (0.5f - frac.y) * BoardController.CardH, z);
        }

        // The equipped weapon is not rendered as a card on the board — HeroSocketsView draws it
        // as the ring beside the hero. The one card that shows an EQUIPPED weapon is the reveal
        // panel's focus copy, bound to the socket's live state, whose Durability is non-null;
        // that is the only case the weapon branch below repaints. Weapon cards in HAND keep the
        // printed attack and durability ApplyFrame wrote above.

        private void RecolorStats()
        {
            if (_live == null || _def == null) return;
            if (_def.Type == CardType.WEAPON) { RecolorEquippedWeapon(); return; }
            if (_def.Type != CardType.MINION) return;

            // In hand the core hasn't materialized instance stats yet (SummonMinion sets them;
            // MaxHealth ≥ 1 for any live minion) — show the printed definition stats instead of
            // live zeros, which read as "0/0 damaged" red.
            if (_live.MaxHealth == 0)
            {
                if (attackLabel != null) { attackLabel.text = (_def.Attack ?? 0).ToString(); attackLabel.color = Color.white; }
                Health().Forget();
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
                Health().Show(healthLabel, _live.Health,
                              HealthInk.Resting(_live.Health, _live.MaxHealth, baseHp));
            }
        }

        // Same colour rules as HeroSocketsView.SetWeapon, so the ring and its enlarged card agree.
        private void RecolorEquippedWeapon()
        {
            if (!_live.Durability.HasValue) return;

            if (attackLabel != null)
            {
                int baseAtk = _def.Attack ?? _live.Attack;
                attackLabel.text  = _live.Attack.ToString();
                attackLabel.color = _live.Attack > baseAtk ? BuffColor
                                  : _live.Attack < baseAtk ? DamageColor
                                  : Color.white;
            }

            if (durabilityLabel != null)
            {
                int dura     = _live.Durability.Value;
                int baseDura = _def.Durability ?? dura;
                durabilityLabel.text  = dura.ToString();
                // Durability only ever falls, so under base always means worn, never buffed.
                durabilityLabel.color = dura < baseDura ? DamageColor : Color.white;
            }
        }

        private bool _hasPaintedFrame;

        /// <summary>Gem keys already reported as unresolvable, so the warning in
        /// <see cref="ApplyFrame"/> is once per rarity rather than once per card. Static because
        /// the collection wall rebinds ONE presenter for every card it bakes.</summary>
        private static readonly HashSet<string> s_reportedMissingGems = new HashSet<string>();

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
                ApplyBodyText();
                return;
            }

            // THE ICON STRIP IS GONE (2026-09-04, Sam: "we aren't doing keyword icons"). It read
            // StatusMarkCatalog for an "icon_kw_<keyword>" address and drew up to four sprites
            // below the art window. No icon_kw_*.png ever existed outside the placeholder
            // generator deleted the same day, so every lookup returned null and every slot stayed
            // hidden — and it could not have rendered anyway, because the painted-frame branch
            // above returns first and every card has a painted frame since 2026-08-19.
            //
            // What remains is the text strip, which is the no-frame path only: a degraded state
            // where frame_card.png failed to load. Keywords on a real card read from the body
            // text, as the §3.1 bold lead.
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
