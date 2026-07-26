using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Flies a newly-spawned hand card from its owner's deck pile into its fan slot.
    ///
    /// The first motion in the game. Everything until now appeared by cut — a card was absent
    /// one snapshot and present the next — and Sam's call (2026-07-25) is that a draw has to
    /// read as an event, not as presto-change-o.
    ///
    /// PURELY COSMETIC, and it has to stay that way (ui-pace-rule): the card is already fully
    /// live in the snapshot before the tween starts, the BoxCollider rides the root so it is
    /// clickable the whole way in, and nothing waits on completion. Interrupting it costs
    /// nothing — the component destroys itself and the card is already where it belongs.
    ///
    /// Tweens the ROOT's localPosition toward zero, never the Visual child: HandCardHover owns
    /// the Visual transform, and the two writing the same field would fight on any card hovered
    /// mid-flight. Local space, so the hand fan re-solving underneath (which it does on every
    /// draw, since pitch is a function of hand count) just moves the target with it.
    /// </summary>
    public sealed class CardDrawFlight : MonoBehaviour
    {
        private const float Duration  = 0.26f;
        private const float StartScale = 0.82f;

        private Vector3 _from;
        private float   _delay;
        private float   _t;

        /// <summary>
        /// Starts a flight on <paramref name="card"/> from a world-space origin. Call AFTER the
        /// card is parented to its final anchor — the origin is converted into that anchor's
        /// local space here, and the tween's destination is the anchor itself.
        /// </summary>
        public static void Launch(Transform card, Vector3 fromWorld, float delay = 0f)
        {
            if (card == null || card.parent == null) return;

            var flight = card.GetComponent<CardDrawFlight>();
            if (flight == null) flight = card.gameObject.AddComponent<CardDrawFlight>();

            flight._from  = card.parent.InverseTransformPoint(fromWorld);
            flight._delay = delay;
            flight._t     = 0f;
            flight.SampleAt(0f);
        }

        private void Update()
        {
            _t += Time.deltaTime;
            if (_t < _delay) return;

            float k = Mathf.Clamp01((_t - _delay) / Duration);
            SampleAt(k);
            if (k >= 1f) Destroy(this);
        }

        /// <summary>
        /// Poses the card at normalised progress <paramref name="k"/> (0 = on the pile, 1 = in
        /// slot). Public so DeckPilePreview can render the flight as a strip without play mode:
        /// the preview drives THIS, so what it shows is the shipping curve rather than a second
        /// copy of the easing that could quietly drift from it.
        /// </summary>
        public void SampleAt(float k)
        {
            // Ease-out cubic: fast off the pile, settling into the slot. An ease-in would read
            // as the card being pulled rather than dealt.
            float e = 1f - (1f - k) * (1f - k) * (1f - k);
            transform.localPosition = Vector3.Lerp(_from, Vector3.zero, e);
            float s = Mathf.Lerp(StartScale, 1f, e);
            transform.localScale = new Vector3(s, s, 1f);
        }

        private void OnDestroy()
        {
            // Land it exactly, whether we finished or were torn down mid-flight (card played,
            // scene unloaded). A card left a few hundredths off its slot would desync the fan
            // permanently — nothing else ever writes this transform back to identity.
            transform.localPosition = Vector3.zero;
            transform.localScale    = Vector3.one;
        }
    }
}
