using System;

namespace Emberfall.Core.Contracts
{
    /// <summary>Exhaustive presentation defaults for the closed semantic event vocabulary.</summary>
    public static class PresentationCueDefaults
    {
        public static PresentationCue For(GameEventKind kind)
        {
            return kind switch
            {
                GameEventKind.CardPlayed => PresentationCue.Play,
                GameEventKind.CardSummoned => PresentationCue.Summon,
                GameEventKind.AttackDeclared => PresentationCue.Attack,
                GameEventKind.DamageApplied => PresentationCue.Damage,
                GameEventKind.HealthRestored => PresentationCue.Heal,
                GameEventKind.CardDestroyed => PresentationCue.Death,
                GameEventKind.StatsModified => PresentationCue.Buff,
                GameEventKind.KeywordApplied => PresentationCue.Buff,
                GameEventKind.CardDrawn => PresentationCue.Draw,
                GameEventKind.CardCreatedInHand => PresentationCue.Draw,
                GameEventKind.CardSilenced => PresentationCue.Silence,
                GameEventKind.FrostApplied => PresentationCue.Frost,
                GameEventKind.CardTransformed => PresentationCue.Transform,
                GameEventKind.CardBounced => PresentationCue.Return,
                GameEventKind.ManaGained => PresentationCue.ManaGain,
                GameEventKind.WeaponEquipped => PresentationCue.Equip,
                GameEventKind.UnearthOffered => PresentationCue.Unearth,
                GameEventKind.TurnStarted => PresentationCue.TurnStart,
                GameEventKind.TurnEnded => PresentationCue.TurnEnd,
                GameEventKind.HeroPowerUsed => PresentationCue.HeroPower,
                GameEventKind.FatigueDamage => PresentationCue.Fatigue,
                GameEventKind.MatchWon => PresentationCue.GameWin,
                GameEventKind.MatchEnded => PresentationCue.GameOver,
                GameEventKind.ArmorGained => PresentationCue.ArmorGain,
                GameEventKind.ControlChanged => PresentationCue.ControlChange,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown semantic game-event kind."),
            };
        }
    }
}
