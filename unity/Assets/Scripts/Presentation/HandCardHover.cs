using UnityEngine;
using UnityEngine.Rendering;

namespace Kingdom.Presentation
{
    // Hover-reveal for cards in hand (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8, revised by Sam 2026-07-24).
    //
    // §8 originally ruled the hovered card "does not move — no lift, no scale", because hand slots
    // were then a flat row where nothing overlapped and nothing was clipped. The fan invalidated
    // that premise: the bottom 6.2% of the centre card and 17.2% of the outermost cards are below
    // the screen edge (NOR-147 raised the hand from 30% / 41% to 15% / 26%, NOR-244 again from
    // there). Re-sorting cannot reveal what is off-screen, so the card rises far enough to clear
    // the edge entirely.
    //
    // THIS COMPONENT ALSO OWNS THE RESTING SIZE OF A CARD IN HAND (EF-062 / NOR-244). A hand card
    // draws at HandFanLayout.RestScale, a board card at 1.0, and this is the only thing that knows
    // which a card currently is — BoardController toggles `enabled` on the zone change and nothing
    // else does. So enabling applies the hand size and disabling takes it away, in the same place
    // the lift is applied and removed, rather than in a second per-zone branch that could disagree.
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
    internal sealed class HandCardHover : MonoBehaviour, IPointerHoverTarget
    {
        // Enough to clear the screen edge for every card in the fan. DERIVED, not dialled: with
        // CentreY 3.56 the outermost card hides 17.2% of a 2.376-tall card (0.408 units) and the
        // hover Scale grows it another 0.238 downward about its own centre, so 0.646 is the floor
        // and 0.66 clears it.
        //
        // Sam tuned this to 0.80 live on 2026-08-07, against a hand that was 26% hidden AND whose
        // resting text was illegible — the lift was doing clearance and readability together. NOR-244
        // moved readability onto the resting card, so the extra 0.20 now buys nothing and costs
        // board occlusion: at 0.80 a revealed card reaches y -1.274, past the CENTRE of the player's
        // own minions at -1.3. The emphasis Sam tuned lives in Scale, which is untouched.
        internal const float Lift = 0.66f;

        // Was "the slightest of scale, just for emphasis" (Sam, 2026-07-24) at 1.05. Sam raised it
        // to 1.2 live (2026-08-07). It MULTIPLIES HandFanLayout.RestScale rather than replacing it,
        // so a revealed card is 1.296 * 1.2 = 1.5552x a board card. Left at Sam's value deliberately:
        // it is his emphasis dial, and NOR-244 was scoped to the RESTING card.
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
        private BoxCollider  _collider;
        private int   _baseOrder;
        private bool  _hovered;
        private bool  _externalReveal;
        private float _t;          // 0 = resting, 1 = fully revealed

        // Either source raises the card. _hovered is the mouse; _externalReveal is EF-049's
        // touch long-press, which has no enter/exit to work with.
        private bool Raised => _hovered || _externalReveal;

        internal void Init(Transform visual, SortingGroup group)
        {
            _visual   = visual;
            _group    = group;
            // Root component, root collider — see the flicker note above. The collider still never
            // MOVES, but it does have to match the card's DRAWN size: the fan spaces cards by their
            // drawn width, so a 1.0x box under a 1.44x card would leave dead gaps falling exactly on
            // the visible edges of each card.
            _collider = GetComponent<BoxCollider>();
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

        // Entering the hand. Update() only runs while _t is CHANGING, so a card that is enabled and
        // never hovered would otherwise sit at whatever scale it was last left at — board size for a
        // card returned to hand, identity for a freshly built one.
        private void OnEnable()
        {
            // The hover growth is emphasis and not worth re-sizing a collider every frame for; the
            // resting 1.44x is the card's real footprint in hand and is what the fan's pitch assumes.
            if (_collider != null)
                _collider.size = new Vector3(HandFanLayout.HandCardW, HandFanLayout.HandCardH, 0.1f);
            Apply();
        }

        // Leaving the hand (played, discarded) must not strand a card lifted and enlarged: the
        // component is disabled on the board path, and that has to snap the visual back itself
        // because Update stops running. It resets to IDENTITY rather than calling Apply(), which
        // would leave the card sitting on the board at hand size.
        private void OnDisable()
        {
            _hovered        = false;
            _externalReveal = false;
            _t              = 0f;

            if (_visual != null)
            {
                _visual.localPosition = Vector3.zero;
                _visual.localScale    = Vector3.one;
                _visual.localRotation = Quaternion.identity;
            }
            if (_collider != null)
                _collider.size = new Vector3(BoardController.CardW, BoardController.CardH, 0.1f);
            if (_group != null) _group.sortingOrder = _baseOrder;
        }

        void IPointerHoverTarget.OnPointerEnter() => _hovered = true;
        void IPointerHoverTarget.OnPointerExit()  => _hovered = false;

        /// <summary>
        /// EF-049 / NOR-147. Raises the card from a source that is not the pointer being over it.
        /// Even with the hand raised (NOR-147), a small fraction of each card remains below the
        /// screen edge, and a hover gesture must fully expose it. A finger is only over a card
        /// while it is pressing, and the sticky read outlives the press, so the reveal layer's
        /// long-press drives it here instead.
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
            _visual.localScale    = Vector3.one
                                  * Mathf.Lerp(HandFanLayout.RestScale,
                                               HandFanLayout.RestScale * Scale, _t);

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
