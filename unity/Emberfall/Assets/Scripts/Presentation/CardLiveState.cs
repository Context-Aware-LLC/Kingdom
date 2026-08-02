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

        public event Action<CardChange> Changed;

        private readonly HashSet<Keyword> _keywords = new HashSet<Keyword>();

        public CardLiveState(InstanceId id) => Id = id;

        // ownerMayAct: this card sits on the local human's board, it's that player's turn,
        // and the phase is Main (BoardController computes it from the snapshot).
        public void UpdateFrom(CardInstance ci, bool ownerMayAct = false)
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

            AttackReadiness newReadiness = BuildReadiness(ci, ownerMayAct);
            if (newReadiness != AttackReadiness)
            {
                AttackReadiness = newReadiness;
                changed |= CardChange.Affordance;
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

            bool eligible = !ci.IsFrozen
                && ci.CurrentAttack > 0
                && (!ci.SummonedThisTurn
                    || ci.Keywords.Contains(Keyword.CHARGE)
                    || ci.Keywords.Contains(Keyword.RUSH))
                && ci.AttacksUsedThisTurn < (ci.Keywords.Contains(Keyword.WINDFURY) ? 2 : 1);

            return eligible ? AttackReadiness.Ready : AttackReadiness.Exhausted;
        }

        private static AuraSet BuildAuras(CardInstance ci)
        {
            AuraSet a = AuraSet.None;
            if (ci.HasAegisShield)                            a |= AuraSet.Shield;
            if (ci.IsFrozen)                                  a |= AuraSet.Frozen;
            if (ci.Keywords.Contains(Keyword.TAUNT))          a |= AuraSet.Taunt;
            if (ci.Keywords.Contains(Keyword.STEALTH))        a |= AuraSet.Stealth;
            if (ci.Keywords.Contains(Keyword.SPELL_DAMAGE))   a |= AuraSet.Glow;
            return a;
        }
    }
}
