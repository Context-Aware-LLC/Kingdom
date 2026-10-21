using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Mulligan confirm button (M2-KICKOFF §E2 minimal slice; C-G1-2b). Visible only during the
    /// Mulligan phase for the local seat: the player toggles cards to toss via <see cref="InputRouter"/>
    /// (red X markers) and this commits the choice through the same intent pipe. Procedural
    /// world-space TMP, the <see cref="EndTurnButtonView"/> idiom (no asset GUID trap). The label
    /// reflects the toss count — "KEEP ALL" at zero, "MULLIGAN (n)" otherwise.
    /// </summary>
    public sealed class MulliganConfirmButtonView : MonoBehaviour, IPointerPressTarget
    {
        // Ember menu chrome (BoardButtonChrome); was a flat green quad at 1.7 x 0.55, enlarged with
        // the chrome (Sam, 2026-09-30). The prompt above moved up with it; PromptTopWorld follows.
        private const float ButtonW = 2.80f;
        private const float ButtonH = 0.90f;
        private const int   ChromeOrder = 11;

        /// <summary>
        /// Where both bootstraps place this button. Owned here rather than typed at each call site,
        /// because it is a geometry constraint and not a preference — see below.
        ///
        /// <para><b>It must clear the hand fan, and the obvious spot does not.</b> This sat at
        /// <c>y = -3.0</c> and was unpressable for it (#138): the button spans y -3.275..-2.725, and
        /// the local player's hand spans y -5.01..-2.37 (<see cref="HandFanLayout.CentreY"/> 3.56
        /// negated for seat 0, ± half of <see cref="HandFanLayout.HandCardH"/> 2.376). The button was
        /// wholly inside the fan, and hand cards sit at z = 0, -0.03, -0.06… — <i>toward</i> the
        /// camera — so every press in that region hit a card instead. Nothing was wrong with
        /// the press handler; it was never the frontmost collider.</para>
        ///
        /// <para><b>Directly above the hand does not fit either.</b> The fan tops out at -2.31 and
        /// P0's board row starts at -2.125 (slot anchors at |y| 1.3, ± half of
        /// <c>BoardController.CardH</c> 1.65), leaving a 0.185 gap for a 0.55-tall button.</para>
        ///
        /// <para><b>So: the board's centre</b>, clear of both hands and of both seats' cards. Zero is
        /// not a hand-picked number — it is the board's own midline, so it stays correct for either
        /// seat and needs no derivation.</para>
        ///
        /// <para><b>The board rows no longer have colliders at all (2026-08-12, #139),</b> so the
        /// "clear of both seats' cards" half of the reasoning above is now free. The 14 invisible
        /// slot boxes this used to have to dodge are deleted; the only colliders left on the board
        /// are the cards themselves, the two heroes and the two hero-power sockets, none of which
        /// come near the midline.</para>
        ///
        /// <para>RETIREMENT TRIGGER: what is left to protect is the HAND, which still reaches up to
        /// <see cref="HandFanLayout.TopEdgeY"/> (-2.372) with its cards in front of z 0. If you move
        /// this button off the midline and down, or move the fan up
        /// (<see cref="HandFanLayout.CentreY"/>, <c>HandCardH</c>), re-check the overlap — a button
        /// behind a hand card fails silently and reads as a dead button, which is how #138
        /// presented.</para>
        /// </summary>
        // Lift the chrome above the Royal Council table's raised centre trim.
        public static readonly Vector3 BoardPosition = new Vector3(0f, 0f, -0.5f);

        // The "TAP CARDS TO REPLACE" prompt, above the button.
        private const float InstructionY = 0.85f;
        private const float InstructionH = 0.55f;

        /// <summary>
        /// World-space top of the prompt above the button. <see cref="MulliganRowLayout"/> seats the
        /// opening cards above this line (#401): the row is nearer the camera than the button, so a
        /// card drawn over it would win every press and the button would read as dead (#138 again).
        /// </summary>
        internal static Vector3 PromptTopWorld
            => BoardPosition + new Vector3(0f, InstructionY + InstructionH * 0.5f, 0f);

        private PlayerId    _p0Id;
        private InputRouter _router;
        private SpriteRenderer _face;
        private TextMeshPro _label;
        private TextMeshPro _instruction;
        private BoxCollider _collider;

        public void Init(PlayerId p0Id, InputRouter router)
        {
            _p0Id   = p0Id;
            _router = router;

            _face = BoardButtonChrome.Create(transform, new Vector2(ButtonW, ButtonH),
                                             primary: true, ChromeOrder);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _label = labelGo.AddComponent<TextMeshPro>();
            _label.fontSize  = 2.6f;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color     = BoardButtonChrome.PrimaryInk;
            _label.rectTransform.sizeDelta = new Vector2(ButtonW, ButtonH);
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            BoardController.ApplyUiFont(_label);
            _label.sortingOrder = ChromeOrder + 1;

            var instructionGo = new GameObject("Instruction");
            instructionGo.transform.SetParent(transform, false);
            instructionGo.transform.localPosition = new Vector3(0f, InstructionY, -0.01f);
            _instruction = instructionGo.AddComponent<TextMeshPro>();
            _instruction.fontSize  = 1.9f;
            _instruction.alignment = TextAlignmentOptions.Center;
            _instruction.color     = new Color(0.95f, 0.78f, 0.25f);
            _instruction.fontStyle = FontStyles.Bold;
            _instruction.rectTransform.sizeDelta = new Vector2(5.2f, InstructionH);
            _instruction.textWrappingMode = TextWrappingModes.NoWrap;
            _instruction.text = Loc.Get("mulligan_instruction");
            BoardController.ApplyUiFont(_instruction);

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
            SetVisible(state.Phase == TurnPhase.Mulligan && !SeatDone(state)
                && (_router == null || !_router.OpeningBlocksInput));
        }

        private bool SeatDone(GameState state)
        {
            int seat = state.SeatOf(_p0Id);
            return seat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
        }

        private void OnTossCountChanged(int count)
        {
            if (_label != null)
                _label.text = count == 0 ? Loc.Get("mulligan_keep_all_button")
                                          : Loc.Format("mulligan_replace_button", count);
        }

        void IPointerPressTarget.OnPointerDown() => _router?.OnMulliganConfirmClicked();

        private void SetVisible(bool on)
        {
            if (_face != null)     _face.gameObject.SetActive(on);
            if (_label != null)    _label.gameObject.SetActive(on);
            if (_instruction != null) _instruction.gameObject.SetActive(on);
            if (_collider != null) _collider.enabled = on;
        }
    }
}
