using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Attached to board-slot anchor GameObjects by ProceduralBoardSetup.SetupClickables.
    // Forwards OnMouseDown to InputRouter so the human player can place cards.
    // Adds its own BoxCollider at Awake so slots are raycast-hittable even without a visible mesh.
    public sealed class ClickableBoardSlot : MonoBehaviour
    {
        public PlayerId OwnerId   { get; private set; }
        public int      SlotIndex { get; private set; }

        private InputRouter _router;

        private void Awake()
        {
            if (GetComponent<Collider>() == null)
            {
                var col = gameObject.AddComponent<BoxCollider>();
                col.size = new Vector3(0.85f, 1.05f, 0.1f);
                // Strictly behind the card root colliders (z ±0.05 at the same anchor): an exact
                // depth tie is broken arbitrarily per collider, letting the slot steal clicks
                // from the card sitting on it (attacks silently dead on the losing slots).
                col.center = new Vector3(0f, 0f, 0.2f);
            }
        }

        public void Init(PlayerId ownerId, int slotIndex, InputRouter router)
        {
            OwnerId   = ownerId;
            SlotIndex = slotIndex;
            _router   = router;
        }

        private void OnMouseDown() => _router?.OnBoardSlotClicked(this);
    }
}
