using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
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
        void SetHandCount(PlayerId player, int count);

        Transform HeroAnchor(PlayerId player);
        Transform HeroPowerAnchor(PlayerId player);
        Transform ManaTrayAnchor(PlayerId player);
        Transform WeaponAnchor(PlayerId player);
    }
}
