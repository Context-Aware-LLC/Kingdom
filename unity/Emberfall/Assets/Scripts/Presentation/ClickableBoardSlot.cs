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
