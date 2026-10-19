using System;
using System.Collections.Generic;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Telemetry;

namespace Kingdom.Net.Server.Achievements
{
    /// <summary>
    /// One seat's view of one finished match: everything an achievement may read. It is built from
    /// <see cref="MatchCompleteEvent"/> alone, the same seam quests read
    /// (<c>QuestMatchFacts</c>), so an achievement can never ask a question the match-end event does
    /// not already answer. One that needs more adds a counter to <see cref="SeatOutcome"/> first, the
    /// route <see cref="SeatOutcome.EnemyMinionsDestroyed"/> took.
    /// </summary>
    public sealed record AchievementMatchFacts(
        string MatchId,
        string PlayerId,
        DateTimeOffset EndedAtUtc,
        TelemetrySource Source,
        SeatResult Result,
        MatchEndReason EndReason,
        int TurnCount,
        int EnemyMinionsDestroyed,
        int MulliganReplaced,
        int HeroPowersUsed,
        int FinalHealth = 0,
        int FatigueDraws = 0,
        int CardsBurned = 0,
        int TurnsTimedOut = 0,
        int TurnsEndedWithTenUnspent = 0)
    {
        /// <summary>Both attributed seats of one finished match.</summary>
        public static IReadOnlyList<AchievementMatchFacts> From(MatchCompleteEvent complete)
        {
            if (complete is null) throw new ArgumentNullException(nameof(complete));
            var facts = new List<AchievementMatchFacts>(2);
            AddSeat(facts, complete, complete.P0);
            AddSeat(facts, complete, complete.P1);
            return facts;
        }

        private static void AddSeat(List<AchievementMatchFacts> into, MatchCompleteEvent complete, SeatOutcome seat)
        {
            if (!seat.Account.IsAttributed) return;
            into.Add(new AchievementMatchFacts(
                complete.Match.Value,
                seat.Account.Value,
                complete.EndedAtUtc,
                complete.Source,
                seat.Result,
                complete.EndReason,
                complete.TurnCount,
                seat.EnemyMinionsDestroyed,
                seat.MulliganReplaced,
                seat.HeroPowersUsed,
                seat.FinalHealth,
                seat.FatigueDraws,
                seat.CardsBurned,
                seat.TurnsTimedOut,
                seat.TurnsEndedWithTenUnspent));
        }
    }

    /// <summary>One achievement: the Ledger entry and its store counterparts.</summary>
    /// <param name="Id">
    /// Permanent. It keys the earned row, and neither Play Games nor Game Center lets a published
    /// achievement be deleted, so an id is retired, never reused.
    /// </param>
    /// <param name="Name">What Play Console shows, and later the Ledger page.</param>
    /// <param name="Description">The plain line first, then the Council's citation.</param>
    /// <param name="PlayGamesId">
    /// The id Play Console minted for this achievement. Empty until the console entry exists: the
    /// server still records the achievement, and the client unlocks it once the id lands here.
    /// </param>
    public sealed record AchievementDefinition(
        string Id,
        string Name,
        string Description,
        string PlayGamesId,
        Func<AchievementMatchFacts, bool> EarnedBy);

    /// <summary>
    /// Every achievement, in the order the Ledger lists them. Each is earned once and kept for good
    /// (Sam, 2026-10-17), and only in matches found through Find Game; <see cref="AchievementService"/>
    /// holds that rule so no entry here has to repeat it.
    ///
    /// <para><b>The first four are the gentle ones</b> Play's quality checklist asks for: reliably
    /// earned in a player's first hour (dev-docs/design/whimsy-pass-2026-10-17.md, #2).</para>
    ///
    /// <para><b>The rest are the Council's Findings</b> from the same list, the ones a match-end
    /// counter can answer. Play wants at least 10 visible, so publish these visible.</para>
    /// </summary>
    public static class AchievementCatalog
    {
        public static IReadOnlyList<AchievementDefinition> All { get; } = Array.AsReadOnly(new[]
        {
            new AchievementDefinition(
                "second_thoughts", "Second Thoughts",
                "Replace a card in your mulligan. The Council has also reconsidered.",
                "CgkIkJ2NnuUeEAIQAQ", f => f.MulliganReplaced > 0),
            new AchievementDefinition(
                "working_as_intended", "Working as Intended",
                "Use your Hero Power. The Council has inspected the button and found it to be working.",
                "CgkIkJ2NnuUeEAIQBw", f => f.HeroPowersUsed > 0),
            new AchievementDefinition(
                "early_retirement", "Early Retirement",
                "Destroy an enemy minion. Its pension has been approved.",
                "CgkIkJ2NnuUeEAIQBA", f => f.EnemyMinionsDestroyed > 0),
            new AchievementDefinition(
                "against_advice", "Against Advice",
                "Win a match. The Council has amended its advice.",
                "CgkIkJ2NnuUeEAIQAw", f => f.Result == SeatResult.Win),
            new AchievementDefinition(
                "proceed_unwisely", "Proceed, Unwisely",
                "Win a match with 1 Health. The Council had already drafted the other announcement.",
                "CgkIkJ2NnuUeEAIQAg", f => f.Result == SeatResult.Win && f.FinalHealth == 1),
            new AchievementDefinition(
                "out_of_ideas", "Out of Ideas",
                "Draw from an empty deck. The Council knows the feeling.",
                "CgkIkJ2NnuUeEAIQBQ", f => f.FatigueDraws > 0),
            new AchievementDefinition(
                "too_many_ideas", "Too Many Ideas",
                "Draw a card into a full hand. The card has been filed with the others.",
                "CgkIkJ2NnuUeEAIQCA", f => f.CardsBurned > 0),
            new AchievementDefinition(
                "approved_by_default", "Approved by Default",
                "Let the hourglass run out. The stamp went ahead without you.",
                "CgkIkJ2NnuUeEAIQCQ", f => f.TurnsTimedOut > 0),
            new AchievementDefinition(
                "waste_not", "Waste Not",
                "End a turn with 10 mana unspent. The Council admires the restraint and has asked what it was for.",
                "CgkIkJ2NnuUeEAIQBg", f => f.TurnsEndedWithTenUnspent > 0),
            new AchievementDefinition(
                "mutual_respect", "Mutual Respect",
                "Draw a match. The Council is still deliberating.",
                "CgkIkJ2NnuUeEAIQCg", f => f.Result == SeatResult.Draw),
        });

        /// <summary>The definition with this id, or null for one the catalog does not hold.</summary>
        public static AchievementDefinition? Find(string id)
        {
            foreach (AchievementDefinition definition in All)
                if (string.Equals(definition.Id, id, StringComparison.Ordinal)) return definition;
            return null;
        }
    }
}
