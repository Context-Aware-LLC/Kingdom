using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    // Concrete, mutable implementation of ICardLiveState (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1 + §5.2).
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
        public AttackReadiness AttackReadiness { get; private set; }
        public int SpellDamageBonus { get; private set; }
        public int? Durability { get; private set; }

        public event Action<CardChange> Changed;

        private readonly HashSet<Keyword> _keywords = new HashSet<Keyword>();
        private readonly bool _equippedWeapon;

        /// <param name="equippedWeapon">True only for the state behind the hero's weapon socket.
        /// A weapon's hand and equipped lives share one InstanceId, so the zone cannot be read off
        /// the instance; the owner says which one this is.</param>
        public CardLiveState(InstanceId id, bool equippedWeapon = false)
        {
            Id = id;
            _equippedWeapon = equippedWeapon;
        }

        // ownerMayAct: this card sits on the local human's board, it's that player's turn,
        // and the phase is Main (BoardController computes it from the snapshot).
        public void UpdateFrom(CardInstance ci, bool ownerMayAct = false, int spellDamageBonus = 0)
        {
            CardChange changed = CardChange.None;

            int? durability = _equippedWeapon ? ci.CurrentDurability : null;
            if (Attack != ci.CurrentAttack || Health != ci.CurrentHealth || MaxHealth != ci.MaxHealth
                || Durability != durability)
            {
                Attack     = ci.CurrentAttack;
                Health     = ci.CurrentHealth;
                MaxHealth  = ci.MaxHealth;
                Durability = durability;
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

            AttackReadiness newReadiness = BuildReadiness(ci, ownerMayAct);
            if (newReadiness != AttackReadiness)
            {
                AttackReadiness = newReadiness;
                changed |= CardChange.Affordance;
            }

            if (SpellDamageBonus != spellDamageBonus)
            {
                SpellDamageBonus = spellDamageBonus;
                changed |= CardChange.Text;
            }

            if (changed != CardChange.None)
                Changed?.Invoke(changed);
        }

        // Mirrors the attacker-side checks of CommandProcessor.ValidateAttack (defender-
        // independent half). Presentation-side courtesy only — the validator/authority
        // still rules; keep in lockstep when the engine's attack rules change.
        private static AttackReadiness BuildReadiness(CardInstance ci, bool ownerMayAct)
        {
            if (!ownerMayAct) return AttackReadiness.None;

            bool eligible = !ci.IsFrosted()
                && ci.CurrentAttack > 0
                && (!ci.SummonedThisTurn
                    || ci.Keywords.Contains(Keyword.FERVOR)
                    || ci.Keywords.Contains(Keyword.SKIRMISH))
                && ci.AttacksUsedThisTurn < (ci.Keywords.Contains(Keyword.DOUBLESTRIKE) ? 2 : 1);

            return eligible ? AttackReadiness.Ready : AttackReadiness.Exhausted;
        }

        private static AuraSet BuildAuras(CardInstance ci)
        {
            AuraSet a = AuraSet.None;
            if (ci.HasAegisShield)                            a |= AuraSet.Shield;
            if (ci.IsFrosted())                                  a |= AuraSet.Frosted;
            if (ci.Keywords.Contains(Keyword.BULWARK))          a |= AuraSet.Bulwark;
            if (ci.Keywords.Contains(Keyword.SHROUD))        a |= AuraSet.Shroud;
            if (ci.Keywords.Contains(Keyword.ARCANE_SURGE))   a |= AuraSet.Glow;
            return a;
        }
    }
}
