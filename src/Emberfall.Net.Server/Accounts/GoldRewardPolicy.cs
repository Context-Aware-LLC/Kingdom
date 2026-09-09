using System;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The shipping Gold reward schedule. <see cref="GoldFaucet"/> owns replay protection and
    /// calendar identity; this type owns nothing but the quantities, which is the whole reason the
    /// faucet takes a policy rather than an amount — no call site can invent a number.
    ///
    /// <para><b>Anchor for every value below: a pack costs
    /// <see cref="EconomyService.PackPrice"/> Gold.</b> That is the only price these rewards are
    /// measured against, so read them as "how much play buys a pack" rather than as absolute
    /// amounts. At the defaults a player who wins once and finishes six matches earns 80 — roughly
    /// four packs a week for a daily player, before quests.</para>
    ///
    /// <para><b>These are tuning numbers, not invariants.</b> They are `init` so a deployment can
    /// move them without a code change, and the economy-health metrics that judge them
    /// (time-to-first-competitive-deck, Shards inflation) are telemetry questions, not test
    /// questions — no test asserts a specific quantity, only that the faucet pays once.</para>
    /// </summary>
    public sealed record GoldRewardPolicy : IGoldRewardPolicy
    {
        /// <summary>
        /// Paid for every completed eligible match, won or lost. Small on purpose: losing has to
        /// pay something or a losing streak reads as wasted time, but a per-match faucet is the one
        /// a colluding pair could farm by conceding to each other, so it is not the bulk of the
        /// income.
        ///
        /// <para><b>There is no daily cap on this, and nothing else caps it either.</b> Eligibility
        /// is the only bound: <c>RankedMatchResult.IsEligible</c> pays only for a Ladder-source
        /// match between two distinct attributed accounts, so farming it needs a second real
        /// account and a matchmaking hit, not a bot. If the ladder ever pays bot-fill matches, or
        /// the population gets small enough that two accounts can reliably find each other, this
        /// needs a per-UTC-day ceiling before that happens — the faucet has the day key already
        /// (see <c>GoldFaucet.GrantMatchAsync</c>'s first-win transaction id).</para>
        /// </summary>
        public long MatchGold { get; init; } = 5;

        /// <summary>
        /// Paid once per UTC day, on top of <see cref="MatchGold"/>, for the day's first win. The
        /// return-tomorrow hook: it is worth ten ordinary matches, so the first session of a day is
        /// always the most valuable one, and skipping a day costs a player nothing they had.
        /// </summary>
        public long FirstWinGold { get; init; } = 50;

        /// <summary>
        /// Paid per completed daily quest. Flat today because no quest catalog exists yet to carry
        /// per-quest values.
        ///
        /// <para><b>RETIREMENT TRIGGER — the quest catalog.</b> The commit that lands a quest
        /// definition carrying its own reward replaces this flat value with a lookup on
        /// <paramref name="questId"/> and deletes this paragraph. Until then every questId pays the
        /// same, and the id is accepted only so the faucet's per-quest idempotency key works.</para>
        /// </summary>
        public long DailyQuestFlatGold { get; init; } = 60;

        long IGoldRewardPolicy.DailyQuestGold(string questId)
        {
            if (string.IsNullOrWhiteSpace(questId))
                throw new ArgumentException("A quest id is required.", nameof(questId));
            return DailyQuestFlatGold;
        }
    }
}
