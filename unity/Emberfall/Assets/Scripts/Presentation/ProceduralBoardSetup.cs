using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Creates the full D6 anchor hierarchy at runtime so the board works without a hand-crafted
    // Unity scene. Runs before GameBootstrap.Start() (Awake order).
    // Attach this to the same GameObject as BoardAnchors.
    // When the scene is authoring-complete (anchors placed in editor), remove this component.
    //
    // Layout (orthographic camera, world units):
    //   Board rows:  y = ±1.3   (7 slots, x = -4.05..+4.05 at 1.35 spacing)
    //   Hand rows:   fanned by HandFanLayout — fixed 5-unit footprint, count-dependent pitch
    //   Hero cluster mirrors per seat (first-person rule: your hero is on YOUR left, so the
    //   two heroes sit kitty-corner — P0 bottom-left, P1 top-right). xSign: P0=-1, P1=+1.
    //   Hero:        x = 5.2·xSign,  y = ±1.3
    //   Weapon:      x = 4.1·xSign,  y = ±1.3
    //   HeroPower:   x = 5.2·xSign,  y = ±2.3
    //   ManaTray:    x = -5.8·xSign, y = ±2.0   (opposite side from the hero cluster)
    //   Deck:        x = -5.8·xSign, y = ±2.5
    [RequireComponent(typeof(BoardAnchors))]
    public sealed class ProceduralBoardSetup : MonoBehaviour
    {
        private const int   BoardSlotCount = 7;
        private const int   HandSlotCount  = 10;
        private const float SlotSpacing    = 1.35f;

        // Bottom (P0) is positive side, top (P1) is negative (camera looks at board from front).
        private const float P0Sign = -1f;
        private const float P1Sign =  1f;

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

            seat.HeroAnchor      = MakeAnchor($"{prefix}_Hero",      new Vector3(xSign *  6.5f, ySign * 1.3f, 0f));
            seat.WeaponAnchor    = MakeAnchor($"{prefix}_Weapon",    new Vector3(xSign *  5.3f, ySign * 1.3f, 0f));
            seat.HeroPowerAnchor = MakeAnchor($"{prefix}_HeroPower", new Vector3(xSign *  6.5f, ySign * 2.3f, 0f));
            seat.ManaTrayAnchor  = MakeAnchor($"{prefix}_ManaTray",  new Vector3(xSign * -5.8f, ySign * 2.0f, 0f));
            seat.DeckAnchor      = MakeAnchor($"{prefix}_Deck",      new Vector3(xSign * -5.8f, ySign * 2.5f, 0f));
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
