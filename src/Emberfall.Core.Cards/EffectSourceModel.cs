using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// The runtime shape that carries a rule. This is deliberately independent of ordinary card
    /// zones: a resolved spell can later own an ongoing effect without becoming a fake card in play.
    /// </summary>
    public enum EffectSourceKind
    {
        Unknown = 0,
        BoardMinion,
        EquippedWeapon,
        Spell,
        HeroPower,
        OngoingEffect,
    }

    /// <summary>A stable reference to one authored rule on one immutable definition.</summary>
    public readonly struct RuleRef : IEquatable<RuleRef>
    {
        public int DefinitionId { get; }
        public RuleId RuleId { get; }

        public RuleRef(int definitionId, RuleId ruleId)
        {
            if (definitionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(definitionId), definitionId,
                    "A rule reference requires a definition id.");
            DefinitionId = definitionId;
            RuleId = ruleId;
        }

        public bool Equals(RuleRef other)
            => DefinitionId == other.DefinitionId && RuleId.Equals(other.RuleId);

        public override bool Equals(object? obj) => obj is RuleRef other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (DefinitionId * 397) ^ RuleId.GetHashCode();
            }
        }

        public override string ToString() => $"{DefinitionId}/{RuleId}";
        public static bool operator ==(RuleRef left, RuleRef right) => left.Equals(right);
        public static bool operator !=(RuleRef left, RuleRef right) => !left.Equals(right);
    }

    /// <summary>
    /// Closed source/trigger relation shared by validation and runtime enumeration. An absent kind,
    /// trigger, or pair is unsupported until its concrete raise path lands.
    /// </summary>
    public static class EffectSourceCapabilities
    {
        private static readonly IReadOnlyDictionary<EffectSourceKind, HashSet<Trigger>> Supported =
            new Dictionary<EffectSourceKind, HashSet<Trigger>>
            {
                [EffectSourceKind.BoardMinion] = new HashSet<Trigger>
                {
                    Trigger.ON_PLAY,
                    Trigger.ON_DEATH,
                    Trigger.ON_ATTACK,
                    Trigger.ON_DAMAGED,
                    Trigger.ON_HEAL,
                    Trigger.ON_TURN_START,
                    Trigger.ON_TURN_END,
                    Trigger.ON_FRIENDLY_SUMMON,
                    Trigger.ON_SPELL_CAST,
                    Trigger.AURA,
                },
                [EffectSourceKind.EquippedWeapon] = new HashSet<Trigger>
                {
                    Trigger.ON_PLAY,
                    Trigger.ON_DEATH,
                    Trigger.ON_ATTACK,
                    Trigger.ON_TURN_START,
                    Trigger.ON_TURN_END,
                    Trigger.ON_FRIENDLY_SUMMON,
                    Trigger.ON_SPELL_CAST,
                },
                [EffectSourceKind.Spell] = new HashSet<Trigger>
                {
                    Trigger.ON_CAST,
                },
                [EffectSourceKind.HeroPower] = new HashSet<Trigger>
                {
                    Trigger.ON_CAST,
                },
                [EffectSourceKind.OngoingEffect] = new HashSet<Trigger>
                {
                    Trigger.ON_TURN_START,
                    Trigger.ON_TURN_END,
                    Trigger.ON_FRIENDLY_SUMMON,
                    Trigger.ON_SPELL_CAST,
                    // AURA is admitted here to give a SPELL's aura a carrier, and it is the one
                    // member of this set that is never RAISED. No `RaiseObservers` site passes
                    // Trigger.AURA, so admitting it buys persistence — a lifetime, a controller and
                    // an id that survive the spell — without ever enqueuing an effect. The fold that
                    // reads it is AuraEngine.Recompute, which walks OngoingEffects directly.
                    Trigger.AURA,
                },
            };

        public static bool Supports(EffectSourceKind sourceKind, Trigger trigger)
            => Supported.TryGetValue(sourceKind, out HashSet<Trigger>? triggers) &&
               triggers.Contains(trigger);

        public static bool SupportsAny(Trigger trigger)
        {
            foreach (HashSet<Trigger> triggers in Supported.Values)
                if (triggers.Contains(trigger)) return true;
            return false;
        }

        public static bool TryForCardType(CardType cardType, out EffectSourceKind sourceKind)
        {
            switch (cardType)
            {
                case CardType.MINION:
                    sourceKind = EffectSourceKind.BoardMinion;
                    return true;
                case CardType.WEAPON:
                    sourceKind = EffectSourceKind.EquippedWeapon;
                    return true;
                case CardType.SPELL:
                    sourceKind = EffectSourceKind.Spell;
                    return true;
                case CardType.HERO_POWER:
                    sourceKind = EffectSourceKind.HeroPower;
                    return true;
                default:
                    sourceKind = EffectSourceKind.Unknown;
                    return false;
            }
        }
    }
}
