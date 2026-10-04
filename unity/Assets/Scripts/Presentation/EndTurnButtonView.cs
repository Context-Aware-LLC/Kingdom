using System.Collections;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Royal Council's world-space End Turn control: <b>the wax crown seal is the button</b>. It
    /// sits centred on the painted seal press for the whole match — lit and pressable on the local
    /// player's turn, dimmed while the opponent plays. Pressing it squashes and springs back.
    /// Authority still lives in <see cref="InputRouter.OnEndTurnClicked"/> and the subsequent state.
    ///
    /// <para>A stamper tool used to rest on the plate and slam the wax into it, with the wax only
    /// appearing afterwards (removed 2026-10-02, Sam). Two sprites that each had to line up with a
    /// painted fixture never did; one sprite centred on the plate has nothing to disagree with.</para>
    /// </summary>
    public sealed class EndTurnButtonView : MonoBehaviour, IPointerPressTarget
    {
        public static readonly Vector3 BoardPosition = new(6.3f, 0f, 0f);

        // The centre of the seal press's face in royal_council_battle_table_v2_composite.png
        // (1867 x 842), read off the plate's own ellipse. See ProceduralBoardSetup for the space.
        public static readonly Vector2 ArtworkUv =
            new(1666f / 1867f, 1f - 385f / 842f);

        private const float SealW = 1.72f;
        private const float SealH = 1.08f;
        // The tap target tracks the wax, with a margin for a thumb.
        private const float HitW = SealW * 1.25f;
        private const float HitH = SealH * 1.35f;
        private const int SealOrder = 12;

        private static readonly Color LitColor = Color.white;
        private static readonly Color DimColor = new(0.50f, 0.46f, 0.46f, 1f);

        private PlayerId _p0Id;
        private InputRouter _router;
        private SpriteRenderer _seal;
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
            _seal.color = DimColor;

            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(HitW, HitH, 0.1f);

            UiSfx.Preload(UiSfx.WaxStamp);
        }

        public void OnStateUpdated(GameState state)
        {
            if (state == null || _seal == null) return;
            _myTurn = state.ActivePlayer == _p0Id && state.Phase == TurnPhase.Main;

            if (_animating) return;
            Show(_myTurn);
        }

        void IPointerPressTarget.OnPointerDown()
        {
            if (!_myTurn || _animating) return;
            _router?.OnEndTurnClicked();
            StartCoroutine(Press());
        }

        private IEnumerator Press()
        {
            _animating = true;
            UiSfx.Play(UiSfx.WaxStamp);

            Vector3 pressed = Vector3.Scale(_sealRestScale, new Vector3(0.90f, 0.84f, 1f));
            yield return AnimateSeal(_sealRestScale, pressed, 0.07f);
            _seal.color = DimColor;
            yield return AnimateSeal(pressed, Vector3.Scale(_sealRestScale, new Vector3(1.05f, 1.03f, 1f)), 0.08f);
            yield return AnimateSeal(_seal.transform.localScale, _sealRestScale, 0.12f);

            // Keep the press committed while the command crosses the network. A rejection that
            // leaves authority on our turn relights it after this grace window; an accepted turn
            // has already changed _myTurn and leaves the seal dim.
            yield return new WaitForSecondsRealtime(1.0f);
            _animating = false;
            Show(_myTurn);
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

        private void Show(bool lit)
        {
            _seal.transform.localScale = _sealRestScale;
            _seal.color = lit ? LitColor : DimColor;
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
    }
}
