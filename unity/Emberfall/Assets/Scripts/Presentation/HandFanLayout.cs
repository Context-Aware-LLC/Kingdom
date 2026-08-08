using UnityEngine;

namespace Emberfall.Presentation
{
    // Positions the ten hand anchors as a fan whose footprint is FIXED
    // (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1): hand count changes overlap, never span.
    //
    // The spec's table is an x-budget only — it describes a straight row. Tilt, sag and the
    // vertical seat are the additions, all Sam's starting values (2026-07-24) and all meant
    // to be dialled by eye:
    //
    //   x        pitch = (Footprint - CardW) / (count - 1), centred on 0  [§1.1, locked]
    //   tilt     ±MaxTiltDeg at the outermost card, linear across the hand
    //   sag      centre card highest, edges dropping on a parabola
    //   y        CentreY sits the fan so roughly the bottom 15% of the centre card is below the
    //            screen edge, exposing the rules band while keeping the tactile "held in hand" feel
    //   z        a per-index step toward the camera, so overlapping cards have a defined
    //            draw order even where the material is opaque, and so Unity's OnMouseDown —
    //            which fires on the NEAREST collider — resolves overlap topmost-wins (§8)
    //
    // Seat 1 is this layout point-reflected through the origin, exactly as §1.1 specifies:
    // (x, y, rot) -> (-x, -y, rot). A point reflection is a 180° rotation, so each card's
    // own tilt is unchanged by it.
    // Public, not internal: DeckPilePreview (Emberfall.Editor, across an asmdef boundary) solves
    // the REAL fan to place the draw flight's destination. A preview that guessed where a hand
    // slot is would be judging the animation against a made-up target.
    public static class HandFanLayout
    {
        // §1.1: the fan owns x -2.5 .. +2.5. Fan width and mana-orb size are ONE budget —
        // every unit taken here comes out of the rail. Do not widen without re-checking it.
        internal const float Footprint = 5.0f;

        // §1.1 fixes the footprint so hand count changes overlap, not span. That holds from 5
        // cards up; below it the rule inverts — pitch exceeds the card width and a 2-card hand
        // would sit 3.83 units apart at opposite ends of an empty span. So the footprint is a
        // MAXIMUM: the pitch caps just inside the card width, keeping the fan always slightly
        // overlapped, and the span shrinks under ~5 cards instead of the cards pulling apart.
        internal const float MaxPitch = 1.05f;    // ≈10% overlap; bites only at 4 cards or fewer

        // The footprint positions the card TOPS, not their centres (Sam, 2026-07-24: "bring the
        // bottom of the fan together more, leave the top pretty much the way it's laid out").
        // Rotation is about each card's own centre, so tilting alone would swing the top outward
        // as far as it draws the bottom in. Solving for the centre from a fixed top edge instead
        // makes tilt behave like a real held fan — a pivot below the hand — and makes MaxTiltDeg
        // a pure "how closed is the bottom" dial that leaves the top spread untouched.
        internal const float MaxTiltDeg = 18f;    // outermost card's lean
        internal const float Sag        = 0.18f;  // edge cards' drop below the centre card
        // |y| of the centre card. Ortho size 4.6, so the screen edge is y -4.6 and the fraction of
        // the centre card hidden below it is (CentreY - 3.775) / CardH. Was 30% (4.27); raised to
        // expose the rules band — the sticky reveal panel is the legibility path, not the hand card
        // face (NOR-147). ~15% hidden: 0.15 * 1.65 + 3.775 = 4.02.
        internal const float CentreY    = 4.02f;
        internal const float ZStep      = 0.03f;  // per-index step toward the camera

        // Lays out every slot in the array for a hand of `count`. Slots beyond `count` park on
        // the last live position — nothing resolves them, but leaving them at a stale pitch
        // would strand a card mid-screen if one ever did.
        public static void Apply(Transform[] slots, int count, bool mirrored)
        {
            if (slots == null || slots.Length == 0) return;

            count = Mathf.Clamp(count, 0, slots.Length);
            if (count == 0) return;

            float half  = (count - 1) * 0.5f;
            float pitch = count > 1
                ? Mathf.Min((Footprint - BoardController.CardW) / (count - 1), MaxPitch)
                : 0f;

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;

                int   index  = Mathf.Min(i, count - 1);
                float offset = index - half;                    // -half .. +half
                float t      = half > 0f ? offset / half : 0f;  // -1 .. +1

                float rot   = -MaxTiltDeg * t;                  // left card leans left
                float halfH = BoardController.CardH * 0.5f;

                // topX is the laid-out position; solve back for the centre the rotation needs.
                // topX = x - halfH*sin(rot)  =>  x = topX + halfH*sin(rot).
                float topX = offset * pitch;
                float x    = topX + halfH * Mathf.Sin(rot * Mathf.Deg2Rad);
                float y    = -(CentreY + Sag * t * t);

                // Anchors are authored in world space (ProceduralBoardSetup.MakeAnchor), so
                // stay in world space here rather than mixing conventions.
                slots[i].position = mirrored
                    ? new Vector3(-x, -y, -ZStep * index)
                    : new Vector3( x,  y, -ZStep * index);
                slots[i].rotation = Quaternion.Euler(0f, 0f, rot);
            }
        }
    }
}
