using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Face-down draw pile for one player. Royal Council uses two pre-rendered piles, each painted
    /// at the perspective of its actual tray; runtime yawing of a flat card back never seated
    /// convincingly in those deep corner cradles. Count remains hover-only and the pile disappears
    /// at zero. Deck contents stay redacted: only <see cref="PlayerState.Deck"/> count is consumed.
    /// </summary>
    public sealed class DeckPileView : MonoBehaviour, IPointerHoverTarget
    {
        // PileW/PileH remain the logical layout footprint so enlarging the artwork does not
        // retreat the camera or disturb the hand. The rendered stack and its hover target are
        // deliberately 2x the first tray-angle pass to fill the painted cradle.
        internal const float PileW = 2.12f;
        private const float PileH = 1.72f;
        private const float RenderW = 4.24f;
        private const float RenderH = 3.44f;
        private const float BaseZ = -0.01f;
        private const string PlayerStackKey = "deck-stack-player-royal-council";
        private const string EnemyStackKey = "deck-stack-enemy-royal-council";

        private SpriteRenderer _stack;
        private TextMeshPro _countLabel;
        private PlayerId _playerId;
        private int _lastCount = -1;

        private static Material s_labelMaterial;

        /// <summary>Attach to a new GameObject and call once before the first state update.</summary>
        public void Init(PlayerId playerId, Transform anchor, bool isLocalSeat, IUiSpriteProvider uiSprites = null)
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

            // P0 is the near/lower seat after BoardBackdrop has registered the artwork anchors.
            // Each sprite already contains its tray-specific foreshortening and shadow, so the
            // runtime transform must stay flat and unrotated.
            // Both refined-table anchors are measured at their tray centres; the old
            // player-only correction would push the stack off the new cradle.
            Sprite renderedStack = uiSprites.Get(isLocalSeat ? PlayerStackKey : EnemyStackKey);
            _stack = MakeStack(transform, renderedStack);

            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(RenderW, RenderH, 0.1f);

            _countLabel = MakeCountLabel(transform);
            _countLabel.gameObject.SetActive(false);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_stack == null) return;
            SetCount(state.Players[state.SeatOf(_playerId)].Deck.Count);
        }

        /// <summary>Public so the offscreen preview can sweep representative count states.</summary>
        public void SetCount(int count)
        {
            if (count == _lastCount) return;
            _lastCount = count;

            _stack.gameObject.SetActive(count > 0);
            if (_countLabel != null) _countLabel.text = count.ToString();
        }

        void IPointerHoverTarget.OnPointerEnter()
        {
            if (_countLabel != null && _lastCount > 0) _countLabel.gameObject.SetActive(true);
        }

        void IPointerHoverTarget.OnPointerExit()
        {
            if (_countLabel != null) _countLabel.gameObject.SetActive(false);
        }

        private static SpriteRenderer MakeStack(Transform parent, Sprite sprite)
        {
            var go = new GameObject("RenderedStack");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, BaseZ);

            var sr = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(sr, sprite);
            sr.sortingOrder = 6;
            if (sprite != null)
            {
                Vector2 native = sprite.bounds.size;
                go.transform.localScale = new Vector3(RenderW / native.x, RenderH / native.y, 1f);
            }
            else
            {
                Debug.LogWarning("[DeckPileView] Royal Council rendered deck stack is missing.");
            }
            return sr;
        }

        private static TextMeshPro MakeCountLabel(Transform parent)
        {
            var go = new GameObject("Count");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, BaseZ - 0.01f);

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.GetComponent<Renderer>().sortingOrder = 20;
            var font = BoardController.CardFont();
            if (font != null) tmp.font = font;
            tmp.fontSize = 3.4f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.rectTransform.sizeDelta = new Vector2(RenderW, RenderH * 0.4f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;

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
