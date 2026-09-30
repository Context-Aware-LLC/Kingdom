using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// World-space end-turn button (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §6 item 1). Right screen edge,
    /// vertically centered — the genre-standard spot. Built procedurally like
    /// HeroPlateView/ManaTrayView; state-driven tint + label, click forwards to
    /// InputRouter which applies the same guards as the Enter/Space path.
    /// </summary>
    public sealed class EndTurnButtonView : MonoBehaviour
    {
        /// <summary>Royal Council seal-press landing plate at centre-right.</summary>
        public static readonly Vector3 BoardPosition = new(6.3f, 0f, 0f);
        public static readonly Vector2 ArtworkUv =
            new(4091f / 4612f, 1f - 902f / 2080f);

        // Menu chrome, ember on your turn and slate on theirs (BoardButtonChrome). Was a flat
        // green/grey quad at 1.30 x 0.52; enlarged with the chrome (Sam, 2026-09-30).
        private const float ButtonW = 2.40f;
        private const float ButtonH = 0.90f;
        private const int   ChromeOrder = 11;

        private PlayerId       _p0Id;
        private InputRouter    _router;
        private SpriteRenderer _face;
        private TextMeshPro    _label;
        private bool?          _ready;

        public void Init(PlayerId p0Id, InputRouter router)
        {
            _p0Id   = p0Id;
            _router = router;

            _face = BoardButtonChrome.Create(transform, new Vector2(ButtonW, ButtonH),
                                             primary: false, ChromeOrder);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _label = labelGo.AddComponent<TextMeshPro>();
            _label.fontSize  = 2.7f;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color     = BoardButtonChrome.SecondaryInk;
            _label.rectTransform.sizeDelta = new Vector2(ButtonW, ButtonH);
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.text = "END TURN";
            BoardController.ApplyUiFont(_label);
            _label.sortingOrder = ChromeOrder + 1;

            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(ButtonW, ButtonH, 0.1f);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_face == null || state == null) return;
            bool myTurn = state.ActivePlayer == _p0Id && state.Phase == TurnPhase.Main;
            if (_ready != myTurn)
            {
                _ready = myTurn;
                BoardButtonChrome.Apply(_face, new Vector2(ButtonW, ButtonH), primary: myTurn);
                _label.color = myTurn ? BoardButtonChrome.PrimaryInk : BoardButtonChrome.SecondaryInk;
            }
            _label.text = myTurn ? "END TURN" : "ENEMY TURN";
        }

        private void OnMouseDown() => _router?.OnEndTurnClicked();
    }
}
