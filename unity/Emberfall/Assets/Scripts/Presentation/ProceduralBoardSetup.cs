using UnityEngine;

namespace Emberfall.Presentation
{
    // Creates the full D6 anchor hierarchy at runtime so the board works without a hand-crafted
    // Unity scene. Runs before GameBootstrap.Start() (Awake order).
    // Attach this to the same GameObject as BoardAnchors.
    // When the scene is authoring-complete (anchors placed in editor), remove this component.
    //
    // Layout (orthographic camera, world units):
    //   Board rows:  y = ±1.3   (7 slots, x = -2.7..+2.7 at 0.9 spacing)
    //   Hand rows:   y = ±3.8   (10 slots, x = -4.05..+4.05 at 0.9 spacing)
    //   Hero:        x = +5.2,  y = ±1.3
    //   Weapon:      x = +4.1,  y = ±1.3
    //   HeroPower:   x = +5.2,  y = ±2.3
    //   ManaTray:    x = -5.8,  y = ±2.0
    //   Deck:        x = +5.8,  y = ±2.5
    [RequireComponent(typeof(BoardAnchors))]
    public sealed class ProceduralBoardSetup : MonoBehaviour
    {
        private const int   BoardSlotCount = 7;
        private const int   HandSlotCount  = 10;
        private const float SlotSpacing    = 0.90f;

        // Bottom (P0) is positive side, top (P1) is negative (camera looks at board from front).
        private const float P0Sign = -1f;
        private const float P1Sign =  1f;

        private void Awake()
        {
            var anchors = GetComponent<BoardAnchors>();
            BuildSeat(anchors.p0, "P0", P0Sign);
            BuildSeat(anchors.p1, "P1", P1Sign);
        }

        private void BuildSeat(SeatAnchors seat, string prefix, float ySign)
        {
            seat.BoardSlots = new Transform[BoardSlotCount];
            for (int i = 0; i < BoardSlotCount; i++)
            {
                float x = (i - (BoardSlotCount - 1) * 0.5f) * SlotSpacing;
                seat.BoardSlots[i] = MakeAnchor($"{prefix}_Board{i}", new Vector3(x, ySign * 1.3f, 0f));
            }

            seat.HandSlots = new Transform[HandSlotCount];
            for (int i = 0; i < HandSlotCount; i++)
            {
                float x = (i - (HandSlotCount - 1) * 0.5f) * SlotSpacing;
                seat.HandSlots[i] = MakeAnchor($"{prefix}_Hand{i}", new Vector3(x, ySign * 3.8f, 0f));
            }

            seat.HeroAnchor      = MakeAnchor($"{prefix}_Hero",      new Vector3( 5.2f, ySign * 1.3f, 0f));
            seat.WeaponAnchor    = MakeAnchor($"{prefix}_Weapon",    new Vector3( 4.1f, ySign * 1.3f, 0f));
            seat.HeroPowerAnchor = MakeAnchor($"{prefix}_HeroPower", new Vector3( 5.2f, ySign * 2.3f, 0f));
            seat.ManaTrayAnchor  = MakeAnchor($"{prefix}_ManaTray",  new Vector3(-5.8f, ySign * 2.0f, 0f));
            seat.DeckAnchor      = MakeAnchor($"{prefix}_Deck",      new Vector3( 5.8f, ySign * 2.5f, 0f));
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
