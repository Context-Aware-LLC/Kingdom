using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
{
    /// <summary>Player-visible, rule-id-free status row for one projected ongoing effect.</summary>
    public readonly struct OngoingEffectStatus
    {
        public InstanceId Id { get; }
        public string SourceName { get; }
        public string Owner { get; }
        public string Remaining { get; }

        public OngoingEffectStatus(InstanceId id, string sourceName, string owner,
            string remaining)
        {
            Id = id;
            SourceName = sourceName;
            Owner = owner;
            Remaining = remaining;
        }
    }

    /// <summary>
    /// Pure adapter from the projected Contracts surface to UI-ready status text. It resolves only
    /// the public originating definition id; the authority-only rule reference is neither needed
    /// nor reachable here.
    /// </summary>
    public static class OngoingEffectStatusModel
    {
        public static IReadOnlyList<OngoingEffectStatus> Build(GameState state,
            ICardDatabase database, PlayerId viewer)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (database == null) throw new ArgumentNullException(nameof(database));
            state.SeatOf(viewer); // reject unknown viewers rather than inventing an ownership label

            var rows = new List<OngoingEffectStatus>(state.OngoingEffects.Count);
            foreach (OngoingEffectInstance effect in state.OngoingEffects)
            {
                // The number is the fallback only when the row is gone; it is not a name, so it
                // is spelled out rather than passed off as one.
                string sourceName = database.TryGet(effect.SourceDefinitionId,
                    out CardDefinition definition)
                    ? definition.Name
                    : Loc.Format("effects_source_fallback", effect.SourceDefinitionId);
                string owner = effect.Controller == viewer ? Loc.Get("common_you") : Loc.Get("common_opponent");
                rows.Add(new OngoingEffectStatus(effect.Id, sourceName, owner,
                    RemainingText(effect)));
            }
            return rows;
        }

        private static string RemainingText(OngoingEffectInstance effect)
        {
            switch (effect.Lifetime)
            {
                case OngoingEffectLifetime.PERMANENT:
                    return Loc.Get("effects_remaining_permanent");
                case OngoingEffectLifetime.ONE_SHOT:
                    return Loc.Plural("effects_remaining_triggers", 1, 1);
                case OngoingEffectLifetime.FINITE:
                    int uses = effect.RemainingUses.GetValueOrDefault();
                    return Loc.Plural("effects_remaining_triggers", uses, uses);
                default:
                    return Loc.Get("effects_remaining_unknown");
            }
        }
    }
}
