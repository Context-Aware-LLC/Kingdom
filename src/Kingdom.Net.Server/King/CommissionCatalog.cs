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
    public sealed record Commission(string Id, bool NeedsWin, bool LowerIsBetter, Func<AchievementMatchFacts, int> Score);

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
        });

        /// <summary>The commission with this id, or null when there is none.</summary>
        public static Commission? Find(string id)
        {
            foreach (Commission c in All)
                if (c.Id == id) return c;
            return null;
        }
    }
}
