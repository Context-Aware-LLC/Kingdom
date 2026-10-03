using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Creates the full D6 anchor hierarchy at runtime so the board works without a hand-crafted
    // Unity scene. Runs before NetworkedGameBootstrap.Start() (Awake order).
    // Attach this to the same GameObject as BoardAnchors.
    // When the scene is authoring-complete (anchors placed in editor), remove this component.
    //
    // Layout (logical board bounds are y ±4.6; BoardCameraRig fits them in perspective):
    //   Board rows:  y = ±RowY (7 slots, x = ±4.86 at BoardRowGeometry.SlotSpacing) — outside §1.1
    //   Hand rows:   fanned by HandFanLayout — fixed 6-unit footprint, count-dependent pitch
    //   Hero and resource cluster ROOTS: registered to measured BoardPlate artwork UVs, because
    //   the generated table's fixtures are not a point-reflected pair and its cover fit moves in
    //   world space with aspect ratio. Children keep the approved 180-degree seat arrangement.
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

        // Fixture centres in royal_council_battle_table_v2_composite.png (1867 x 842, the 4612 x
        // 2080 master at 1/2.47): the refined_v3 measurements carried through the same warp that
        // seats the table in the hall scene, not re-eyeballed. UVs start at bottom-left.
        // BoardBackdrop projects these through the fitted artwork plane, so the
        // clusters remain seated in their painted furniture at every aspect and safe-area inset.
        // P0's hero is nudged off its socket centre (171, 618) up and inboard, twice as far
        // across as up (Sam, 2026-10-02): the camera's crop cut the portrait at the left edge.
        private static readonly Vector2 P0HeroFixtureUv =
            new(251f / 1867f, 1f - 578f / 842f);
        private static readonly Vector2 P1HeroFixtureUv =
            new(1508f / 1867f, 1f - 169f / 842f);
        private static readonly Vector2 P0ResourceFixtureUv =
            new(1669f / 1867f, 1f - 619f / 842f);
        private static readonly Vector2 P1ResourceFixtureUv =
            new(399f / 1867f, 1f - 151f / 842f);

        // ── Royal Council fixture-local cluster geometry ───────────────────────────────
        // The Royal Council board keeps both circular satellites on the portrait's INBOARD side
        // (Sam, 2026-09-30). The pair is rotationally symmetric by seat: local power upper-right
        // and weapon lower-right; opponent power lower-left and weapon upper-left. This removes the
        // old outboard ring that competed with the frame edge and the board's timer furniture.
        //
        // The hero-power ring retains the approved 0.34-unit metal-on-metal overlap. The weapon
        // uses a smaller quad overlap because its painted metal is left-heavy. Both x offsets derive
        // from the hero width, so enlarging a portrait does not bury either satellite in it. Their
        // offset delta remains 0.235 and their 1.568 centre distance keeps the 1.51 click targets
        // distinct.
        private const float HeroPowerOverlap = 0.34f;
        private const float WeaponQuadOverlap = 0.105f;
        internal const float HeroPowerOffsetX =
            HeroPlateView.PlateW * 0.5f + HeroSocketsView.FrameSize * 0.5f - HeroPowerOverlap;
        internal const float WeaponOffsetX =
            HeroPlateView.PlateW * 0.5f + HeroSocketsView.FrameSize * 0.5f - WeaponQuadOverlap;

        // Fallback placement before BoardBackdrop has fitted the artwork. The live hero-cluster
        // roots are registered to the UV fixtures above and replace this position immediately.
        internal const float HeroX = 6.65f;

        // Fallback resource-cluster placement. The mana rail keeps this offset from the deck when
        // the cluster root is projected onto the painted deck cradle.
        internal const float ManaGridX = 6.83f;   // rail 6.37..7.29
        internal const float DeckX     = 8.175f;  // resized pile fallback still ends at 8.90

        // The pile renders at 2x its footprint (DeckPileView.RenderW 4.24, half-width 2.12), and the
        // rail at DeckX - ManaGridX (1.345) from it sat entirely under the stack. Another 1.5 rail
        // widths inboard clears it (Sam, 2026-10-02): the rail now spans 2.27..3.19 from the pile.
        private const float ManaClearOfPileX = 1.5f * ManaTrayView.RailW;

        // ── Vertical (§1.3) ─────────────────────────────────────────────────────────────
        // Fallback baseline used until the artwork fit projects each fixture root.
        private const float Baseline = 4.45f; // frame crown/bottom remains 0.15 inside y +/-4.6
        internal const float HeroY = Baseline - HeroPlateView.PlateH * 0.5f;

        // The power sits toward the battlefield; the optional weapon sits toward the outer screen
        // edge. Multiplying both by ySign rotates the complete cluster 180 degrees for the opponent.
        internal const float HeroPowerTowardCenterY = 0.55f;
        internal const float WeaponTowardEdgeY = 1.00f;
        internal const float ManaGridY = 3.35f;
        private const float DeckY      = 3.59f;  // resized pile fallback remains on the baseline.
                                                // The live root is artwork-registered; DeckAnchor
                                                // remains the draw-flight source in either case.

        private void Awake()
        {
            var anchors = GetComponent<BoardAnchors>();
            BuildSeat(anchors.p0, "P0", P0Sign, xSign: -1f,
                out Transform p0HeroCluster, out Transform p0ResourceCluster);
            BuildSeat(anchors.p1, "P1", P1Sign, xSign: +1f,
                out Transform p1HeroCluster, out Transform p1ResourceCluster);
            BoardBackdrop.Create(transform);
            BoardBackdrop.RegisterArtworkAnchor(p0HeroCluster, P0HeroFixtureUv);
            BoardBackdrop.RegisterArtworkAnchor(p1HeroCluster, P1HeroFixtureUv);
            BoardBackdrop.RegisterArtworkAnchor(p0ResourceCluster, P0ResourceFixtureUv);
            BoardBackdrop.RegisterArtworkAnchor(p1ResourceCluster, P1ResourceFixtureUv);
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

        private void BuildSeat(
            SeatAnchors seat,
            string prefix,
            float ySign,
            float xSign,
            out Transform heroCluster,
            out Transform resourceCluster)
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

            heroCluster = MakeAnchor($"{prefix}_HeroCluster",
                new Vector3(xSign * HeroX, ySign * HeroY, 0f));
            seat.HeroAnchor = MakeChildAnchor(heroCluster, $"{prefix}_Hero", Vector3.zero);
            seat.HeroPowerAnchor = MakeChildAnchor(heroCluster, $"{prefix}_HeroPower",
                new Vector3(-xSign * HeroPowerOffsetX,
                            -ySign * HeroPowerTowardCenterY, 0f));
            seat.WeaponAnchor = MakeChildAnchor(heroCluster, $"{prefix}_Weapon",
                new Vector3(-xSign * WeaponOffsetX,
                            ySign * WeaponTowardEdgeY, 0f));

            resourceCluster = MakeAnchor($"{prefix}_ResourceCluster",
                new Vector3(xSign * -DeckX, ySign * DeckY, 0f));
            seat.DeckAnchor = MakeChildAnchor(resourceCluster, $"{prefix}_Deck", Vector3.zero);
            seat.ManaTrayAnchor = MakeChildAnchor(resourceCluster, $"{prefix}_ManaTray",
                new Vector3(xSign * (DeckX - ManaGridX + ManaClearOfPileX),
                            ySign * (ManaGridY - DeckY), 0f));
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

        private static Transform MakeChildAnchor(
            Transform parent,
            string anchorName,
            Vector3 localPosition)
        {
            var go = new GameObject(anchorName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }
    }
}
