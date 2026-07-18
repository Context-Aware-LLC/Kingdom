using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    [Flags]
    public enum AuraSet { None = 0, Shield = 1, Stealth = 2, Glow = 4, Taunt = 8, Frozen = 16 }

    [Flags]
    public enum CardChange { None = 0, Stats = 1, Keywords = 2, Auras = 4, Slot = 8, Zone = 16, Affordance = 32 }

    // Attack affordance for the local player's board minions (design-system.md §6.5 "Also do"
    // column / ui-polish master list). None = not applicable (hand card, enemy minion, not
    // your turn); Ready = would pass the attacker-side checks; Exhausted = on board on your
    // turn but cannot attack (used attacks, summoning sickness, frozen, 0 attack).
    public enum AttackReadiness { None, Ready, Exhausted }

    // Live, read-only view of one card instance on screen (interfaces.md §5.1).
    // Fires Changed when any field changes so CardPresenter subscribes once and never polls.
    public interface ICardLiveState
    {
        InstanceId Id { get; }
        int Attack { get; }
        int Health { get; }
        int MaxHealth { get; }
        IReadOnlyCollection<Keyword> Keywords { get; }
        AuraSet Auras { get; }
        int BoardSlot { get; }
        AttackReadiness AttackReadiness { get; }

        event Action<CardChange> Changed;
    }
}
