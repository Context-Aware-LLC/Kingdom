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

        /// <summary>
        /// Where both bootstraps place this button. Owned here rather than typed at each call site,
        /// because it is a geometry constraint and not a preference — see below.
        ///
        /// <para><b>It must clear the hand fan, and the obvious spot does not.</b> This sat at
        /// <c>y = -3.0</c> and was unpressable for it (#138): the button spans y -3.275..-2.725, and
        /// the local player's hand spans y -4.69..-2.31 (<see cref="HandFanLayout.CentreY"/> 3.50
        /// negated for seat 0, ± half of <see cref="HandFanLayout.HandCardH"/> 2.376). The button was
        /// wholly inside the fan, and hand cards sit at z = 0, -0.03, -0.06… — <i>toward</i> the
        /// camera — so every press in that region hit a card instead. Nothing was wrong with
        /// <c>OnMouseDown</c>; it was never the frontmost collider.</para>
        ///
        /// <para><b>Directly above the hand does not fit either.</b> The fan tops out at -2.31 and
        /// P0's board row starts at -2.125 (slot anchors at |y| 1.3, ± half of
        /// <c>BoardController.CardH</c> 1.65), leaving a 0.185 gap for a 0.55-tall button.</para>
        ///
        /// <para><b>So: the board's centre</b>, clear of both hands and of both seats' cards. Zero is
        /// not a hand-picked number — it is the board's own midline, so it stays correct for either
        /// seat and needs no derivation.</para>
        ///
        /// <para><b>It is no longer clear of the SLOT boxes, and does not need to be (2026-08-12,
        /// #139).</b> <see cref="ClickableBoardSlot"/>'s collider now runs the full half-board, out to
        /// the midline, so both rows' boxes reach y 0 and this button sits inside them. What keeps it
        /// pressable is depth, not distance: the slot boxes sit at z 0.15..0.25 and this one at
        /// z ±0.05, and both <c>OnMouseDown</c> and <c>InputRouter</c>'s <c>Physics.Raycast</c> take
        /// the nearest collider. The y span above is history now — kept because it is the reasoning
        /// that put the button here, not a live constraint.</para>
        ///
        /// <para>RETIREMENT TRIGGER: what this position now depends on is being IN FRONT of the slot
        /// boxes. Moving this button back past <c>ClickableBoardSlot</c>'s z 0.15, or pulling that box
        /// toward the camera, buries it again — and a buried button fails silently and reads as a dead
        /// one, which is exactly how #138 presented. The hand still matters if you move the button
        /// off-centre: <see cref="HandFanLayout.CentreY"/> and <c>HandCardH</c> set the fan's reach.</para>
        /// </summary>
        public static readonly Vector3 BoardPosition = new Vector3(0f, 0f, 0f);

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
