using System;
using System.Collections.Generic;

namespace Emberfall.Net.Client.Quests
{
    /// <summary>One quest that moved between two reads, and where it stood before.</summary>
    public sealed class QuestAdvance
    {
        public QuestEntry Quest { get; }
        public int PreviousProgress { get; }

        /// <summary>True when this is the read that carried it over its goal.</summary>
        public bool CompletedNow => Quest.Complete && PreviousProgress < Quest.Goal;

        public QuestAdvance(QuestEntry quest, int previousProgress)
        {
            Quest = quest ?? throw new ArgumentNullException(nameof(quest));
            PreviousProgress = previousProgress;
        }
    }

    /// <summary>
    /// What a match did to the player's quests, read off two <c>GET /quests</c> snapshots — one
    /// taken before the match and one after. The server sends no "this match advanced X" message
    /// and progress is applied off the match thread (<c>QuestProgressSink</c>), so the difference
    /// between two projections is the only place the client can see it.
    /// </summary>
    public static class QuestProgressDiff
    {
        private static readonly IReadOnlyList<QuestAdvance> None = Array.Empty<QuestAdvance>();

        /// <summary>
        /// The quests whose progress is higher in <paramref name="after"/>, in the order
        /// <paramref name="after"/> lists them. Either snapshot being anything but
        /// <see cref="QuestFetchStatus.Known"/> answers nothing: without a real baseline every
        /// quest with progress would read as having advanced this match.
        ///
        /// <para>A quest keys on id AND day — the same quest id recurs on a later day as a
        /// separate row. One absent from <paramref name="before"/> started from zero.</para>
        /// </summary>
        public static IReadOnlyList<QuestAdvance> Advanced(QuestSnapshot? before, QuestSnapshot? after)
        {
            if (before is null || after is null) return None;
            if (before.Status != QuestFetchStatus.Known || after.Status != QuestFetchStatus.Known)
                return None;

            var baseline = new Dictionary<(string, string), int>();
            foreach (QuestEntry quest in before.Quests)
                baseline[(quest.Id, quest.Day)] = quest.Progress;

            List<QuestAdvance>? advanced = null;
            foreach (QuestEntry quest in after.Quests)
            {
                baseline.TryGetValue((quest.Id, quest.Day), out int previous);
                if (quest.Progress <= previous) continue;
                (advanced ??= new List<QuestAdvance>()).Add(new QuestAdvance(quest, previous));
            }
            return advanced ?? None;
        }
    }
}
