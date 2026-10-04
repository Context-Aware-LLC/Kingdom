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
    //   x        pitch = (Footprint - HandCardW) / (count - 1), centred on 0  [§1.1, locked]
    //   tilt     ±MaxTiltDeg at the outermost card, linear across the hand
    //   sag      centre card highest, edges dropping on a parabola
    //   y        CentreY seats the fan so the whole RULES BAND clears the bottom screen edge at
    //            rest — see CentreY below; the hidden fraction falls out of that, it is not a dial
    //   z        a per-index step toward the camera, so overlapping cards have a defined
    //            draw order even where the material is opaque, and so a raycast — which
    //            takes the NEAREST collider — resolves overlap topmost-wins (§8)
    //
    // Seat 1 is this layout point-reflected through the origin, exactly as §1.1 specifies:
    // (x, y, rot) -> (-x, -y, rot). A point reflection is a 180° rotation, so each card's
    // own tilt is unchanged by it.
    // Public, not internal: DeckPilePreview (Emberfall.Editor, across an asmdef boundary) solves
    // the REAL fan to place the draw flight's destination. A preview that guessed where a hand
    // slot is would be judging the animation against a made-up target.
    public static class HandFanLayout
    {
        // The fan owns x -3.0 .. +3.0. This was widened from 5.0 to 6.0 after the redesigned
        // match HUD made the hand read undersized beside the new hero treatment (Sam, 2026-08-29).
        // Fan width and mana-orb size are ONE budget — every unit taken here comes out of the rail.
        internal const float Footprint = 6.0f;

        // A CARD IN HAND IS DRAWN LARGER THAN THE SAME CARD ON THE BOARD (EF-062 / NOR-244).
        //
        // WHY. Before NOR-244 a hand card was drawn at the board card's size, which was then
        // 137.3 x 193.7 px on every 1080p phone — 52% of the 265 px collection
        // tile EF-050's 1.6x acuity margin was measured against. Re-measured at that true size,
        // rules text at BodyEmFloor (the common case: anything longer than one short line
        // auto-shrinks to it) reaches only 0.74x the recognition threshold. NO TYPEFACE CHOICE BUYS
        // BACK A FACTOR OF 1.35 — ONLY SIZE DOES, which is why EF-062 raised this as a finding
        // rather than re-opening the font ticket.
        //
        // WHY THE RATIO IS 1.436-ISH AND NOT 1.35. 1/0.74 = 1.348 clears threshold at the AUTHORED
        // camera only.
        // SafeAreaCamera raises orthographicSize to lift the board off the Android gesture bar,
        // shrinking everything on screen by 6.1% (24 dp gesture bar) to 12.2% (48 dp three-button
        // bar). 1.348 / 0.939 = 1.436 holds 1.0x through the gesture-bar case, the modern default.
        // The 48 dp case lands at 0.94x — a KNOWN, QUANTIFIED RESIDUAL, not an oversight: closing
        // it needs 1.54x, which drives the hand up into the board row.
        //
        // Run `python tooling/scripts/device_layout.py` and read §5 and §6 of the stdout. Those
        // numbers are derived from THIS constant; the ones in this comment are a copy and can rot.
        //
        // Public, for the same reason Apply() is: DeckPilePreview (Emberfall.Editor, across an
        // asmdef boundary) draws the draw-flight's destination card, and a preview that guessed the
        // size of a hand slot would be judging the animation against a made-up target.
        //
        // WHY 1.20 AND NOT 1.44 (#234 + 2026-08-29 readability pass). THIS IS NOT A REDUCTION IN
        // THE SIZE OF A CARD IN HAND. The multiplicand has now moved three times:
        // BoardController.CardW/CardH grew by 1/0.9, then 1.08, then 1.08 again, and this fell by
        // the inverse factor each time to hold the product where it was. HandCardW/H below remain
        // 1.6848 x 2.3760 — every number in the block above still describes the card this
        // produces. What changed is the board card.
        //
        // So the hand:board RATIO is 1.111 now, down from 1.44. The gap keeps closing from the
        // board end because the hand end is pinned by an acuity solve.
        //
        // THE HAND WAS ASKED TO GROW ON 2026-08-31 AND COULD NOT. Not for want of room on screen —
        // measured against the live perspective rig the worst-sag card still sits 93 px above the
        // bottom edge at 1.15x. It is DeviceLayout.SelfCheck assertion 7 that refuses, and that
        // assertion bounds the band against DesignHalfHeight 4.6, the ORTHOGRAPHIC half-height, on
        // a camera that has been perspective since f121a591. Fix the guard first; see
        // BoardController.CardW.
        //
        // RETIREMENT TRIGGER: this constant is the hand's whole legibility budget, and since #234 it
        // is one number spread over two files. Moving it, BoardController.CardW/CardH, or
        // FrameRarityCatalog.BodyEmFloor invalidates DeviceLayout.SelfCheck's band-clearance
        // assertion and CentreY's solve below — re-solve CentreY in the same commit. NOT
        // device_layout.py: it reads `orthographic size` from the scene YAML and reports a camera
        // the game does not use. Moving THIS without the compensating move on CardW/CardH (or the
        // reverse) resizes the card in hand, which is the one thing the #234 change was built not
        // to do.
        public const float RestScale = 1.111111f;  // 1.20 / 1.08; hand stays 1.6848 x 2.376

        // The card's DRAWN size in hand. Every card-relative number below derives from these and
        // NOT from BoardController.CardW/CardH — a fan solved against the board-size card would
        // space an enlarged card at the board card's pitch and pivot its tilt about the wrong point.
        public const float HandCardW = BoardController.CardW * RestScale;   // 1.6848
        public const float HandCardH = BoardController.CardH * RestScale;   // 2.3760

        // §1.1 fixes the footprint so hand count changes overlap, not span. That holds from 5
        // cards up; below it the rule inverts — pitch exceeds the card width and a 2-card hand
        // would sit 3.83 units apart at opposite ends of an empty span. So the footprint is a
        // MAXIMUM: the pitch caps just inside the card width, keeping the fan always slightly
        // overlapped, and the span shrinks under ~5 cards instead of the cards pulling apart.
        // Scaled with the card (was 1.05 against a 1.17-wide card) so "just inside the card width"
        // still means ~10% overlap; against HandCardW it bites at 3 cards or fewer rather than 4.
        // Unmoved by #234 — HandCardW is unmoved by #234.
        internal const float MaxPitch = 1.51f;    // ≈10% overlap of HandCardW 1.6848

        // The footprint positions the card TOPS, not their centres (Sam, 2026-07-24: "bring the
        // bottom of the fan together more, leave the top pretty much the way it's laid out").
        // Rotation is about each card's own centre, so tilting alone would swing the top outward
        // as far as it draws the bottom in. Solving for the centre from a fixed top edge instead
        // makes tilt behave like a real held fan — a pivot below the hand — and makes MaxTiltDeg
        // a pure "how closed is the bottom" dial that leaves the top spread untouched.
        internal const float MaxTiltDeg = 18f;    // outermost card's lean
        // Edge cards' drop below the centre card. Scaled with the card (was 0.18 against a
        // 1.65-tall card) so the arc keeps its shape instead of flattening as the card grows.
        // Unmoved by #234: this is a fraction of HandCardH, and HandCardH did not move.
        internal const float Sag        = 0.26f;
        // |y| of the centre card. Ortho size 4.6, so the screen edge is y -4.6.
        //
        // THIS IS NO LONGER A "HOW MUCH HANGS OFF THE BOTTOM" DIAL. It was one twice: Sam set 30%
        // (4.27, 2026-07-24) when §8 had accepted top-half-only legibility in hand, and NOR-147
        // raised it to ~15% (4.02) while treating the sticky reveal panel as the legibility path.
        // Sam then declined that framing (2026-08-07) and ruled that the RESTING card carries the
        // legibility requirement — so the seat is now solved from the rules band, and the hidden
        // fraction is whatever falls out of that solve.
        //
        // It has to be solved, not nudged: EF-062 measured the band's centre at y -4.600 against a
        // screen edge at -4.60, and enlarging the card by RestScale makes a taller card hang LOWER,
        // so 4.02 with a 1.44x card would bury the band 0.33 below the edge. Scaling the text buys
        // nothing while the text is off-screen.
        //
        // Solve, against the worst card in the fan (the outermost — it carries the full Sag):
        //   band bottom = -(CentreY + Sag) - 0.305 * HandCardH   [CardPresenter seats the band
        //                 centre 0.20 CardH below the card centre with half-height 0.105 CardH]
        //   require 0.05 of clearance, which is also where the safe-area boundary sits:
        //     CentreY <= 4.6 - 0.05 - 0.26 - 0.305*2.376 = 3.565.
        //
        // What falls out at 3.56: the centre card hides 6.2% of its height and the outermost 17.1%.
        // The card top rises to y -2.372, leaving the board row's stat bosses unobscured
        // — the hand overlaps the frame skirt of your own minions and nothing they carry: their
        // stat bosses sit at -1.993 and their own rules bands bottom out at -1.803.
        //
        // RESIDUAL, stated rather than hidden: the outermost card is also tilted 18°, dropping its
        // band's outer CORNER a further 0.157, to 0.04 below the edge. That is a corner of the text
        // box on the most-overlapped card in the fan; closing it needs CentreY 3.41, at which point
        // the centre card stops overhanging the edge at all and the hand stops reading as held.
        // RETIREMENT TRIGGER: MulliganConfirmButtonView.BoardPosition reasons about the span this
        // constant produces (the fan reaching y -2.372) to justify sitting clear of it, and TopEdgeY
        // below turns that same span into the line a drop must clear to count as a play. Moving
        // CentreY or the card height means re-checking both — #138 was a confirm button buried
        // inside the fan, which fails silently and reads as a dead button, not a hidden one.
        internal const float CentreY    = 3.56f;

        // THE LINE BETWEEN "STILL IN MY HAND" AND "ON THE BOARD" (Sam, 2026-08-12, #139).
        //
        // The highest point any resting hand card reaches: the centre card's top edge, since Sag
        // only ever pushes the outer cards DOWN. A release above this is a play — wherever it is,
        // whosever half of the board it is over. A release below it is the one and only cancel:
        // you put the card back. That is the whole rule, and it is a LINE rather than a set of
        // colliders on purpose (see DragTargetingModel.DropPoint).
        //
        // Local seat only, which is all this is ever asked about: the local player is always the
        // un-mirrored bottom seat (ProceduralBoardSetup.BuildSeat, P0Sign -1).
        internal const float TopEdgeY   = -CentreY + HandCardH * 0.5f;   // -2.372

        // Hand cards step TOWARD the camera as the index rises, so a later card wins the raycast
        // against anything parked at z 0. That is what ate the mulligan confirm button (#138).
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
                ? Mathf.Min((Footprint - HandCardW) / (count - 1), MaxPitch)
                : 0f;

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;

                int   index  = Mathf.Min(i, count - 1);
                float offset = index - half;                    // -half .. +half
                float t      = half > 0f ? offset / half : 0f;  // -1 .. +1

                float rot   = -MaxTiltDeg * t;                  // left card leans left
                float halfH = HandCardH * 0.5f;                 // the DRAWN half-height, so the
                                                                // tilt pivot below the hand scales
                                                                // with the card

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
