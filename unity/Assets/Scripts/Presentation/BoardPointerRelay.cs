using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Kingdom.Presentation
{
    /// <summary>Implemented by a component on a collider's GameObject that wants to know the
    /// pointer is over it. See <see cref="BoardPointerRelay"/>.</summary>
    internal interface IPointerHoverTarget
    {
        void OnPointerEnter();
        void OnPointerExit();
    }

    /// <summary>Implemented by a component on a collider's GameObject whose whole action is a
    /// press (end turn, mulligan confirm, an Unearth pick). See <see cref="BoardPointerRelay"/>.</summary>
    internal interface IPointerPressTarget
    {
        void OnPointerDown();
    }

    /// <summary>
    /// Replaces Unity's <c>OnMouseEnter</c> / <c>OnMouseExit</c> / <c>OnMouseDown</c> messages for
    /// everything on the board that is not an <see cref="InputRouter"/> gesture subject: the HUD
    /// buttons, the hover affordances and the reveal triggers. One raycast, the nearest collider,
    /// and the components on THAT GameObject — the same contract the engine messages had, so the
    /// z-ordering every collider here was placed by still decides who wins.
    ///
    /// <para><b>Why not the engine messages.</b> A single <c>OnMouse*</c> method anywhere in the
    /// player makes Unity raycast from every camera on every frame, on a phone included, whether
    /// or not a finger is down — and it says so with a build warning. Here a finger costs a
    /// raycast only while it is on the glass.</para>
    ///
    /// <para><b>A finger exists only while it is down.</b> The engine left its simulated mouse
    /// parked where the last touch lifted, so an enter arrived with the press and the matching
    /// exit often never did (#362: a hover banner stuck over the mana cost). Here a lifted finger
    /// is no pointer at all, so whatever it was over gets its exit on the frame it lifts.</para>
    ///
    /// <para><b>It runs before every other Update</b> (the execution order below), as the engine
    /// messages did from PreUpdate. <see cref="CardRevealController"/> depends on that: it tells a
    /// press that landed on a card from one that did not by whether OnPointerDown has already
    /// arrived by the time its own Update runs.</para>
    ///
    /// <para>Deliberately not part of <see cref="InputRouter"/>: the review board runs without a
    /// router and still has cards and deck piles to hover. <see cref="BoardController"/> adds this.
    /// It gates nothing itself — each target already owns its own "not now" rule.</para>
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    internal sealed class BoardPointerRelay : MonoBehaviour
    {
        private static readonly List<IPointerHoverTarget> s_hover = new List<IPointerHoverTarget>();
        private static readonly List<IPointerPressTarget> s_press = new List<IPointerPressTarget>();

        private GameObject _over;

        private void Update()
        {
            GameObject now  = null;
            bool       down = false;

            Pointer pointer = Pointer.current;
            if (pointer != null)
            {
                down = pointer.press.wasPressedThisFrame;
                // `down` covers a tap that landed and lifted inside one frame, which isPressed
                // has already forgotten.
                if (!(pointer is Touchscreen) || down || pointer.press.isPressed)
                    now = Under(pointer.position.ReadValue());
            }

            // Unity's ==, so a target destroyed while hovered reads as null: it is sent nothing,
            // and its own OnDestroy is what cleans up after it (see CardRevealTrigger).
            if (now != _over)
            {
                if (_over != null)
                {
                    _over.GetComponents(s_hover);
                    for (int i = 0; i < s_hover.Count; i++) s_hover[i].OnPointerExit();
                }
                _over = now;
                if (_over != null)
                {
                    _over.GetComponents(s_hover);
                    for (int i = 0; i < s_hover.Count; i++) s_hover[i].OnPointerEnter();
                }
                s_hover.Clear();
            }

            if (down && _over != null)
            {
                _over.GetComponents(s_press);
                for (int i = 0; i < s_press.Count; i++) s_press[i].OnPointerDown();
                s_press.Clear();
            }
        }

        private static GameObject Under(Vector2 screenPos)
        {
            Camera cam = Camera.main;
            if (cam == null || !cam.pixelRect.Contains(screenPos)) return null;
            return Physics.Raycast(cam.ScreenPointToRay(screenPos), out RaycastHit hit, 100f)
                ? hit.collider.gameObject
                : null;
        }
    }
}
