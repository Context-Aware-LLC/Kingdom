using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-049. Owns the one <see cref="HoverRevealModel"/> and the one <see cref="CardRevealPanel"/>
    /// for the match, and is the only thing here that reads a clock. Per-card
    /// <see cref="CardRevealTrigger"/>s feed it pointer events; it decides what is on screen.
    ///
    /// <para>One model for the whole board, not one per card: two cards must never both believe
    /// they are revealed, and a per-card model turns that from an impossibility into a
    /// coordination problem.</para>
    /// </summary>
    internal sealed class CardRevealController : MonoBehaviour
    {
        private readonly HoverRevealModel _model = new HoverRevealModel();
        private readonly Dictionary<InstanceId, CardRevealTrigger> _triggers = new();

        private CardRevealPanel _panel;
        private CardFlavorTextPanel _flavorPanel;
        private InputRouter     _router;

        private bool       _pressActive;
        private InstanceId _pressCard;
        private bool       _wasPointerHeld;

        internal static CardRevealController Create(Transform parent, InputRouter router)
        {
            var go = new GameObject("CardRevealController");
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<CardRevealController>();
            c._router = router;
            c._panel       = CardRevealPanel.Create(go.transform);
            c._flavorPanel = CardFlavorTextPanel.Create(go.transform);
            return c;
        }

        // The panels re-parent themselves to the camera to stay screen-anchored, so they do not die
        // with this object's own hierarchy. Take them down explicitly.
        private void OnDestroy()
        {
            if (_panel != null) Destroy(_panel.gameObject);
            if (_flavorPanel != null) Destroy(_flavorPanel.gameObject);
        }

        internal void Register(CardRevealTrigger t) => _triggers[t.CardId] = t;

        /// <summary>Called by a trigger being torn down, so the panel cannot outlive the card it is
        /// describing — a hand card played mid-reveal is the ordinary case, not an edge one.</summary>
        internal void Unregister(CardRevealTrigger t)
        {
            if (_triggers.TryGetValue(t.CardId, out CardRevealTrigger held) && held == t)
                _triggers.Remove(t.CardId);

            _model.PointerExit(t.CardId, Time.unscaledTime);
            if (_model.Visible && _model.Target.Equals(t.CardId)) _model.Cancel();
            if (_pressActive && _pressCard.Equals(t.CardId)) _pressActive = false;
            Sync();
        }

        internal void OnPointerEnter(CardRevealTrigger t)
        {
            if (Blocked()) return;
            _model.PointerEnter(t.CardId, Time.unscaledTime);
        }

        internal void OnPointerExit(CardRevealTrigger t)
            => _model.PointerExit(t.CardId, Time.unscaledTime);

        internal void OnPointerDown(CardRevealTrigger t)
        {
            if (Blocked()) return;
            _pressActive = true;
            _pressCard   = t.CardId;
            // A real touch is the only thing that arms the hold — see HoverRevealModel for why a
            // mouse press must not. The unified pointer stream deliberately does not carry which
            // device is driving it, so this asks the touchscreen directly; the fallback keeps
            // reveal-only modes working without a router.
            bool touchActive = _router != null
                ? _router.Pointer.HasActiveTouch
                : Touchscreen.current?.primaryTouch.press.isPressed == true;
            var kind = touchActive
                ? HoverRevealModel.PointerKind.Touch
                : HoverRevealModel.PointerKind.Mouse;
            _model.PointerDown(t.CardId, kind, Time.unscaledTime);
        }

        private void Update()
        {
            float now         = Time.unscaledTime;
            bool  pointerHeld = PointerHeld();

            // Release is POLLED, not taken from OnMouseUp. OnMouseUp is not delivered when the
            // collider is destroyed mid-press — a card played from hand is exactly that — and a
            // press left active would strand the panel on screen with no way to dismiss it.
            if (_pressActive && !pointerHeld)
            {
                _pressActive = false;
                InstanceId pressed = _pressCard;
                if (_model.Release(now)) CancelPressSelection(pressed);
            }

            // Dismiss a sticky reveal when the player presses something that is not a card.
            // OnPointerDown (from CardRevealTrigger.OnMouseDown) sets _pressActive in the same
            // frame it fires, so if a new press arrived without setting _pressActive the player
            // tapped the board, a hero, or empty space — all of which should close the panel.
            bool newPress = pointerHeld && !_wasPointerHeld;
            if (newPress && !_pressActive && _model.Source == HoverRevealModel.RevealSource.Sticky)
                _model.Cancel();

            _wasPointerHeld = pointerHeld;

            _model.Tick(now);
            if (Blocked() && _model.Visible) _model.Cancel();
            Sync();
        }

        private bool PointerHeld()
            => _router != null
                ? _router.Pointer.IsPressed
                : Pointer.current?.press.isPressed == true;

        // The press that became a read was already dispatched to InputRouter by
        // ClickableCard.OnMouseDown, so the card is now sitting selected — primed to be played by
        // the player's next tap, which is not what "let me read this" asked for. Undo exactly that.
        //
        // Not dispatching in the first place would be cleaner than undoing, and needs the click to
        // move from press to release: EF-048's pointer abstraction (press/drag/release) is where
        // that belongs. Until it lands, this is the seam that keeps a read from arming a card.
        private void CancelPressSelection(InstanceId card)
        {
            if (_router == null) return;
            if (!_triggers.TryGetValue(card, out CardRevealTrigger t) || t == null) return;
            _router.CancelSelection(t.Clickable);
        }

        private void Sync()
        {
            if (_panel == null) return;

            if (!_model.Visible
                || !_triggers.TryGetValue(_model.Target, out CardRevealTrigger t)
                || t == null)
            {
                SetLift(null);
                _panel.Hide();
                _flavorPanel?.Hide();
                return;
            }

            // The lift is driven for the HOLD source only. On a mouse hover the card's own
            // OnMouseEnter already raised it, and driving it from here as well would give one
            // behaviour two owners.
            SetLift(_model.Source == HoverRevealModel.RevealSource.Hold ? t : null);
            CardRevealContent content = t.BuildContent();
            _panel.Show(content);
            _flavorPanel?.Show(content.FlavorText);
        }

        // Exactly one card is ever lifted from here, and the previous one is always put back —
        // a card left raised after its reveal ends would stay clear of the fan for good.
        private CardRevealTrigger _lifted;

        private void SetLift(CardRevealTrigger t)
        {
            if (_lifted == t) return;
            if (_lifted != null) _lifted.SetLift(false);
            _lifted = t;
            if (_lifted != null) _lifted.SetLift(true);
        }

        // Borrowed from InputRouter rather than wired separately. UI Toolkit does not block Unity
        // physics, so while a modal surface owns input the board's colliders are still live and a
        // reveal would open behind the scrim. Reading the router's own delegate means the two
        // bootstraps that already set it get this for free — a third wiring site is a third thing
        // to forget, and forgetting it fails silently.
        private bool Blocked()
            => _router != null && _router.UiBlocksInput != null && _router.UiBlocksInput();
    }
}
