#nullable enable
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
    /// The words a status row is built from. The model is pure and compiled into the .NET tests,
    /// which have no <c>Loc</c>, so the language comes in from the caller: <see cref="English"/> by
    /// default, the player's language from <c>OngoingEffectsView</c>.
    /// </summary>
    public interface IOngoingEffectWords
    {
        string You { get; }
        string Opponent { get; }
        string Permanent { get; }
        string UnknownLifetime { get; }
        string SourceFallback(int cardId);
        string TriggersRemaining(int uses);
        string CardName(CardDefinition card);
    }

    /// <summary>
    /// Pure adapter from the projected Contracts surface to UI-ready status text. It resolves only
    /// the public originating definition id; the authority-only rule reference is neither needed
    /// nor reachable here.
    /// </summary>
    public static class OngoingEffectStatusModel
    {
        /// <summary>The source-language words, matching the English rows of <c>strings.xml</c>.</summary>
        public sealed class English : IOngoingEffectWords
        {
            public static readonly English Instance = new English();
            public string You => "You";
            public string Opponent => "Opponent";
            public string Permanent => "Permanent";
            public string UnknownLifetime => "Unknown lifetime";
            public string SourceFallback(int cardId) => $"card {cardId}";
            public string TriggersRemaining(int uses) => uses == 1 ? "1 trigger remaining" : $"{uses} triggers remaining";
            public string CardName(CardDefinition card) => card.Name;
        }

        public static IReadOnlyList<OngoingEffectStatus> Build(GameState state,
            ICardDatabase database, PlayerId viewer, IOngoingEffectWords? words = null)
        {
            words ??= English.Instance;
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
                    ? words.CardName(definition)
                    : words.SourceFallback(effect.SourceDefinitionId);
                string owner = effect.Controller == viewer ? words.You : words.Opponent;
                rows.Add(new OngoingEffectStatus(effect.Id, sourceName, owner,
                    RemainingText(effect, words)));
            }
            return rows;
        }

        private static string RemainingText(OngoingEffectInstance effect, IOngoingEffectWords words)
        {
            switch (effect.Lifetime)
            {
                case OngoingEffectLifetime.PERMANENT:
                    return words.Permanent;
                case OngoingEffectLifetime.ONE_SHOT:
                    return words.TriggersRemaining(1);
                case OngoingEffectLifetime.FINITE:
                    int uses = effect.RemainingUses.GetValueOrDefault();
                    return words.TriggersRemaining(uses);
                default:
                    return words.UnknownLifetime;
            }
        }
    }
}
