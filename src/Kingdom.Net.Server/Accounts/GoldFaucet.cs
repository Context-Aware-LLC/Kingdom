using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>Server-owned Gold rewards. Callers report outcomes, never currency amounts.</summary>
    public interface IGoldRewardPolicy
    {
        long DailyQuestGold(string questId);

        long MatchGold { get; }

        long FirstWinGold { get; }
    }

    public readonly record struct MatchGoldGrantResult(
        bool MatchRewardApplied,
        bool FirstWinRewardApplied,
        long GoldApplied);

    /// <summary>
    /// Server-authoritative Gold faucet for the three founder-approved sources. Only authoritative
    /// quest and match systems may report completions; the injected policy owns all reward quantities.
    /// This module owns replay protection and calendar/match identity so no completion can pay twice.
    /// </summary>
    public sealed class GoldFaucet
    {
        private readonly IEntitlementLedger _ledger;
        private readonly ITimeSource _time;
        private readonly IGoldRewardPolicy _policy;

        public GoldFaucet(IEntitlementLedger ledger, ITimeSource time, IGoldRewardPolicy policy)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>Grant one daily quest's configured reward at most once on the current UTC day.</summary>
        public Task<GrantResult> GrantDailyQuestAsync(
            Account account,
            string questId,
            CancellationToken ct = default)
        {
            EconomyAccountPolicy.RequireDurable(account);
            Require(questId, nameof(questId));
            long gold = _policy.DailyQuestGold(questId);
            RequireReward(gold, nameof(IGoldRewardPolicy.DailyQuestGold));
            DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs);
            string txnId = "gold:daily:" + account.PlayerId + ":" + UtcDay(now) + ":" + questId;
            return _ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, gold, "daily_quest", txnId, now), ct);
        }

        /// <summary>
        /// Grant the configured per-match reward and, for a win, the configured first-win reward.
        /// The match id pays once; the first-win key pays once per player and UTC day even when two
        /// winning matches finish concurrently.
        /// </summary>
        public async Task<MatchGoldGrantResult> GrantMatchAsync(
            Account account,
            string matchId,
            bool won,
            CancellationToken ct = default)
        {
            EconomyAccountPolicy.RequireDurable(account);
            Require(matchId, nameof(matchId));
            long matchGold = _policy.MatchGold;
            long firstWinGold = _policy.FirstWinGold;
            RequireReward(matchGold, nameof(IGoldRewardPolicy.MatchGold));
            RequireReward(firstWinGold, nameof(IGoldRewardPolicy.FirstWinGold));
            DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs);

            GrantResult match = await _ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, matchGold, "match_reward",
                "gold:match:" + account.PlayerId + ":" + matchId, now), ct);

            GrantResult firstWin = default;
            if (won)
            {
                firstWin = await _ledger.GrantAsync(new EntitlementGrant(
                    account.PlayerId, EconomyItems.Gold, firstWinGold, "first_win",
                    "gold:first-win:" + account.PlayerId + ":" + UtcDay(now), now), ct);
            }

            bool matchApplied = match.Applied;
            bool firstWinApplied = won && firstWin.Applied;
            long applied = (matchApplied ? matchGold : 0) + (firstWinApplied ? firstWinGold : 0);
            return new MatchGoldGrantResult(matchApplied, firstWinApplied, applied);
        }

        private static string UtcDay(DateTimeOffset at)
            => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(name + " is required.", name);
        }

        private static void RequireReward(long gold, string name)
        {
            if (gold <= 0) throw new ArgumentOutOfRangeException(name, "A Gold reward must be positive.");
        }
    }
}
