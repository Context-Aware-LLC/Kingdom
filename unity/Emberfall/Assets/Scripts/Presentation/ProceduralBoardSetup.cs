using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Creates the full D6 anchor hierarchy at runtime so the board works without a hand-crafted
    // Unity scene. Runs before GameBootstrap.Start() (Awake order).
    // Attach this to the same GameObject as BoardAnchors.
    // When the scene is authoring-complete (anchors placed in editor), remove this component.
    //
    // Every bottom-edge anchor below is the techart/board-and-vfx.md §1.1 budget, pinned
    // 2026-07-24 (was M1 placeholder coordinates that sat the hero cluster level with the
    // board row). §1.1 allocates SPANS on x; the element centres here are those spans minus
    // each element's own width, and the y column is §1.3 — the vertical §1.1 never wrote.
    //
    // Layout (orthographic camera size 4.6, so the screen edges are y ±4.6, world units):
    //   Board rows:  y = ±1.3   (7 slots, x = -4.05..+4.05 at 1.35 spacing) — outside §1.1
    //   Hand rows:   fanned by HandFanLayout — fixed 5-unit footprint, count-dependent pitch
    //   The hero cluster mirrors per seat (first-person rule: your hero is on YOUR left, so the
    //   two heroes sit kitty-corner — P0 bottom-left, P1 top-right). P0 signs are (-1,-1), so
    //   seat 1 is seat 0 point-reflected through the origin, exactly as §1.1 specifies.
    [RequireComponent(typeof(BoardAnchors))]
    public sealed class ProceduralBoardSetup : MonoBehaviour
    {
        private const int   BoardSlotCount = 7;
        private const int   HandSlotCount  = 10;
        private const float SlotSpacing    = 1.35f;

        // Bottom (P0) is positive side, top (P1) is negative (camera looks at board from front).
        private const float P0Sign = -1f;
        private const float P1Sign =  1f;

        // ── §1.1 bottom-edge budget, as |coordinates| for seat 0 ────────────────────────
        // Signs are applied in BuildSeat: x is negated for the hero side, positive for the
        // rail/deck side, then multiplied by the seat sign.
        //
        // Hero cluster owns x -7.88..-4.98 (2.9). Portrait outer against the margin, the
        // two-disc column inboard toward the hand (Sam, 2026-07-24).
        private const float HeroX      = 6.98f;  // 1.8-wide plate centred in -7.88..-6.08
        private const float DiscX      = 5.53f;  // 0.9Ø column centred in -6.08..-4.98

        // Mana rail owns +2.5..+6.53 (4.03); deck pile +6.53..+7.83 (1.3).
        private const float ManaRailX  = 4.515f;
        private const float DeckX      = 7.18f;

        // ── Vertical (§1.3) ─────────────────────────────────────────────────────────────
        // §1.1 is an x-budget only. Everything in it rests on one baseline 0.15 above the
        // screen edge: |y| = 4.45. Each centre below is that baseline plus half the element's
        // own height, so moving the baseline moves the whole bottom row together.
        private const float HeroY      = 3.55f;  // 1.8-tall plate on the baseline
        private const float WeaponY    = 3.10f;  // upper 0.9 disc  — column top = plate top
        private const float HeroPowerY = 4.00f;  // lower 0.9 disc, sitting on the baseline
        private const float ManaRailY  = 4.00f;  // aligned to the hero-power disc, not the
                                                 // baseline: the two read as one bottom line
        private const float DeckY      = 3.53f;  // a 1.3-wide card-proportioned pile (1.83
                                                 // tall) on the baseline; nothing renders
                                                 // here yet — DeckAnchor is the draw source

        private void Awake()
        {
            var anchors = GetComponent<BoardAnchors>();
            BuildSeat(anchors.p0, "P0", P0Sign, xSign: -1f);
            BuildSeat(anchors.p1, "P1", P1Sign, xSign: +1f);
        }

        private void BuildSeat(SeatAnchors seat, string prefix, float ySign, float xSign)
        {
            seat.BoardSlots = new Transform[BoardSlotCount];
            for (int i = 0; i < BoardSlotCount; i++)
            {
                float x = (i - (BoardSlotCount - 1) * 0.5f) * SlotSpacing;
                seat.BoardSlots[i] = MakeAnchor($"{prefix}_Board{i}", new Vector3(x, ySign * 1.3f, 0f));
            }

            // Hand slots are placed by HandFanLayout, not by this method's flat pitch: the fan
            // re-solves all ten every time the hand count changes (ui/match-hud.md §8). Built
            // here at full count so the anchors are never in a stale row before the first sync.
            seat.FanMirrored = ySign > 0f;
            seat.HandSlots   = new Transform[HandSlotCount];
            for (int i = 0; i < HandSlotCount; i++)
                seat.HandSlots[i] = MakeAnchor($"{prefix}_Hand{i}", Vector3.zero);
            HandFanLayout.Apply(seat.HandSlots, HandSlotCount, seat.FanMirrored);

            seat.HeroAnchor      = MakeAnchor($"{prefix}_Hero",      new Vector3(xSign *  HeroX,     ySign * HeroY,      0f));
            seat.WeaponAnchor    = MakeAnchor($"{prefix}_Weapon",    new Vector3(xSign *  DiscX,     ySign * WeaponY,    0f));
            seat.HeroPowerAnchor = MakeAnchor($"{prefix}_HeroPower", new Vector3(xSign *  DiscX,     ySign * HeroPowerY, 0f));
            seat.ManaTrayAnchor  = MakeAnchor($"{prefix}_ManaTray",  new Vector3(xSign * -ManaRailX, ySign * ManaRailY,  0f));
            seat.DeckAnchor      = MakeAnchor($"{prefix}_Deck",      new Vector3(xSign * -DeckX,     ySign * DeckY,      0f));
        }

        // Called from GameBootstrap.Start() after SetPlayers() — attaches ClickableBoardSlot
        // and ClickableHeroPower so the human player can click the board and hero power.
        // Board slots get slots for own side only; P1's slots are intentionally not clickable
        // in hot-seat mode (P1 is AI-controlled).
        public void SetupClickables(PlayerId p0Id, PlayerId p1Id, InputRouter router)
        {
            if (router == null) return;
            var anchors = GetComponent<BoardAnchors>();
            AttachSlotClickables(anchors.p0, p0Id, router);
            AttachSlotClickables(anchors.p1, p1Id, router);
        }

        private static void AttachSlotClickables(SeatAnchors seat, PlayerId ownerId, InputRouter router)
        {
            if (seat.BoardSlots != null)
            {
                for (int i = 0; i < seat.BoardSlots.Length; i++)
                {
                    if (seat.BoardSlots[i] == null) continue;
                    var slot = seat.BoardSlots[i].gameObject.AddComponent<ClickableBoardSlot>();
                    slot.Init(ownerId, i, router);
                }
            }

            if (seat.HeroPowerAnchor != null)
            {
                var hp = seat.HeroPowerAnchor.gameObject.AddComponent<ClickableHeroPower>();
                hp.Init(ownerId, router);
            }

            if (seat.HeroAnchor != null)
            {
                var hero = seat.HeroAnchor.gameObject.AddComponent<ClickableHero>();
                hero.Init(ownerId, router);
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
