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

        private Collider _col;

        private void Awake()
        {
            _col = GetComponent<Collider>();
            if (_col == null)
            {
                var col = gameObject.AddComponent<BoxCollider>();

                // MAXIMUM SIZE, not a tuned one (Sam, 2026-08-12, #139). The box was 0.85 x 1.05
                // against a 1.35 slot pitch, leaving a 0.5-wide dead channel between every pair —
                // 3.0 units of nothing across a full row, which is what made placement feel like
                // aiming at invisible cups. Both dimensions now run to the first thing that would
                // actually collide, so a slot's box is the largest one that stays unambiguous:
                //
                //   x: exactly SlotSpacing. Neighbours meet edge to edge with no gap. Wider would
                //      OVERLAP the neighbouring box, and two coplanar boxes tie — Unity breaks that
                //      tie arbitrarily, so a wider box buys area at the cost of picking a random
                //      slot out of the overlap. Full pitch is the widest that is still deterministic.
                //   y: 2*RowY, so the row's box runs from its anchor out to the board's midline at
                //      y 0 and the same distance the other way. The two seats' rows meet at 0 and
                //      never cross, which is what keeps "my half / your half" a hard, seat-agnostic
                //      boundary. It overhangs the hand by 0.288 (the fan tops out at y -2.312) and
                //      overlaps the mulligan button's y span entirely — both are harmless, and only
                //      because of the z rule below. Height, not depth, is what the two enemy rows
                //      are separated by; height, not depth, is NOT what separates this from the
                //      hand or the button.
                col.size = new Vector3(ProceduralBoardSetup.SlotSpacing,
                                       ProceduralBoardSetup.RowY * 2f,
                                       0.1f);

                // Strictly behind the card root colliders (z ±0.05 at the same anchor): an exact
                // depth tie is broken arbitrarily per collider, letting the slot steal the ray
                // from the card sitting on it (attacks silently dead on the losing slots).
                //
                // At the size above this is load-bearing for two more things, not just the card:
                // hand cards (z 0, -0.03, -0.06…) and MulliganConfirmButtonView (z ±0.05) both now
                // sit INSIDE a slot box in x/y and are only reachable because they are nearer the
                // camera. InputRouter uses Physics.Raycast, which returns the closest hit, and
                // OnMouseDown likewise fires on the nearest collider — so anything that must beat a
                // slot has to be in front of z 0.15, not merely outside the row.
                //
                // RETIREMENT TRIGGER: pulling this box back toward the camera, or pushing the hand /
                // the mulligan button back past z 0.15, makes a button or a hand card unclickable
                // with no error — the #138 failure again, from the other side.
                col.center = new Vector3(0f, 0f, 0.2f);
                _col = col;
            }
        }

        public void Init(PlayerId ownerId, int slotIndex)
        {
            OwnerId   = ownerId;
            SlotIndex = slotIndex;
        }

        // An occupied slot is never a legal placement (DragTargetingModel rejects it outright), so
        // its box can only ever swallow a drop and return nothing. At the old size that box hid
        // under the minion's own card collider and cost nothing; at full pitch it sticks out well
        // past the 1.17-wide card and would tie with the empty-slot boxes behind it — half the
        // fringe around every minion would silently eat the drop instead of playing it.
        //
        // So an occupied slot stops raycasting entirely and the ray falls through to the empty slot
        // behind it, which accepts. Called every tick from BoardRowLayout.Apply, the one place that
        // knows the count; occupancy is `index < count` by the packed-position invariant.
        internal void SetOccupied(bool occupied)
        {
            if (_col != null) _col.enabled = !occupied;
        }
    }
}
