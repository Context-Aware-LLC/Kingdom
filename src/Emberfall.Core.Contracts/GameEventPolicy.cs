using System;

namespace Emberfall.Core.Contracts
{
    /// <summary>Field and flag policy for the closed semantic event vocabulary.</summary>
    public static class GameEventPolicy
    {
        public static bool IsKnown(GameEventKind kind) => Enum.IsDefined(typeof(GameEventKind), kind);

        public static bool IsKnown(PresentationCue cue) => Enum.IsDefined(typeof(PresentationCue), cue);

        public static bool AllowsAmount(GameEventKind kind)
        {
            return kind == GameEventKind.DamageApplied
                || kind == GameEventKind.HealthRestored
                || kind == GameEventKind.StatsModified
                || kind == GameEventKind.ManaGained
                || kind == GameEventKind.ArmorGained
                || kind == GameEventKind.FatigueDamage;
        }

        public static EventFlags AllowedFlags(GameEventKind kind)
        {
            return kind switch
            {
                GameEventKind.DamageApplied => EventFlags.AegisAbsorbed | EventFlags.Lethal,
                GameEventKind.CardSummoned => EventFlags.IsToken,
                GameEventKind.CardCreatedInHand => EventFlags.IsToken,
                GameEventKind.CardDrawn => EventFlags.Overdraw,
                GameEventKind.FatigueDamage => EventFlags.Fatigue,
                GameEventKind.MatchWon => EventFlags.Lethal,
                GameEventKind.CardPlayed
                    or GameEventKind.AttackDeclared
                    or GameEventKind.HealthRestored
                    or GameEventKind.CardDestroyed
                    or GameEventKind.StatsModified
                    or GameEventKind.KeywordApplied
                    or GameEventKind.CardSilenced
                    or GameEventKind.FrostApplied
                    or GameEventKind.CardTransformed
                    or GameEventKind.CardBounced
                    or GameEventKind.ManaGained
                    or GameEventKind.WeaponEquipped
                    or GameEventKind.UnearthOffered
                    or GameEventKind.TurnStarted
                    or GameEventKind.TurnEnded
                    or GameEventKind.HeroPowerUsed
                    or GameEventKind.ArmorGained
                    or GameEventKind.ControlChanged
                    or GameEventKind.MatchEnded => EventFlags.None,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown semantic game-event kind."),
            };
        }

        public static void Validate(
            GameEventKind kind,
            int? amount,
            EventFlags flags,
            PresentationCue? cueOverride)
        {
            if (!IsKnown(kind))
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown semantic game-event kind.");
            if (amount.HasValue && !AllowsAmount(kind))
                throw new ArgumentException($"{kind} does not carry an amount.", nameof(amount));

            EventFlags allowedFlags = AllowedFlags(kind);
            if ((flags & ~allowedFlags) != 0)
                throw new ArgumentException($"{kind} does not allow flags '{flags & ~allowedFlags}'.", nameof(flags));
            if (cueOverride.HasValue && !IsKnown(cueOverride.Value))
                throw new ArgumentOutOfRangeException(nameof(cueOverride), cueOverride, "Unknown presentation cue.");
        }
    }
}
