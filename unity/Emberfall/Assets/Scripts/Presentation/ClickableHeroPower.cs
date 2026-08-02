using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Attached to the HeroPower anchor by ProceduralBoardSetup.SetupClickables.
    //
    // A raycast descriptor for InputRouter's press/drag/release machine (EF-048). An UNTARGETED power
    // fires on a tap — press and release without travelling, which is the same gesture on a mouse and
    // a thumb. A power with an explicit-choice (ANY_*) selector is a drag source instead: aim it at a
    // character and release there.
    public sealed class ClickableHeroPower : MonoBehaviour
    {
        public PlayerId OwnerId { get; private set; }

        private void Awake()
        {
            if (GetComponent<Collider>() == null)
            {
                var col = gameObject.AddComponent<BoxCollider>();
                // The hero-power RING, not the frame canvas it is drawn on. The canvas carries
                // 34% transparent margin for the weapon's stat bosses, so sizing this to the
                // canvas would put a hit target on empty air and steal the bottom of the
                // weapon ring stacked directly above.
                float d = HeroSocketsView.FrameSize * HeroSocketsView.RingFraction;
                col.size = new Vector3(d, d, 0.1f);
            }
        }

        public void Init(PlayerId ownerId) => OwnerId = ownerId;
    }
}
