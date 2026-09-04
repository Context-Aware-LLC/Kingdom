using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Creates the full D6 anchor hierarchy at runtime so the board works without a hand-crafted
    // Unity scene. Runs before NetworkedGameBootstrap.Start() (Awake order).
    // Attach this to the same GameObject as BoardAnchors.
    // When the scene is authoring-complete (anchors placed in editor), remove this component.
    //
    // Every bottom-edge anchor below is the hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1 budget, pinned
    // 2026-07-24 (was M1 placeholder coordinates that sat the hero cluster level with the
    // board row). §1.1 allocates SPANS on x; the element centres here are those spans minus
    // each element's own width, and the y column is §1.3 — the vertical §1.1 never wrote.
    //
    // Layout (logical board bounds are y ±4.6; BoardCameraRig fits them in perspective):
    //   Board rows:  y = ±RowY (7 slots, x = ±4.86 at BoardRowGeometry.SlotSpacing) — outside §1.1
    //   Hand rows:   fanned by HandFanLayout — fixed 6-unit footprint, count-dependent pitch
    //   The hero cluster mirrors per seat (first-person rule: your hero is on YOUR left, so the
    //   two heroes sit kitty-corner — P0 bottom-left, P1 top-right). P0 signs are (-1,-1), so
    //   seat 1 is seat 0 point-reflected through the origin, exactly as §1.1 specifies.
    [RequireComponent(typeof(BoardAnchors))]
    public sealed class ProceduralBoardSetup : MonoBehaviour
    {
        private const int   BoardSlotCount = 7;
        private const int   HandSlotCount  = 10;

        // The row's pitch used to be a constant here. It lives in BoardRowGeometry now: BoardRowLayout
        // re-solves these same anchors when the occupied count changes (Sam, 2026-08-09), and since
        // #139 DragTargetingModel runs the same arithmetic backwards to place a dropped minion — from
        // a build with no Unity assemblies in it, which is why that file cannot be this one.

        // |y| of a board row.
        //
        // MOVED OUTWARD 1.30 -> 1.39 BY #234, then 1.39 -> 1.4633 by the 2026-08-29 readability
        // pass — each following half the card-height growth so the inner bulwark edge stayed put.
        //
        // 1.4633 -> 1.41 on 2026-08-31 goes the OTHER WAY, and deliberately: the row moved BACK,
        // toward the centreline (Sam's word for it). The card grew 1.08 again and there was no
        // outward room left to follow it into — the hand is there. Stepping back hands the growth
        // to the clearance that had none, and BulwarkRowOffset 0.40 -> 0.28 pays for what that
        // costs at the centreline. The row sits between two clearances and this balances them:
        //
        //   centreline lane   RowY - BulwarkRowOffset - CardH/2      = 0.061   (two opposed
        //                                                                       bulwark rows meet)
        //   hand clearance    |TopEdgeY| - RowY - 0.42 * CardH       = 0.064   (the fan draws IN
        //                                                                       FRONT of the row,
        //                                                                       so what matters is
        //                                                                       that it does not
        //                                                                       cover the stat
        //                                                                       bosses at 0.42H)
        //
        // Both are asserted in DeviceLayout.SelfCheck against a 0.05 floor, so both are now inside
        // 0.015 of failing. Before #234 they were 0.075 and 0.319. THIS AXIS IS SPENT: the next
        // card growth has nothing left to take except the bulwark step or the hand's own size, and
        // the hand is blocked on a stale guard (see BoardController.CardW).
        // Internal, not private: DeviceLayout.SelfCheck asserts both clearances above, the same
        // reason HeroX and DeckX below are internal.
        internal const float RowY          = 1.41f;

        // Bottom (P0) is positive side, top (P1) is negative (camera looks at board from front).
        private const float P0Sign = -1f;
        private const float P1Sign =  1f;

        // ── §1.1 bottom-edge budget, as |coordinates| for seat 0 ────────────────────────
        // Signs are applied in BuildSeat: x is negated for the hero side, positive for the
        // rail/deck side, then multiplied by the seat sign.
        //
        // THE SOCKET PAIR MOVED OFF THE PORTRAIT (Sam, 2026-09-04: "these need to be outside of
        // the hero frame not top of it ... the images must not be rendering on top of each
        // other"). It used to straddle the hero's centre line at ±0.57, which put both 1.32-wide
        // frames wholly inside a 2.16-wide plate and drew them across the lower half of the hero's
        // picture. The pair keeps its internal spacing — the two rings are cut to interlock at
        // exactly 2 x SocketOffsetX and nothing about that changed — and slides INBOARD as one
        // unit until it clears the portrait's oval aperture.
        //
        // ONE RING EACH SIDE OF THE PORTRAIT DOES NOT FIT, AND THE NUMBER THAT SETTLES IT IS 0.45.
        // The oval's outer edge is |x| 7.583 and the design box ends at 7.88, so there are 0.45
        // units of board outboard of the hero's picture and a ring is 1.32 wide. Widening the box
        // is not available either: DesignHalfWidth/DesignHalfHeight IS MinAspect, so spending
        // width there narrows the range of screens the layout is guaranteed on, and 16:9 is inside
        // it. So an outboard ring can only be bought by moving the hero inboard, and the exposed
        // width it gains is taken one-for-one out of the board row's outermost slot — a 7-minion
        // row already reaches |x| 5.618 against a plate edge at 5.72. Both rings inboard costs
        // nothing instead, because the lane between the hand fan and the portrait was empty.
        //
        // THE LANE, and how the pair is centred in it:
        //     hand fan's widest reach   |x| 3.325   (Footprint 6.0 -> outer centre 2.157,
        //                                            plus a MaxTiltDeg corner at 1.168)
        //     portrait oval inner edge  |x| 6.017   (HeroX - PortraitW/2)
        //     lane 2.692 wide, pair 2.46 wide (2 x SocketOffsetX + FrameSize) -> 0.116 each side
        // giving inner ring inner edge 3.44 and outer ring outer edge 5.90. The outer ring
        // overlaps the PLATE by 0.18 and clears the PICTURE by 0.12, which is the distinction Sam
        // drew: metal may touch metal.
        //
        // RETIREMENT TRIGGER: HeroPlateView.PortraitW/PortraitH, HandFanLayout.Footprint/MaxTiltDeg
        // and HeroSocketsView.FrameSize are all inputs to that centring. Change any of them and
        // re-solve SocketPairInboard here; nothing checks the two clearances above.
        internal const float SocketOffsetX = 0.57f;
        internal const float SocketPairInboard = 2.13f;

        // 6.65 -> 6.80 so the PLATE lands exactly on the design edge. The outboard socket used to
        // own that edge and does not exist out there any more, so without this the widest built
        // element would be 7.73 and DeviceLayout.DesignHalfWidth would have had to follow it —
        // rescaling the entire board by 1.9% for a hero-frame fix. The 0.15 also buys the lane
        // above its margin. Kept a LITERAL: deriving it from DesignHalfWidth would make
        // DeviceLayout.SelfCheck's "design half-width == the widest thing the board builds"
        // assertion tautological, and that assertion is this block's retirement trigger.
        internal const float HeroX = 6.80f;
        internal const float HeroClusterOuterEdge = HeroX + HeroPlateView.PlateW * 0.5f;

        // Two columns of five mana orbs stand inboard of the deck pile. The 0.31-unit gap from
        // the grid's outer edge at 6.22 to the pile's inner edge at 6.53 keeps them distinct.
        internal const float ManaGridX = 5.76f;
        internal const float DeckX     = 7.18f;

        // ── Vertical (§1.3) ─────────────────────────────────────────────────────────────
        // §1.1 is an x-budget only. Everything in it rests on one baseline 0.15 above the
        // screen edge: |y| = 4.45. Each centre below is that baseline plus half the element's
        // own height, so moving the baseline moves the whole bottom row together.
        private const float Baseline = 4.45f; // frame crown/bottom remains 0.15 inside y +/-4.6
        internal const float HeroY = Baseline - HeroPlateView.PlateH * 0.5f;

        // How far below the hero's centre the pair rides. Unchanged by the 2026-09-04 move, and it
        // is what keeps the pair out of the BOARD ROW now that it sits inboard rather than tucked
        // under the portrait: the rings span |y| 2.86..4.18 and a minion reaches |y| 2.479
        // (RowY + CardH/2), so the clearance is 0.38. Raising this spends that.
        //
        // Screen-down is deliberate rather than multiplied by the seat sign: every portrait is
        // upright, so the opponent's rings ride toward the battlefield while the local ones ride
        // toward the bottom edge.
        internal const float SocketDrop = 0.69f;
        internal const float ManaGridY = 3.35f;
        private const float DeckY      = 3.53f;  // a 1.3-wide card-proportioned pile (1.83
                                                 // tall) on the baseline. DeckPileView renders
                                                 // here — NetworkedGameBootstrap builds one per
                                                 // seat — and DeckAnchor is the draw source

        private void Awake()
        {
            var anchors = GetComponent<BoardAnchors>();
            BuildSeat(anchors.p0, "P0", P0Sign, xSign: -1f);
            BuildSeat(anchors.p1, "P1", P1Sign, xSign: +1f);
            BoardBackdrop.Create(transform);
            AttachBoardLighting(transform);
            AttachSafeAreaCamera();
        }

        private static void AttachBoardLighting(Transform parent)
        {
            if (parent == null || parent.Find("Board Key Light") != null) return;

            var go = new GameObject("Board Key Light");
            go.transform.SetParent(parent, false);
            go.transform.rotation = Quaternion.Euler(38f, -32f, 0f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.78f, 0.55f);
            light.intensity = 1.35f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.52f;
            light.shadowBias = 0.025f;
            light.shadowNormalBias = 0.18f;
        }

        // EF-062. The safe-area solve is attached here rather than authored into the scene:
        // every board scene builds its board through this component, so one call covers all of
        // them and no scene can drift from the code. Must run AFTER BoardBackdrop.Create —
        // SafeAreaCamera.Awake rescales the plate, and there has to be a plate to rescale.
        private static void AttachSafeAreaCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[ProceduralBoardSetup] no MainCamera — safe-area insets "
                                 + "will not be applied (EF-062).");
                return;
            }

            BoardCameraRig.Configure(cam);
            if (cam.GetComponent<SafeAreaCamera>() == null)
                cam.gameObject.AddComponent<SafeAreaCamera>();
        }

        private void BuildSeat(SeatAnchors seat, string prefix, float ySign, float xSign)
        {
            seat.BoardSlots = new Transform[BoardSlotCount];
            for (int i = 0; i < BoardSlotCount; i++)
            {
                float x = BoardRowGeometry.XForSlot(i, (BoardSlotCount - 1) * 0.5f);
                seat.BoardSlots[i] = MakeAnchor($"{prefix}_Board{i}", new Vector3(x, ySign * RowY, 0f));
            }

            // Hand slots are placed by HandFanLayout, not by this method's flat pitch: the fan
            // re-solves all ten every time the hand count changes (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8). Built
            // here at full count so the anchors are never in a stale row before the first sync.
            seat.FanMirrored = ySign > 0f;
            seat.HandSlots   = new Transform[HandSlotCount];
            for (int i = 0; i < HandSlotCount; i++)
                seat.HandSlots[i] = MakeAnchor($"{prefix}_Hand{i}", Vector3.zero);
            HandFanLayout.Apply(seat.HandSlots, HandSlotCount, seat.FanMirrored);

            seat.HeroAnchor = MakeAnchor($"{prefix}_Hero",
                new Vector3(xSign * HeroX, ySign * HeroY, 0f));
            // SCREEN-RIGHT / SCREEN-LEFT, NOT INBOARD / OUTBOARD. The x offset is deliberately NOT
            // multiplied by xSign, for the same reason SocketDrop is not multiplied by ySign: the
            // sprites are upright and unmirrored, so their left and right are screen-absolute while
            // the seats are mirrored (p0 xSign -1, p1 xSign +1).
            //
            // WHICH SIDE IS WHICH IS DECIDED BY THE ART, not by taste. The two rings are cut as a
            // MIRRORED PAIR: the hero power's molten wreath sits on its lower-RIGHT with its rivets
            // on the left, the weapon's blades sit on its LEFT with its rivets on the right. The
            // sockets overlap (centres 2 x SocketOffsetX apart, frames FrameSize wide), so there is
            // exactly one arrangement in which they interlock — ornament flaring outward on both
            // sides, rivets meeting in the middle. Swap them and the weapon's blades cut straight
            // across the power ring's aperture. Rendered both ways before choosing (2026-09-03).
            //
            // The 2026-09-04 move slid the interlocked pair inboard as one unit and did not touch
            // this: the pair's internal arrangement is the same, only where it sits changed.
            //
            // RETIREMENT TRIGGER: if either socket ring is ever re-cut symmetrical, or mirrored
            // per seat, this stops being a constraint and the offset can go back to xSign.
            // SocketPairInboard IS multiplied by xSign and SocketOffsetX is NOT, and that pair of
            // decisions is the whole placement. The shift moves the pair toward the board's centre
            // line, which is a different direction per seat; the ±offset separates the two rings,
            // which is a screen-absolute arrangement decided by the art. Multiply the wrong one and
            // one seat's pair lands under its own portrait again.
            float pairCentreX = xSign * (HeroX - SocketPairInboard);
            seat.HeroPowerAnchor = MakeAnchor($"{prefix}_HeroPower",
                new Vector3(pairCentreX + SocketOffsetX, ySign * HeroY - SocketDrop, 0f));
            seat.WeaponAnchor = MakeAnchor($"{prefix}_Weapon",
                new Vector3(pairCentreX - SocketOffsetX, ySign * HeroY - SocketDrop, 0f));
            seat.ManaTrayAnchor = MakeAnchor($"{prefix}_ManaTray",
                new Vector3(xSign * -ManaGridX, ySign * ManaGridY, 0f));
            seat.DeckAnchor      = MakeAnchor($"{prefix}_Deck",      new Vector3(xSign * -DeckX,     ySign * DeckY,      0f));
        }

        // Called from NetworkedGameBootstrap's boot after SetPlayers() — attaches the ClickableHeroPower /
        // ClickableHero raycast descriptors so InputRouter's press/drag/release machine can identify
        // what the pointer is over (EF-048). Both seats get them: ownership is read off the
        // descriptor, not implied by which ones exist, and the enemy side has to be identifiable to
        // be a drag TARGET.
        //
        // BOARD SLOTS GET NOTHING (Sam, 2026-08-12, #139). Every anchor used to receive a
        // ClickableBoardSlot with an invisible box collider, and dropping a minion inside one was
        // the only way to play it. Placement is solved from the drop's x now
        // (BoardRowLayout.InsertIndexForX), so the boxes, the component and the PointerSubjectKind
        // are all gone. The anchors themselves stay — they are where cards are PARKED, which was
        // always a separate job from being hit.
        //
        // `router` is only a mode flag now (null = AI-vs-AI, nothing to point at). The descriptors
        // no longer hold a reference to it — InputRouter raycasts and reads them.
        public void SetupClickables(PlayerId p0Id, PlayerId p1Id, InputRouter router)
        {
            if (router == null) return;
            var anchors = GetComponent<BoardAnchors>();
            AttachSeatClickables(anchors.p0, p0Id);
            AttachSeatClickables(anchors.p1, p1Id);
        }

        private static void AttachSeatClickables(SeatAnchors seat, PlayerId ownerId)
        {
            if (seat.HeroPowerAnchor != null)
            {
                var hp = seat.HeroPowerAnchor.gameObject.AddComponent<ClickableHeroPower>();
                hp.Init(ownerId);
            }

            if (seat.HeroAnchor != null)
            {
                var hero = seat.HeroAnchor.gameObject.AddComponent<ClickableHero>();
                hero.Init(ownerId);
            }
        }

        private Transform MakeAnchor(string anchorName, Vector3 worldPos)
        {
            var go = new GameObject(anchorName);
            go.transform.SetParent(transform, false);
            go.transform.position = worldPos;
            return go.transform;
        }
    }
}
