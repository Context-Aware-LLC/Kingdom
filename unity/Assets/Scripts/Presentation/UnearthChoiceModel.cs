#nullable enable
using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    public enum UnearthChoiceAudience
    {
        None,
        Chooser,
        Opponent,
    }

    /// <summary>
    /// Viewer-safe, immutable presentation state for the snapshot's pending Unearth choice.
    /// Options deliberately exist only for the chooser and retain their authoritative order.
    /// </summary>
    public sealed class UnearthChoicePresentation
    {
        public static readonly UnearthChoicePresentation Hidden =
            new UnearthChoicePresentation(UnearthChoiceAudience.None, Array.Empty<int>(), 0);

        public UnearthChoicePresentation(
            UnearthChoiceAudience audience, IReadOnlyList<int> options, ulong choiceSequence)
        {
            Audience = audience;
            Options = options;
            ChoiceSequence = choiceSequence;
        }

        public UnearthChoiceAudience Audience { get; }
        public IReadOnlyList<int> Options { get; }
        public ulong ChoiceSequence { get; }
        public bool BlocksGameplayInput => Audience != UnearthChoiceAudience.None;
    }

    /// <summary>
    /// Interprets projected state without relying on events, so reconnect snapshots reopen the
    /// choice naturally and an opponent never infers an offer count from redacted options.
    /// </summary>
    public static class UnearthChoiceModel
    {
        public static UnearthChoicePresentation ForViewer(GameState state, PlayerId viewer)
        {
            PendingChoice? pending = state?.PendingChoice;
            if (pending == null || pending.Kind != PendingChoiceKind.Unearth)
                return UnearthChoicePresentation.Hidden;

            if (pending.Chooser != viewer)
                return new UnearthChoicePresentation(
                    UnearthChoiceAudience.Opponent, Array.Empty<int>(), 0);

            var options = new List<int>(pending.Options.Count);
            foreach (int option in pending.Options) options.Add(option);
            return new UnearthChoicePresentation(
                UnearthChoiceAudience.Chooser, options, pending.ChoiceSequence);
        }

        public static UnearthChoiceIntent? IntentForSelection(
            GameState state, PlayerId player, int chosenCardId, ulong choiceSequence)
        {
            PendingChoice? pending = state?.PendingChoice;
            if (pending == null || pending.Kind != PendingChoiceKind.Unearth
                || pending.Chooser != player || pending.ChoiceSequence != choiceSequence)
                return null;

            foreach (int option in pending.Options)
                if (option == chosenCardId)
                    return new UnearthChoiceIntent(player, chosenCardId, choiceSequence);
            return null;
        }
    }
}
