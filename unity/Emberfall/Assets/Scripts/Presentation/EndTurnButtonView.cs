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
        private static readonly Color ReadyColor   = new Color(0.32f, 0.68f, 0.28f);
        private static readonly Color WaitingColor = new Color(0.24f, 0.24f, 0.31f);

        private const float ButtonW = 1.30f;
        private const float ButtonH = 0.52f;

        private PlayerId    _p0Id;
        private InputRouter _router;
        private Renderer    _face;
        private TextMeshPro _label;
        private MaterialPropertyBlock _mpb;

        public void Init(PlayerId p0Id, InputRouter router)
        {
            _p0Id   = p0Id;
            _router = router;
            _mpb    = new MaterialPropertyBlock();

            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(transform, false);
            faceGo.transform.localScale = new Vector3(ButtonW, ButtonH, 1f);
            Destroy(faceGo.GetComponent<MeshCollider>());
            _face = faceGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(_face, MaterialCatalog.Plate);
            Tint(WaitingColor);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _label = labelGo.AddComponent<TextMeshPro>();
            _label.fontSize  = 1.6f;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color     = Color.white;
            _label.rectTransform.sizeDelta = new Vector2(ButtonW, ButtonH);
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.text = "END TURN";

            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(ButtonW, ButtonH, 0.1f);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_face == null || state == null) return;
            bool myTurn = state.ActivePlayer == _p0Id && state.Phase == TurnPhase.Main;
            Tint(myTurn ? ReadyColor : WaitingColor);
            _label.text = myTurn ? "END TURN" : "ENEMY TURN";
        }

        private void OnMouseDown() => _router?.OnEndTurnClicked();

        private void Tint(Color c)
        {
            _face.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            _face.SetPropertyBlock(_mpb);
        }
    }
}
