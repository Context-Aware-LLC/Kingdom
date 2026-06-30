using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    // Concrete, mutable implementation of ICardLiveState (interfaces.md §5.1 + §5.2).
    // BoardController owns these; it diffs successive GameState snapshots and calls UpdateFrom.
    public sealed class CardLiveState : ICardLiveState
    {
        public InstanceId Id { get; }
        public int Attack    { get; private set; }
        public int Health    { get; private set; }
        public int MaxHealth { get; private set; }
        public IReadOnlyCollection<Keyword> Keywords => _keywords;
        public AuraSet Auras   { get; private set; }
        public int BoardSlot   { get; private set; }

        public event Action<CardChange> Changed;

        private readonly HashSet<Keyword> _keywords = new HashSet<Keyword>();

        public CardLiveState(InstanceId id) => Id = id;

        public void UpdateFrom(CardInstance ci)
        {
            CardChange changed = CardChange.None;

            if (Attack != ci.CurrentAttack || Health != ci.CurrentHealth || MaxHealth != ci.MaxHealth)
            {
                Attack    = ci.CurrentAttack;
                Health    = ci.CurrentHealth;
                MaxHealth = ci.MaxHealth;
                changed  |= CardChange.Stats;
            }

            if (!_keywords.SetEquals(ci.Keywords))
            {
                _keywords.Clear();
                foreach (Keyword k in ci.Keywords) _keywords.Add(k);
                changed |= CardChange.Keywords;
            }

            AuraSet newAuras = BuildAuras(ci);
            if (newAuras != Auras)
            {
                Auras   = newAuras;
                changed |= CardChange.Auras;
            }

            if (BoardSlot != ci.Position)
            {
                BoardSlot = ci.Position;
                changed  |= CardChange.Slot;
            }

            if (changed != CardChange.None)
                Changed?.Invoke(changed);
        }

        private static AuraSet BuildAuras(CardInstance ci)
        {
            AuraSet a = AuraSet.None;
            if (ci.HasAegisShield)                         a |= AuraSet.Shield;
            if (ci.IsFrozen)                               a |= AuraSet.Frozen;
            if (ci.Keywords.Contains(Keyword.TAUNT))       a |= AuraSet.Taunt;
            if (ci.Keywords.Contains(Keyword.STEALTH))     a |= AuraSet.Stealth;
            return a;
        }
    }
}
