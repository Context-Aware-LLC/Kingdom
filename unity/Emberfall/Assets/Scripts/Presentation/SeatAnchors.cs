using System;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Serializable holder for one player's D6 anchor transforms (board-and-vfx.md §1).
    // Authored in the editor scene OR populated at runtime by ProceduralBoardSetup.
    [Serializable]
    public sealed class SeatAnchors
    {
        public Transform DeckAnchor;
        public Transform[] HandSlots  = new Transform[10];   // 0-9 hand positions

        // Seat 1's hand fan is seat 0's point-reflected through the origin (§1.1).
        public bool FanMirrored;

        public Transform[] BoardSlots = new Transform[7];    // 0-6 minion slots
        public Transform HeroAnchor;
        public Transform WeaponAnchor;
        public Transform HeroPowerAnchor;
        public Transform ManaTrayAnchor;
    }
}
