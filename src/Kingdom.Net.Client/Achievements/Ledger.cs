using System;
using System.Collections.Generic;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Client.Achievements
{
    /// <summary>One entry of the Ledger of Bad Ideas: its text, and when it was earned.</summary>
    public sealed class LedgerEntry
    {
        public AchievementText Text { get; }
        public bool Earned { get; }

        /// <summary>Null for an entry not earned, and for one earned on a row with no date.</summary>
        public DateTimeOffset? EarnedAt { get; }

        public LedgerEntry(AchievementText text, bool earned, DateTimeOffset? earnedAt)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Earned = earned;
            EarnedAt = earnedAt;
        }
    }

    /// <summary>
    /// The Ledger as the page shows it: every achievement this build has text for, in
    /// <see cref="AchievementTexts"/> order, each marked earned or not from <c>GET /achievements</c>.
    /// An earned id this build has no text for is left out rather than printed by id: a newer
    /// server can name one an older store build does not know.
    /// </summary>
    public static class Ledger
    {
        public static IReadOnlyList<LedgerEntry> Of(IReadOnlyList<EarnedAchievementEntry> earned)
        {
            var byId = new Dictionary<string, EarnedAchievementEntry>(StringComparer.Ordinal);
            if (earned != null)
                foreach (EarnedAchievementEntry entry in earned)
                    if (entry != null && !byId.ContainsKey(entry.Id)) byId.Add(entry.Id, entry);

            var entries = new List<LedgerEntry>(AchievementTexts.All.Count);
            foreach (AchievementText text in AchievementTexts.All)
            {
                bool has = byId.TryGetValue(text.Id, out EarnedAchievementEntry? row);
                entries.Add(new LedgerEntry(text, has, has ? row!.EarnedAt : null));
            }
            return entries;
        }
    }
}
