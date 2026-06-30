using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Position → named scene anchor (interfaces.md §5.1, D6 zones).
    // BoardAnchors implements this; BoardController and VfxDirector consume it.
    public interface IBoardAnchors
    {
        // Board slots (0-6) and hand slots (0-9) via Zone.Board / Zone.Hand.
        // Zone.Deck and Zone.Weapon each have one slot (slotIndex=0).
        Transform AnchorFor(PlayerId player, Zone zone, int slotIndex);

        Transform HeroAnchor(PlayerId player);
        Transform HeroPowerAnchor(PlayerId player);
        Transform ManaTrayAnchor(PlayerId player);
        Transform WeaponAnchor(PlayerId player);
    }
}
