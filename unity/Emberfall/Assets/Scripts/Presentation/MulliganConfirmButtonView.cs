using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Mulligan confirm button (M2-KICKOFF §E2 minimal slice; C-G1-2b). Visible only during the
    /// Mulligan phase for the local seat: the player toggles cards to toss via <see cref="InputRouter"/>
    /// (red X markers) and this commits the choice through the same intent pipe. Procedural
    /// world-space TMP, the <see cref="EndTurnButtonView"/> idiom (no asset GUID trap). The label
    /// reflects the toss count — "KEEP ALL" at zero, "MULLIGAN (n)" otherwise.
    /// </summary>
    public sealed class MulliganConfirmButtonView : MonoBehaviour
    {
        private static readonly Color ButtonColor = new Color(0.32f, 0.68f, 0.28f);

        private const float ButtonW = 1.7f;
        private const float ButtonH = 0.55f;

        private PlayerId    _p0Id;
        private InputRouter _router;
        private Renderer    _face;
        private TextMeshPro _label;
        private BoxCollider _collider;
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
            _face.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", ButtonColor);
            _face.SetPropertyBlock(_mpb);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _label = labelGo.AddComponent<TextMeshPro>();
            _label.fontSize  = 1.3f;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color     = Color.white;
            _label.rectTransform.sizeDelta = new Vector2(ButtonW, ButtonH);
            _label.textWrappingMode = TextWrappingModes.NoWrap;

            _collider = gameObject.AddComponent<BoxCollider>();
            _collider.size = new Vector3(ButtonW, ButtonH, 0.1f);

            if (_router != null) _router.MulliganSelectionChanged += OnTossCountChanged;
            OnTossCountChanged(0);
            SetVisible(false);
        }

        private void OnDestroy()
        {
            if (_router != null) _router.MulliganSelectionChanged -= OnTossCountChanged;
        }

        // Wired to the truth-cadence snapshot (like InputRouter) so visibility tracks the phase
        // exactly as the router's IsMulliganActive does.
        public void OnStateUpdated(GameState state)
        {
            if (_face == null || state == null) return;
            SetVisible(state.Phase == TurnPhase.Mulligan && !SeatDone(state));
        }

        private bool SeatDone(GameState state)
        {
            int seat = state.SeatOf(_p0Id);
            return seat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
        }

        private void OnTossCountChanged(int count)
        {
            if (_label != null)
                _label.text = count == 0 ? "KEEP ALL" : $"MULLIGAN ({count})";
        }

        private void OnMouseDown() => _router?.OnMulliganConfirmClicked();

        private void SetVisible(bool on)
        {
            if (_face != null)     _face.gameObject.SetActive(on);
            if (_label != null)    _label.gameObject.SetActive(on);
            if (_collider != null) _collider.enabled = on;
        }
    }
}
