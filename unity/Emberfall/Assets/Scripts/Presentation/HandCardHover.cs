using UnityEngine;
using UnityEngine.Rendering;

namespace Emberfall.Presentation
{
    // Hover-reveal for cards in hand (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8, revised by Sam 2026-07-24).
    //
    // §8 originally ruled the hovered card "does not move — no lift, no scale", because hand slots
    // were then a flat row where nothing overlapped and nothing was clipped. The fan invalidated
    // that premise: 30% of every card is now deliberately below the screen edge, and re-sorting
    // cannot reveal what is off-screen. So the card rises far enough to clear the edge entirely.
    //
    // What stays parked is the EXPENSIVE version — the card flying out of the hand and enlarging.
    // That one has to overlap the board, sort above everything, animate back, and survive being
    // interrupted mid-sweep. This card rises in place and never leaves the fan, which keeps all of
    // that out of scope while still exposing the whole card.
    //
    // THE FLICKER HAZARD §8 NAMES IS REAL, and it is why the lift happens on a child transform.
    // A card that moves under the cursor can move out from under it, un-hover, drop, re-hover, and
    // oscillate. Here the BoxCollider lives on the card ROOT and never moves; only the "Visual"
    // child rises. The pointer therefore cannot lose the card it is hovering, so no hysteresis and
    // no decoupled hover region are needed.
    internal sealed class HandCardHover : MonoBehaviour
    {
        // Enough to clear the screen edge for every card in the fan. The centre card hides 30% of
        // its height (0.495 units) and the sag puts the outermost near 41% (0.675), so 0.70 was the
        // floor that fully exposed the worst case rather than merely improving it. Sam took it to
        // 0.80 live (2026-08-07) — clearance is no longer the binding constraint, readability is.
        internal const float Lift = 0.80f;

        // Was "the slightest of scale, just for emphasis" (Sam, 2026-07-24) at 1.05. Sam raised it
        // to 1.2 live (2026-08-07), which is past emphasis — at this size the scale is doing
        // legibility work alongside the lift, so treat the two as one tuned pair, not independent.
        internal const float Scale = 1.2f;

        // Reveal is non-blocking, per the standing pace rule — the hand must never gate input
        // behind an animation. Speed is 1/duration: ~0.11s each way (Sam asked for it slower than
        // the first pass at 14 / ~0.07s, which read as a snap rather than a lift).
        private const float Speed = 9f;

        // Clears all ten hand orders (HandSortBase + 0..9) so the hovered card is unambiguously on
        // top of both neighbours, not just the one below it.
        private const int HoverSortBoost = 20;

        // SORTING ORDER ALONE IS NOT ENOUGH. The card body material is opaque, so the depth buffer
        // overrules draw order: HandFanLayout stages each card 0.03 nearer the camera than the one
        // before it, and a hovered card drawn last still has its pixels rejected wherever a
        // physically-nearer neighbour already wrote depth. The symptom is a card that half-reveals
        // — clear where it overhangs nothing, still buried exactly where it overlaps a neighbour.
        // So the reveal must also travel FORWARD. 0.40 comfortably clears the deepest stagger a
        // 10-card hand can produce (9 * 0.03 = 0.27); negative z is toward the camera.
        private const float HoverZ = 0.40f;

        private Transform    _visual;
        private SortingGroup _group;
        private int   _baseOrder;
        private bool  _hovered;
        private bool  _externalReveal;
        private float _t;          // 0 = resting, 1 = fully revealed

        // Either source raises the card. _hovered is the mouse; _externalReveal is EF-049's
        // touch long-press, which has no enter/exit to work with.
        private bool Raised => _hovered || _externalReveal;

        internal void Init(Transform visual, SortingGroup group)
        {
            _visual = visual;
            _group  = group;
        }

        /// <summary>The card's resting sort order — its hand index (see BoardController). Called
        /// every state tick, so it must RE-APPLY the hover boost rather than skip while hovered:
        /// Apply() only runs while _t is changing, so a tick landing after the lift settled would
        /// otherwise drop the card back under its neighbours permanently.</summary>
        internal void SetBaseOrder(int order)
        {
            _baseOrder = order;
            if (_group != null) _group.sortingOrder = _t > 0.01f ? order + HoverSortBoost : order;
        }

        // Leaving the hand (played, discarded) must not strand a card lifted and enlarged: the
        // component is disabled on the board path, and that has to snap the visual back itself
        // because Update stops running.
        private void OnDisable()
        {
            _hovered        = false;
            _externalReveal = false;
            _t              = 0f;
            Apply();
        }

        private void OnMouseEnter() => _hovered = true;
        private void OnMouseExit()  => _hovered = false;

        /// <summary>
        /// EF-049. Raises the card from a source that is not the mouse. The lift exists because 30%
        /// of every hand card is deliberately below the screen edge, and that is just as true under
        /// a finger as under a cursor — but <c>OnMouseEnter</c>/<c>OnMouseExit</c> never fire on
        /// touch, so before this the lift simply did not exist on Android. The reveal layer's
        /// long-press drives it here.
        /// </summary>
        internal void SetExternalReveal(bool revealed) => _externalReveal = revealed;

        private void Update()
        {
            float target = Raised ? 1f : 0f;
            if (_t == target) return;
            _t = Mathf.MoveTowards(_t, target, Speed * Time.deltaTime);
            Apply();
        }

        private void Apply()
        {
            if (_visual == null) return;

            _visual.localPosition = new Vector3(0f, Lift * _t, -HoverZ * _t);
            _visual.localScale    = Vector3.one * Mathf.Lerp(1f, Scale, _t);

            // Straighten as it rises. The card inherits its tilt from the fan anchor, so cancelling
            // that rotation on the visual brings the text level at full reveal. DeltaAngle because
            // eulerAngles reports the anchor's -18 degrees as 342.
            float tilt = transform.parent != null
                ? Mathf.DeltaAngle(0f, transform.parent.eulerAngles.z)
                : 0f;
            _visual.localRotation = Quaternion.Euler(0f, 0f, -tilt * _t);

            if (_group != null)
                _group.sortingOrder = _t > 0.01f ? _baseOrder + HoverSortBoost : _baseOrder;
        }
    }
}
