using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Unity-free interpretation of authoritative match events for hero-impact feedback. Keeping
    /// this policy separate makes local-seat filtering and concede-vs-death behavior testable
    /// without a device or the machine-wide Unity Editor seat.
    /// </summary>
    internal static class HeroImpactFeedbackPolicy
    {
        public static HeroImpactFeedbackDecision Evaluate(GameState state, PlayerId localPlayer,
            IReadOnlyList<GameEvent> events, bool heroDeathShakePlayed)
        {
            if (state == null || events == null)
                return default;

            InstanceId? localHeroId = null;
            for (int i = 0; i < state.Players.Length; i++)
            {
                if (state.Players[i].Id != localPlayer) continue;
                localHeroId = state.Players[i].Hero.Id;
                break;
            }

            int damagePulseCount = 0;
            bool matchEnded = false;
            for (int i = 0; i < events.Count; i++)
            {
                GameEvent matchEvent = events[i];
                if (matchEvent.Kind == GameEventKind.MatchEnded)
                    matchEnded = true;

                bool isHeroDamage = matchEvent.Kind == GameEventKind.DamageApplied ||
                                    matchEvent.Kind == GameEventKind.FatigueDamage;
                if (isHeroDamage && matchEvent.Amount.GetValueOrDefault() > 0 &&
                    localHeroId.HasValue && matchEvent.Target == localHeroId)
                    damagePulseCount++;
            }

            bool shakeForHeroDeath = false;
            if (!heroDeathShakePlayed && matchEnded)
            {
                for (int i = 0; i < state.Players.Length; i++)
                {
                    if (state.Players[i].Hero.CurrentHealth > 0) continue;
                    shakeForHeroDeath = true;
                    break;
                }
            }

            return new HeroImpactFeedbackDecision(damagePulseCount, shakeForHeroDeath);
        }
    }

    internal readonly struct HeroImpactFeedbackDecision
    {
        public readonly int DamagePulseCount;
        public readonly bool ShakeForHeroDeath;

        public HeroImpactFeedbackDecision(int damagePulseCount, bool shakeForHeroDeath)
        {
            DamagePulseCount = damagePulseCount;
            ShakeForHeroDeath = shakeForHeroDeath;
        }
    }
}
