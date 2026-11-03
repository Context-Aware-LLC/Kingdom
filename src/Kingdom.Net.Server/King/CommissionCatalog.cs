using System;
using System.Collections.Generic;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Achievements;

namespace Kingdom.Net.Server.King
{
    /// <summary>
    /// One commission's scoring rule. <paramref name="Score"/> reads the seat's exact tally off its
    /// <see cref="AchievementMatchFacts"/>, which <c>MatchTelemetryRecorder.TallyFor</c> fills, so the
    /// live score and the banked one come from the same numbers.
    /// </summary>
    /// <param name="NeedsWin">Only a won match banks, a win by concede or timeout included.</param>
    /// <param name="LowerIsBetter">The board sorts ascending. Such a commission also places at 0 or
    /// below, because those are legitimate scores there; every other one needs a score above 0.</param>
    public sealed record Commission(string Id, bool NeedsWin, bool LowerIsBetter, Func<AchievementMatchFacts, int> Score)
    {
        /// <summary>Whether <paramref name="score"/> can take a place: above 0, or anything at all
        /// where lower is better. The in-match card shows no number until this holds.</summary>
        public bool Qualifies(int score) => LowerIsBetter || score > 0;

        /// <summary>Whether <paramref name="score"/> strictly beats <paramref name="best"/>. A tie
        /// does not, because the board gives a tie to whoever reached it first.</summary>
        public bool Beats(int score, int? best)
            => best is not int held || (LowerIsBetter ? score < held : score > held);
    }

    /// <summary>
    /// Every commission the King can ask for, by its permanent <see cref="CommissionIds"/> id. The
    /// spec's table (dev-docs/design/king-of-the-week-2026-11-02.md, "The commissions") is the source.
    /// </summary>
    public static class CommissionCatalog
    {
        public static IReadOnlyList<Commission> All { get; } = Array.AsReadOnly(new[]
        {
            new Commission(CommissionIds.Overkill, false, false, f => f.OverkillBest),
            new Commission(CommissionIds.Technically, true, true, f => f.FinalHealth),
            new Commission(CommissionIds.SelfStarter, true, false, f => f.SelfDamage),
            new Commission(CommissionIds.WasteNot, true, false, f => f.UnspentMana),
            new Commission(CommissionIds.OutOfIdeas, true, false, f => f.FatigueDamage),
            new Commission(CommissionIds.TooManyIdeas, true, false, f => f.CardsBurned),
            new Commission(CommissionIds.Posthumous, false, false, f => f.TollDamage),
            new Commission(CommissionIds.ReturnToSender, false, false, f => f.StolenDamage),
            new Commission(CommissionIds.SignatureMove, false, false, f => f.HeroPowerDamage),
            new Commission(CommissionIds.Comeback, true, false, f => f.MostHealthDeficit),
            new Commission(CommissionIds.TheLongGame, true, false, f => f.TurnCount),
            new Commission(CommissionIds.Overgrown, false, false, f => f.LargestMinion),
        });

        /// <summary>The commission with this id, or null when there is none.</summary>
        public static Commission? Find(string id)
        {
            foreach (Commission c in All)
                if (c.Id == id) return c;
            return null;
        }

        private static readonly HashSet<string> UnknownLogged = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The board's direction for <paramref name="id"/>: the one place a reader asks it. An id this
        /// catalog does not know sorts highest first and is logged, once per id, the way
        /// <c>FavorService</c> logs one it cannot bank. Nothing banks under such an id, so its board
        /// is empty whichever way it sorts; the log line is what says why.
        /// </summary>
        public static bool LowerIsBetter(string id)
        {
            Commission? commission = Find(id);
            if (commission is not null) return commission.LowerIsBetter;
            bool first;
            lock (UnknownLogged) first = UnknownLogged.Add(id ?? "");
            if (first)
                Console.Error.WriteLine("[king] no commission '{0}' in the catalog; its board sorts highest first.", id);
            return false;
        }
    }
}
