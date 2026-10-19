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
        int TurnsEndedWithTenUnspent = 0,
        int SelfDamage = 0,
        int Overkills = 0,
        int BigMinionsBounced = 0,
        int LegendariesLost = 0,
        int MostBulwarks = 0,
        FinalBlow FellTo = FinalBlow.None,
        FinalBlow EnemyFellTo = FinalBlow.None)
    {
        /// <summary>Both attributed seats of one finished match.</summary>
        public static IReadOnlyList<AchievementMatchFacts> From(MatchCompleteEvent complete)
        {
            if (complete is null) throw new ArgumentNullException(nameof(complete));
            var facts = new List<AchievementMatchFacts>(2);
            AddSeat(facts, complete, complete.P0, complete.P1);
            AddSeat(facts, complete, complete.P1, complete.P0);
            return facts;
        }

        /// <summary>One seat (0 or 1) of a finished match, attributed or not — the result screen
        /// stamps a guest's findings too, though only a durable account keeps them.</summary>
        public static AchievementMatchFacts ForSeat(MatchCompleteEvent complete, int seat)
        {
            if (complete is null) throw new ArgumentNullException(nameof(complete));
            return seat == 0 ? Of(complete, complete.P0, complete.P1) : Of(complete, complete.P1, complete.P0);
        }

        private static void AddSeat(List<AchievementMatchFacts> into, MatchCompleteEvent complete,
            SeatOutcome seat, SeatOutcome enemy)
        {
            if (!seat.Account.IsAttributed) return;
            into.Add(Of(complete, seat, enemy));
        }

        private static AchievementMatchFacts Of(MatchCompleteEvent complete, SeatOutcome seat, SeatOutcome enemy)
            => new AchievementMatchFacts(
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
                seat.TurnsEndedWithTenUnspent,
                seat.SelfDamage,
                seat.Overkills,
                seat.BigMinionsBounced,
                seat.LegendariesLost,
                seat.MostBulwarks,
                seat.FellTo,
                enemy.FellTo);
    }

    /// <summary>One achievement: its text, its store counterpart and the rule that earns it.</summary>
    /// <param name="PlayGamesId">
    /// The id Play Console minted for this achievement. Empty until the console entry exists: the
    /// server still records the achievement, and the client unlocks it once the id lands here.
    /// </param>
    public sealed record AchievementDefinition(
        AchievementText Text,
        string PlayGamesId,
        Func<AchievementMatchFacts, bool> EarnedBy)
    {
        /// <summary>Permanent; see <see cref="AchievementText.Id"/>.</summary>
        public string Id => Text.Id;
    }

    /// <summary>
    /// Every achievement, in the order the Ledger lists them, which is the order of
    /// <see cref="AchievementTexts.All"/>. Each is earned once and kept for good
    /// (Sam, 2026-10-17), and only in matches found through Find Game; <see cref="AchievementService"/>
    /// holds that rule so no entry here has to repeat it.
    ///
    /// <para><b>The first four are the gentle ones</b> Play's quality checklist asks for: reliably
    /// earned in a player's first hour (dev-docs/design/whimsy-pass-2026-10-17.md, #2).</para>
    ///
    /// <para><b>The rest are the Council's Findings</b> from the same list. Play wants at least 10
    /// visible, so publish these visible. The ones about who dealt a blow read
    /// <see cref="FinalBlow"/>, which <c>FindingsObserver</c> fills from the event stream.</para>
    /// </summary>
    public static class AchievementCatalog
    {
        public static IReadOnlyList<AchievementDefinition> All { get; } = Array.AsReadOnly(new[]
        {
            Rule("second_thoughts", "CgkIkJ2NnuUeEAIQAQ", f => f.MulliganReplaced > 0),
            Rule("working_as_intended", "CgkIkJ2NnuUeEAIQBw", f => f.HeroPowersUsed > 0),
            Rule("early_retirement", "CgkIkJ2NnuUeEAIQBA", f => f.EnemyMinionsDestroyed > 0),
            Rule("against_advice", "CgkIkJ2NnuUeEAIQAw", f => f.Result == SeatResult.Win),
            Rule("proceed_unwisely", "CgkIkJ2NnuUeEAIQAg", f => f.Result == SeatResult.Win && f.FinalHealth == 1),
            Rule("out_of_ideas", "CgkIkJ2NnuUeEAIQBQ", f => f.FatigueDraws > 0),
            Rule("too_many_ideas", "CgkIkJ2NnuUeEAIQCA", f => f.CardsBurned > 0),
            Rule("approved_by_default", "CgkIkJ2NnuUeEAIQCQ", f => f.TurnsTimedOut > 0),
            Rule("waste_not", "CgkIkJ2NnuUeEAIQBg", f => f.TurnsEndedWithTenUnspent > 0),
            Rule("mutual_respect", "CgkIkJ2NnuUeEAIQCg", f => f.Result == SeatResult.Draw),
            Rule("self_starter", "", f => f.SelfDamage >= 15),
            Rule("overkill", "", f => f.Overkills > 0),
            Rule("hubris", "", f => f.BigMinionsBounced > 0),
            Rule("gift_horse", "", f => f.LegendariesLost > 0),
            Rule("seven_walls", "", f => f.MostBulwarks >= 7),
            Rule("the_last_idea", "", f => f.Result == SeatResult.Loss && (f.FellTo & FinalBlow.Fatigue) != 0),
            Rule("technically", "", f => f.Result == SeatResult.Win && f.FellTo != FinalBlow.None),
            Rule("posthumous", "", f => f.Result == SeatResult.Win && (f.EnemyFellTo & FinalBlow.Toll) != 0),
            Rule("return_to_sender", "", f => f.Result == SeatResult.Win && (f.EnemyFellTo & FinalBlow.StolenMinion) != 0),
            Rule("signature_move", "", f => f.Result == SeatResult.Win && (f.EnemyFellTo & FinalBlow.HeroPower) != 0),
            Rule("own_goal", "", f => f.Result == SeatResult.Loss && (f.FellTo & (FinalBlow.Spell | FinalBlow.OwnSide)) == (FinalBlow.Spell | FinalBlow.OwnSide)),
        });

        /// <summary>
        /// The Council's Findings this seat committed in one match, in Ledger order — what the
        /// result screen stamps, every match, whether or not the Ledger already holds them. The
        /// same rules the Ledger is earned by, and the same Find Game rule
        /// <see cref="AchievementService"/> keeps, so a friend match stamps nothing.
        /// </summary>
        public static IReadOnlyList<string> FindingsIn(AchievementMatchFacts facts)
        {
            if (facts is null) throw new ArgumentNullException(nameof(facts));
            var found = new List<string>();
            if (facts.Source != TelemetrySource.Ladder && facts.Source != TelemetrySource.BotFill) return found;
            foreach (AchievementDefinition definition in All)
                if (definition.Text.IsFinding && definition.EarnedBy(facts)) found.Add(definition.Id);
            return found;
        }

        private static AchievementDefinition Rule(string id, string playGamesId, Func<AchievementMatchFacts, bool> earnedBy)
            => new AchievementDefinition(
                AchievementTexts.Find(id) ?? throw new InvalidOperationException($"No AchievementText for '{id}'."),
                playGamesId, earnedBy);

        /// <summary>The definition with this id, or null for one the catalog does not hold.</summary>
        public static AchievementDefinition? Find(string id)
        {
            foreach (AchievementDefinition definition in All)
                if (string.Equals(definition.Id, id, StringComparison.Ordinal)) return definition;
            return null;
        }
    }
}
