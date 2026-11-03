using System;
using System.Collections.Generic;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Server.King
{
    /// <summary>One reign's King: pool indices for the epithet and fate, and the commission id.</summary>
    public sealed record Reign(int Number, int Epithet, int Fate, string Commission);

    /// <summary>
    /// A run of reigns drawing from one set of pools. Append a new epoch to grow a pool; never edit
    /// an existing one, because that rewrites the Kings already crowned.
    /// </summary>
    public sealed record KingEpoch(int FromReign, IReadOnlyList<string> Commissions, int EpithetCount, int FateCount);

    /// <summary>
    /// The King is a pure function of the reign number. <c>reign % pool.Count</c> would rewrite
    /// history whenever a pool grew, so reigns index from the start of the epoch they fall in.
    /// </summary>
    public static class KingSchedule
    {
        /// <summary>Epithet and fate advance by these steps so they do not pair predictably.
        /// Both are coprime to 30, so neither repeats within a pool cycle.</summary>
        private const int EpithetStep = 7;
        private const int FateStep = 11;

        public static IReadOnlyList<KingEpoch> Epochs { get; } = new[]
        {
            new KingEpoch(1, CommissionIds.Launch, CommissionIds.EpithetCount, CommissionIds.FateCount),
        };

        public static Reign For(int reign) => For(reign, Epochs);

        internal static Reign For(int reign, IReadOnlyList<KingEpoch> epochs)
        {
            if (reign < 1) throw new ArgumentOutOfRangeException(nameof(reign), "Reigns start at 1.");

            KingEpoch? epoch = null;
            foreach (KingEpoch candidate in epochs)
                if (candidate.FromReign <= reign && (epoch == null || candidate.FromReign > epoch.FromReign))
                    epoch = candidate;
            if (epoch == null) throw new InvalidOperationException("No epoch covers reign " + reign + ".");

            long i = reign - epoch.FromReign;
            return new Reign(
                reign,
                (int)(i * EpithetStep % epoch.EpithetCount),
                (int)(i * FateStep % epoch.FateCount),
                epoch.Commissions[(int)(i % epoch.Commissions.Count)]);
        }
    }
}
