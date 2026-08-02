using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Attached to board-slot anchor GameObjects by ProceduralBoardSetup.SetupClickables.
    //
    // A raycast descriptor for InputRouter's press/drag/release machine (EF-048) — slot identity plus
    // a collider, nothing else. Releasing a dragged hand card over one of these is how a card reaches
    // the board, and it is now the ONLY way: the auto-place shortcut that guessed a slot from a click
    // on empty space is gone (plan decision 13), because on touch it turned stray taps into misplays.
    //
    // Adds its own BoxCollider at Awake so slots are raycast-hittable even without a visible mesh.
    public sealed class ClickableBoardSlot : MonoBehaviour
    {
        public PlayerId OwnerId   { get; private set; }
        public int      SlotIndex { get; private set; }

        private void Awake()
        {
            if (GetComponent<Collider>() == null)
            {
                var col = gameObject.AddComponent<BoxCollider>();
                col.size = new Vector3(0.85f, 1.05f, 0.1f);
                // Strictly behind the card root colliders (z ±0.05 at the same anchor): an exact
                // depth tie is broken arbitrarily per collider, letting the slot steal the ray
                // from the card sitting on it (attacks silently dead on the losing slots).
                col.center = new Vector3(0f, 0f, 0.2f);
            }
        }

        public void Init(PlayerId ownerId, int slotIndex)
        {
            OwnerId   = ownerId;
            SlotIndex = slotIndex;
        }
    }
}
