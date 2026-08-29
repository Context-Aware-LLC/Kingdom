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
                // The new bottom-docked frame overlaps the hero collider. Put the power hitbox
                // nearer the board camera so a tap on the visible power resolves deterministically
                // as HeroPower rather than whichever overlapping collider Physics returns first.
                col.center = new Vector3(0f, 0f, -0.1f);
            }
        }

        public void Init(PlayerId ownerId) => OwnerId = ownerId;
    }
}
