using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    [Flags]
    public enum AuraSet { None = 0, Shield = 1, Shroud = 2, Glow = 4, Bulwark = 8, Frosted = 16 }

    [Flags]
    public enum CardChange
    {
        None = 0,
        Stats = 1,
        Keywords = 2,
        Auras = 4,
        Slot = 8,
        Zone = 16,
        Affordance = 32,
        Text = 64,
    }

    // Attack affordance for the local player's board minions (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/design-system.md §6.5 "Also do"
    // column / ui-polish master list). None = not applicable (hand card, enemy minion, not
    // your turn); Ready = would pass the attacker-side checks; Exhausted = on board on your
    // turn but cannot attack (used attacks, summoning sickness, frosted, 0 attack).
    public enum AttackReadiness { None, Ready, Exhausted }

    // Live, read-only view of one card instance on screen (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1).
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
        int SpellDamageBonus { get; }

        /// <summary>Current durability of an EQUIPPED weapon, and null for every other card —
        /// including a weapon in hand, whose instance stats are not materialised until it is
        /// equipped (DerivedStats.EnterWeapon). Null means "the printed card is the whole truth".
        /// Only HeroSocketsView's weapon state sets it.</summary>
        int? Durability { get; }

        event Action<CardChange> Changed;
    }
}
