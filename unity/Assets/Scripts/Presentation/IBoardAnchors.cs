using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    // Position → named scene anchor (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1, D6 zones).
    // BoardAnchors implements this; BoardController and VfxDirector consume it.
    public interface IBoardAnchors
    {
        // Board slots (0-6) and hand slots (0-9) via Zone.Board / Zone.Hand.
        // Zone.Deck and Zone.Weapon each have one slot (slotIndex=0).
        Transform AnchorFor(PlayerId player, Zone zone, int slotIndex);

        // Hand slots are not fixed transforms: the fan re-solves all ten as a function of hand
        // count (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8). Call before resolving a Zone.Hand anchor for this tick.
        // mulliganRow lays the hand out as the head-on mulligan row instead of the fan (#401).
        void SetHandCount(PlayerId player, int count, bool mulliganRow = false);

        // Board slots center on the occupied count rather than sitting at a fixed span (Sam,
        // 2026-08-09). Call before resolving a Zone.Board anchor for this tick, same as
        // SetHandCount.
        void SetBoardCount(PlayerId player, int count);

        Transform HeroAnchor(PlayerId player);
        Transform HeroPowerAnchor(PlayerId player);
        Transform ManaTrayAnchor(PlayerId player);
        Transform WeaponAnchor(PlayerId player);
    }
}
