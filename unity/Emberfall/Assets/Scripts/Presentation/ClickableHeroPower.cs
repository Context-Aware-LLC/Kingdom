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
                // Match the interactive circular opening, not the decorative blades that extend
                // beyond it and overlap the hero frame.
                float d = HeroSocketsView.HitDiameter;
                col.size = new Vector3(d, d, 0.1f);
                // Kept nearer the board camera than the hero's box, which is the whole plate. The
                // overlap shrank from "the socket sits on the portrait" to the SocketOverlap 0.15
                // the ring laps onto the plate's edge (2026-09-04), but it did not go away, so a
                // tap in that strip still needs a deterministic answer. InputRouter.Probe
                // additionally walks past the OPPONENT's socket, which is a pure obstruction;
                // read that comment before widening this box.
                Vector2 aperture = HeroSocketsView.PowerApertureOffset;
                col.center = new Vector3(aperture.x, aperture.y, -0.1f);
            }
        }

        public void Init(PlayerId ownerId) => OwnerId = ownerId;
    }
}
