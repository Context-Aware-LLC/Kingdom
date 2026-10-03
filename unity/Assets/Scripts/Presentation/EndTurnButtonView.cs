using System.Collections;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Royal Council's world-space End Turn control. The existing crown stamper rests over the
    /// painted seal press while it is the local player's turn. Clicking slams it into the plate,
    /// lifts the tool out of view and leaves the wax crown behind for the complete opponent turn.
    /// Authority still lives in <see cref="InputRouter.OnEndTurnClicked"/> and the subsequent state.
    /// </summary>
    public sealed class EndTurnButtonView : MonoBehaviour
    {
        public static readonly Vector3 BoardPosition = new(6.3f, 0f, 0f);
        public static readonly Vector2 ArtworkUv =
            new(1477f / 1869f, 1f - 290f / 842f);

        private const float HitW = 3.20f;
        private const float HitH = 2.05f;
        private const float ToolSize = 2.55f;
        private const float SealW = 1.72f;
        private const float SealH = 1.08f;
        private const float ToolRestY = 0.72f;
        private const int SealOrder = 12;
        private const int ToolOrder = 13;

        private PlayerId _p0Id;
        private InputRouter _router;
        private SpriteRenderer _tool;
        private SpriteRenderer _seal;
        private Vector3 _toolRestScale;
        private Vector3 _sealRestScale;
        private bool _myTurn;
        private bool _animating;

        public void Init(PlayerId p0Id, InputRouter router, IUiSpriteProvider uiSprites = null)
        {
            _p0Id = p0Id;
            _router = router;

#if UNITY_EDITOR
            uiSprites ??= new EditorUiSpriteProvider();
#else
            uiSprites ??= new AddressableUiSpriteProvider();
#endif

            _seal = MakeSprite(transform, "WaxSeal", uiSprites.Get(SealStamp.SpriteKey),
                SealW, SealH, SealOrder);
            _sealRestScale = _seal.transform.localScale;
            _seal.gameObject.SetActive(false);

            _tool = MakeSprite(transform, "StampTool", uiSprites.Get(SealStamp.ToolSpriteKey),
                ToolSize, ToolSize, ToolOrder);
            _tool.transform.localPosition = new Vector3(0f, ToolRestY, -0.02f);
            _toolRestScale = _tool.transform.localScale;

            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(HitW, HitH, 0.1f);

            UiSfx.Preload(UiSfx.WaxStamp);
        }

        public void OnStateUpdated(GameState state)
        {
            if (state == null || _tool == null || _seal == null) return;
            _myTurn = state.ActivePlayer == _p0Id && state.Phase == TurnPhase.Main;

            if (_animating) return;
            if (_myTurn) ResetForMyTurn();
            else ShowOpponentSeal();
        }

        private void OnMouseDown()
        {
            if (!_myTurn || _animating) return;
            _router?.OnEndTurnClicked();
            StartCoroutine(StampAndLift());
        }

        private IEnumerator StampAndLift()
        {
            _animating = true;
            _tool.gameObject.SetActive(true);
            _seal.gameObject.SetActive(false);
            SetAlpha(_tool, 1f);

            Vector3 restPosition = new(0f, ToolRestY, -0.02f);
            Vector3 impactPosition = new(0f, ToolRestY - 0.38f, -0.03f);
            yield return AnimateTool(restPosition, impactPosition,
                _toolRestScale, Vector3.Scale(_toolRestScale, new Vector3(1.08f, 0.88f, 1f)),
                1f, 1f, 0.14f);

            _seal.gameObject.SetActive(true);
            _seal.transform.localScale = Vector3.Scale(_sealRestScale, new Vector3(0.52f, 0.38f, 1f));
            UiSfx.Play(UiSfx.WaxStamp);

            yield return AnimateSeal(
                Vector3.Scale(_sealRestScale, new Vector3(0.52f, 0.38f, 1f)),
                Vector3.Scale(_sealRestScale, new Vector3(1.10f, 0.94f, 1f)), 0.08f);
            yield return AnimateSeal(_seal.transform.localScale, _sealRestScale, 0.12f);
            yield return new WaitForSecondsRealtime(0.08f);

            Vector3 liftPosition = new(0f, ToolRestY + 3.25f, -0.05f);
            yield return AnimateTool(impactPosition, liftPosition, _tool.transform.localScale,
                Vector3.Scale(_toolRestScale, new Vector3(1.28f, 1.28f, 1f)),
                1f, 0f, 0.22f);

            _tool.gameObject.SetActive(false);
            // Keep the press committed while the command crosses the network. A rejection that
            // leaves authority on our turn restores it after this grace window; an accepted turn
            // has already changed _myTurn and leaves the wax in place.
            yield return new WaitForSecondsRealtime(1.0f);
            _animating = false;
            if (_myTurn) ResetForMyTurn();
            else ShowOpponentSeal();
        }

        private IEnumerator AnimateTool(
            Vector3 fromPosition, Vector3 toPosition,
            Vector3 fromScale, Vector3 toScale,
            float fromAlpha, float toAlpha,
            float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t);
                _tool.transform.localPosition = Vector3.LerpUnclamped(fromPosition, toPosition, eased);
                _tool.transform.localScale = Vector3.LerpUnclamped(fromScale, toScale, eased);
                SetAlpha(_tool, Mathf.Lerp(fromAlpha, toAlpha, eased));
                yield return null;
            }
        }

        private IEnumerator AnimateSeal(Vector3 fromScale, Vector3 toScale, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                _seal.transform.localScale = Vector3.LerpUnclamped(fromScale, toScale, eased);
                yield return null;
            }
        }

        private void ResetForMyTurn()
        {
            _seal.gameObject.SetActive(false);
            _seal.transform.localScale = _sealRestScale;
            _tool.gameObject.SetActive(true);
            _tool.transform.localPosition = new Vector3(0f, ToolRestY, -0.02f);
            _tool.transform.localScale = _toolRestScale;
            SetAlpha(_tool, 1f);
        }

        private void ShowOpponentSeal()
        {
            _tool.gameObject.SetActive(false);
            _seal.gameObject.SetActive(true);
            _seal.transform.localScale = _sealRestScale;
        }

        private static SpriteRenderer MakeSprite(
            Transform parent, string name, Sprite sprite, float width, float height, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            var sr = go.AddComponent<SpriteRenderer>();
            SpriteMaterials.Set(sr, sprite);
            sr.sortingOrder = order;
            if (sprite != null)
            {
                Vector2 native = sprite.bounds.size;
                go.transform.localScale = new Vector3(width / native.x, height / native.y, 1f);
            }
            else
            {
                Debug.LogWarning($"[EndTurnButtonView] missing sprite for {name}.");
            }
            return sr;
        }

        private static void SetAlpha(SpriteRenderer renderer, float alpha)
        {
            Color color = renderer.color;
            color.a = alpha;
            renderer.color = color;
        }
    }
}
