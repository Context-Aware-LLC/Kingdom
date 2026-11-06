using UnityEngine;

namespace Kingdom.Presentation
{
    // Centers a seat's occupied board slots in the row instead of packing them against the
    // leftmost anchors (Sam, 2026-08-09: "everything's jammed up against the left... we could
    // center the playing field and spread things out").
    //
    // RoyalCouncilBoard.BuildSeat lays out all 7 board anchors at fixed
    // x = (i - 3) * SlotSpacing, spanning the whole row regardless of how many minions are on the
    // board — that span is right for a full board, but BoardAnchors.AnchorFor indexes it with the
    // PACKED position (0, 1, 2, ...), so 2 minions always land on the two leftmost anchors. This
    // re-solves the SAME anchors' x position for the current count, the same idea as
    // HandFanLayout re-solving the hand fan for the current hand size — except the board row has
    // no tilt, sag or footprint compression to reproduce: the pitch never changes, only which span
    // of it is used, so centering is the whole layout.
    //
    // The arithmetic itself lives in BoardRowGeometry, not here, because since #139 the gesture
    // model has to run it BACKWARDS — a drop's x picks the position — and that model compiles
    // without Unity. See that file; this one is just the half that moves Transforms.
    //
    // OCCUPIED slots (index < count) center as a group. UNOCCUPIED slots (index >= count) are put
    // back at their ORIGINAL full-board position — not left wherever a previous, larger count put
    // them, and not collapsed onto the last occupied slot. Both alternatives were tried and
    // rejected: parking every unused slot on the last occupied one leaves no reachable empty space
    // to drop a new minion INTO, and letting the centered sequence run on unclamped pushes
    // empty-slot anchors for a near-empty board out past the side HUD (a 1-minion board would put
    // slot 6 at x=8.10, beyond the mana grid and deck pile).
    // Recomputing the ORIGINAL position fresh from `i` every call, rather than only touching
    // `i < count`, is what keeps a slot that drops OUT of the occupied range (a minion died) from
    // being stranded at a stale centered position from before it went empty.
    //
    // Only x is touched. Each anchor's y (seat side) and z were already correct at construction
    // (RoyalCouncilBoard.BuildSeat) and never depend on count, so there is nothing to re-derive
    // for them — touching them here would risk drifting from that construction over time.
    public static class BoardRowLayout
    {
        public static void Apply(Transform[] slots, int count, float spacing = BoardRowGeometry.SlotSpacing)
        {
            if (slots == null || slots.Length == 0) return;

            count = Mathf.Clamp(count, 0, slots.Length);
            float occupiedHalf = count > 1 ? (count - 1) * 0.5f : 0f;
            float fullHalf = (slots.Length - 1) * 0.5f;

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;

                float half = i < count ? occupiedHalf : fullHalf;

                Vector3 p = slots[i].position;
                slots[i].position = new Vector3(BoardRowGeometry.XForSlot(i, half, spacing), p.y, p.z);
            }
        }
    }
}
