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
    // Layout (orthographic camera size 4.6, so the screen edges are y ±4.6, world units):
    //   Board rows:  y = ±1.39  (7 slots, x = -4.50..+4.50 at 1.50 spacing) — outside §1.1
    //   Hand rows:   fanned by HandFanLayout — fixed 5-unit footprint, count-dependent pitch
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
        // MOVED OUTWARD 1.30 -> 1.39 BY #234, and it had to move: the card grew by 1/0.9 about this
        // centre, so holding 1.30 would have pushed a bulwarked minion (which steps 0.40 further in)
        // 0.04 ACROSS the centreline and into the opposing seat's front row. The row sits between
        // two clearances and this is the value that balances them:
        //
        //   centreline lane   RowY - BulwarkRowOffset - CardH/2      = 0.073   (two opposed
        //                                                                       bulwark rows meet)
        //   hand clearance    |TopEdgeY| - RowY - 0.42 * CardH       = 0.152   (the fan draws IN
        //                                                                       FRONT of the row,
        //                                                                       so what matters is
        //                                                                       that it does not
        //                                                                       cover the stat
        //                                                                       bosses at 0.42H)
        //
        // Both are asserted in DeviceLayout.SelfCheck. Before #234 they were 0.075 and 0.319; the
        // card spent the slack in the second, which is the one with room, and left the first alone.
        // Internal, not private: DeviceLayout.SelfCheck asserts both clearances above, the same
        // reason HeroX and DeckX below are internal.
        internal const float RowY          = 1.39f;

        // Bottom (P0) is positive side, top (P1) is negative (camera looks at board from front).
        private const float P0Sign = -1f;
        private const float P1Sign =  1f;

        // ── §1.1 bottom-edge budget, as |coordinates| for seat 0 ────────────────────────
        // Signs are applied in BuildSeat: x is negated for the hero side, positive for the
        // rail/deck side, then multiplied by the seat sign.
        //
        // Hero cluster owns x -7.88..-4.98 (2.9). Portrait outer against the margin, the
        // two-disc column inboard toward the hand (Sam, 2026-07-24).
        internal const float HeroX     = 6.98f;  // 1.8-wide plate centred in -7.88..-6.08
        private const float DiscX      = 5.53f;  // socket column centred in -6.08..-4.98; the
                                                 // 1.008 rings span -6.03..-5.03 inside it

        // Mana rail owns +2.5..+6.53 (4.03); deck pile +6.53..+7.83 (1.3).
        private const float ManaRailX  = 4.515f;
        internal const float DeckX     = 7.18f;

        // ── Vertical (§1.3) ─────────────────────────────────────────────────────────────
        // §1.1 is an x-budget only. Everything in it rests on one baseline 0.15 above the
        // screen edge: |y| = 4.45. Each centre below is that baseline plus half the element's
        // own height, so moving the baseline moves the whole bottom row together.
        private const float Baseline   = 4.45f;  // 0.15 above the screen edge at 4.6
        private const float HeroY      = 3.55f;  // 1.8-tall plate on the baseline

        // The sockets DOCK SIDE BY SIDE, hero power then weapon, both on the baseline — never
        // stacked. Stacking is what the two 0.9Ø discs did, and two rings in that column would
        // have to shrink to fit the plate's 1.8 height (Sam, 2026-07-25). Docked, height is not
        // the constraint at all: the rings clear the board row, whose cards top out near 2.2.
        //
        // DERIVED, not independent constants — hand-picked values stop touching the moment the
        // frame size changes. Budget by the RING, not the frame canvas: the canvas carries 34%
        // transparent margin so the stat bosses can overhang, so 1.35 draws a ring of ~1.008.
        // The docked pair reaches ~1 unit inboard of the old disc column, past §1.1's hero-cluster
        // budget. IF IT EVER NEEDS MORE ROOM, TAKE IT FROM THE HAND — tighten HandFanLayout's
        // pitch, do not shrink the portrait or the rings (Sam, 2026-07-25).
        private const float RingD      = HeroSocketsView.FrameSize * HeroSocketsView.RingFraction;
        private const float SocketY    = Baseline - RingD * 0.5f;   // both rings on the baseline
        private const float WeaponX    = DiscX - RingD;             // docked inboard, rings touching
        private const float ManaRailY  = 4.00f;  // aligned to the hero-power disc, not the
                                                 // baseline: the two read as one bottom line
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
            AttachSafeAreaCamera();
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

            seat.HeroAnchor      = MakeAnchor($"{prefix}_Hero",      new Vector3(xSign *  HeroX,     ySign * HeroY,      0f));
            seat.HeroPowerAnchor = MakeAnchor($"{prefix}_HeroPower", new Vector3(xSign *  DiscX,     ySign * SocketY,    0f));
            seat.WeaponAnchor    = MakeAnchor($"{prefix}_Weapon",    new Vector3(xSign *  WeaponX,   ySign * SocketY,    0f));
            seat.ManaTrayAnchor  = MakeAnchor($"{prefix}_ManaTray",  new Vector3(xSign * -ManaRailX, ySign * ManaRailY,  0f));
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
