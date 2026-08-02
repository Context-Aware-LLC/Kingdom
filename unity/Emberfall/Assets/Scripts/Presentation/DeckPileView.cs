using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The face-down draw pile for one player (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1 DeckAnchor).
    ///
    /// Nothing rendered here before this — the anchor existed only as the origin the draw
    /// animation flies from. The pile is a stack of card-back sprites offset down-left so the
    /// edges of the cards underneath show, and it THINS as the deck depletes: the stack is the
    /// read, so a player can see fatigue coming without a number on screen at all times.
    ///
    /// COUNT IS THE ONLY LEGAL DATA. Deck contents are redacted for BOTH viewers, the owner
    /// included — GameStateProjector.cs:69 — because deck ORDER is the future draw sequence.
    /// PlayerState.Deck arrives as Count placeholders and nothing else, which is exactly what
    /// this view consumes.
    ///
    /// The number itself is hover-only (Sam, 2026-07-25): permanently-on digits are one more
    /// thing sitting in the corner of every frame, and the stack height already answers "am I
    /// running out" at a glance. Hover is non-blocking and consumes no click — the pace rule.
    ///
    /// Created procedurally by GameBootstrap / NetworkedGameBootstrap — one instance per player.
    /// </summary>
    public sealed class DeckPileView : MonoBehaviour
    {
        // hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1 deck pile: 1.3 wide (x +6.53..+7.83), card-proportioned, so
        // 1.83 tall. Deliberately larger than the 1.17 x 1.65 card quad — the pile is furniture,
        // not a card in play, and the extra size is what keeps the back's pattern legible.
        internal const float PileW = 1.30f;
        private const float PileH = 1.83f;

        // Thickness. Six layers is the visual ceiling; past that the steps stop reading and just
        // smear the silhouette. A full 30-card deck therefore starts at six and sheds one every
        // five cards, which puts the last layer at 1-5 remaining — the range that matters.
        private const int   MaxLayers     = 6;
        private const int   CardsPerLayer = 5;
        private const float LayerStep     = 0.028f;  // down-left, per layer
        private const float LayerStepZ    = 0.003f;  // -z is toward the camera (see CreatePresenter)

        // Layers below the top read as card EDGES, not faces: darkened toward the bottom of the
        // stack so the offset steps show as shadowed slivers rather than six competing patterns.
        private static readonly Color EdgeTint = new Color(0.30f, 0.28f, 0.26f, 1f);

        private const float BaseZ = -0.01f;

        private Vector2 _stackDir;
        private SpriteRenderer[] _layers;
        private TextMeshPro _countLabel;
        private PlayerId _playerId;
        private int _lastCount = -1;

        private static Material s_labelMaterial;

        /// <summary>
        /// Attach to a new GameObject and call Init once before the first OnStateUpdated.
        /// Identified by PlayerId, never by seat index (HeroPlateView.Init precedent).
        /// </summary>
        public void Init(PlayerId playerId, Transform anchor, IUiSpriteProvider uiSprites = null)
        {
            _playerId = playerId;
            transform.SetParent(anchor, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

#if UNITY_EDITOR
            uiSprites ??= new EditorUiSpriteProvider();
#else
            uiSprites ??= new AddressableUiSpriteProvider();
#endif
            Sprite back = uiSprites.Get("card_back");

            // WHICH WAY THE STACK GROWS, derived from the anchor so both seats mirror for free.
            //   y: INWARD, toward the board. The top card's bottom edge sits exactly on the §1.3
            //      baseline (|y| 4.445 against a screen edge at 4.6); growing outward instead put
            //      the last layer 1.7px off the edge, which the first full-frame render showed as
            //      a pile visibly clipped by the bottom of the screen.
            //   x: OUTWARD, into the corner margin. §1.1 gives the pile +6.53..+7.83 and hands the
            //      space below 6.53 to the mana rail — growing inward pushed dark card edges under
            //      the rail's near end. Outward only spends margin (screen edge is 8.18).
            _stackDir = new Vector2(Mathf.Sign(anchor.position.x), -Mathf.Sign(anchor.position.y));

            _layers = new SpriteRenderer[MaxLayers];
            for (int i = 0; i < MaxLayers; i++)
            {
                // i counts UP the stack; MaxLayers-1 is the top card, which sits dead on the
                // anchor. Everything below steps down-left, so the anchor keeps meaning "where
                // the top card is" no matter how many layers are showing — which is what the
                // draw flight aims at.
                int below = (MaxLayers - 1) - i;
                var go = new GameObject($"Layer{i}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(
                    below * LayerStep * _stackDir.x,
                    below * LayerStep * _stackDir.y,
                    BaseZ + below * LayerStepZ);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.drawMode     = SpriteDrawMode.Sliced;   // .size sets exact world dims, PPU-independent
                // SPRITE FIRST, THEN SIZE. Assigning a sprite resets .size to that sprite's native
                // PPU extents — which is the whole reason CardPresenter has SetSpritePreserveSize.
                // Sized first, this rendered the back at its native 14.6 x 20.5 world units and
                // filled the screen with one chain link.
                sr.sprite       = back;
                sr.size         = new Vector2(PileW, PileH);
                sr.color        = i == MaxLayers - 1 ? Color.white : EdgeTint;
                sr.sortingOrder = i;
                _layers[i] = sr;
            }

            // Hover target. On the view root, not a layer child: Unity delivers OnMouseEnter/Exit
            // to the collider's own GameObject only, and the layers come and go with the count.
            // Sized to the fully-fanned stack so the hit area does not shrink as the deck thins.
            var col = gameObject.AddComponent<BoxCollider>();
            float spread = (MaxLayers - 1) * LayerStep;
            col.size   = new Vector3(PileW + spread, PileH + spread, 0.1f);
            col.center = new Vector3(spread * 0.5f * _stackDir.x, spread * 0.5f * _stackDir.y, 0f);

            _countLabel = MakeCountLabel(transform);
            _countLabel.gameObject.SetActive(false);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_layers == null) return;
            SetCount(state.Players[state.SeatOf(_playerId)].Deck.Count);
        }

        /// <summary>
        /// Sets the pile depth directly. Public, not private: DeckPilePreview lives in
        /// Emberfall.Editor and drives the count sweep through THIS method, so the offscreen
        /// render exercises the shipping layer logic rather than a re-derivation of it. (Internal
        /// does not reach across an asmdef boundary — that is what it was tried as first.) The
        /// mana rail shipped a real bug behind a single sampled state; a parameterised thing gets
        /// rendered across its parameter space.
        /// </summary>
        public void SetCount(int count)
        {
            if (count == _lastCount) return;
            _lastCount = count;

            // Ceil, not round: one card left is still a pile you can draw from, and showing
            // nothing there would read as an empty deck one draw early.
            int visible = Mathf.Clamp(Mathf.CeilToInt(count / (float)CardsPerLayer), 0, MaxLayers);

            for (int i = 0; i < _layers.Length; i++)
            {
                if (_layers[i] == null) continue;
                bool on = i >= MaxLayers - visible;
                _layers[i].gameObject.SetActive(on);
                // The topmost SHOWING layer is the face; everything under it is an edge. Without
                // this the face tint would stay pinned to index MaxLayers-1 and a thinned pile
                // would render as an all-dark stack.
                if (on) _layers[i].color = (i == MaxLayers - 1) ? Color.white : EdgeTint;
            }

            if (_countLabel != null) _countLabel.text = count.ToString();
        }

        private void OnMouseEnter()
        {
            if (_countLabel != null && _lastCount > 0) _countLabel.gameObject.SetActive(true);
        }

        private void OnMouseExit()
        {
            if (_countLabel != null) _countLabel.gameObject.SetActive(false);
        }

        private static TextMeshPro MakeCountLabel(Transform parent)
        {
            var go = new GameObject("Count");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, BaseZ - 0.01f);

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.GetComponent<Renderer>().sortingOrder = 20;   // over every layer
            var font = BoardController.CardFont();
            if (font != null) tmp.font = font;
            tmp.fontSize  = 3.4f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = Color.white;
            tmp.rectTransform.sizeDelta = new Vector2(PileW, PileH * 0.4f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;

            // White digits on a black rim, the same treatment as the card stat digits (§3.1) —
            // it is what makes a number legible over painted art without needing a plate behind
            // it. One shared material: per-label fontMaterial instances a material per pile.
            if (s_labelMaterial == null && tmp.font != null)
            {
                s_labelMaterial = new Material(tmp.font.material);
                s_labelMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                s_labelMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.3f);
                s_labelMaterial.EnableKeyword(ShaderUtilities.Keyword_Outline);
            }
            if (s_labelMaterial != null) tmp.fontSharedMaterial = s_labelMaterial;

            return tmp;
        }
    }
}
