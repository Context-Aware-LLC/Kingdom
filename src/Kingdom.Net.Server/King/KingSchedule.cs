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
    /// <param name="FirstIndex">The pool position the epoch's first reign takes. 0 starts every
    /// pool over; an epoch that keeps the epithet and fate pools sets it to the number of reigns
    /// before it, so its first King is the one the old epoch would have crowned next rather than
    /// a repeat of Reign 1's epithet and fate. It shifts the commission index too, so the reigns the
    /// old epoch already announced stay the same only while the new commission list keeps the old
    /// one's entries at those positions (epoch 2 keeps Launch's first three; reigns 2 and 3 depend
    /// on it). Ship a new epoch before its first changed reign begins, and never roll back past it:
    /// either moves a live board's commission mid-reign.</param>
    public sealed record KingEpoch(int FromReign, IReadOnlyList<string> Commissions, int EpithetCount, int FateCount,
                                   int FirstIndex = 0);

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
            // #443: Comeback, The Long Game and Overgrown join from Reign 2 (2026-11-10). Same epithet
            // and fate pools, so FirstIndex 1 carries them on unbroken, and Reigns 2 and 3 keep the
            // commissions the launch epoch gave them.
            new KingEpoch(2, CommissionIds.FromReignTwo, CommissionIds.EpithetCount, CommissionIds.FateCount,
                          FirstIndex: 1),
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

            long i = reign - epoch.FromReign + epoch.FirstIndex;
            return new Reign(
                reign,
                (int)(i * EpithetStep % epoch.EpithetCount),
                (int)(i * FateStep % epoch.FateCount),
                epoch.Commissions[(int)(i % epoch.Commissions.Count)]);
        }
    }
}
