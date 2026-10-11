using System.Threading.Tasks;
using System;
using System.Linq;
using Kingdom.Core.Collection;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    [TestFixture]
    [Category("Unit")]
    public sealed class GoldFaucetTests
    {
        private sealed class StubGoldRewardPolicy : IGoldRewardPolicy
        {
            public long QuestGold { get; init; } = 50;
            public long MatchGold { get; init; } = 10;
            public long FirstWinGold { get; init; } = 20;

            public long DailyQuestGold(string questId) => QuestGold;
        }

        private static GoldFaucet CreateFaucet(
            IEntitlementLedger ledger,
            ITimeSource time,
            IGoldRewardPolicy? policy = null)
            => new GoldFaucet(ledger, time, policy ?? new StubGoldRewardPolicy());

        private static Account AuthenticatedAccount(string credentialKind = "google")
            => new Account("player-1", credentialKind, "subject-hash", DateTimeOffset.UnixEpoch);

        [Test]
        public async Task A_Daily_Quest_Completion_Grants_Once_Per_Player_Quest_And_Utc_Day()
        {
            var ledger = new InMemoryEntitlementLedger();
            var faucet = CreateFaucet(ledger, new FakeTime());

            await faucet.GrantDailyQuestAsync(AuthenticatedAccount(), "quest-play-three");
            await faucet.GrantDailyQuestAsync(AuthenticatedAccount(), "quest-play-three");

            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gold), Is.EqualTo(50));
            Assert.That(await ledger.HistoryAsync("player-1"), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_Winning_Match_Grants_The_Per_Match_And_First_Win_Rewards_Once()
        {
            var ledger = new InMemoryEntitlementLedger();
            var faucet = CreateFaucet(ledger, new FakeTime());

            MatchGoldGrantResult first = await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-1", won: true);
            MatchGoldGrantResult replay = await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-1", won: true);

            Assert.That(first.MatchRewardApplied, Is.True);
            Assert.That(first.FirstWinRewardApplied, Is.True);
            Assert.That(first.GoldApplied, Is.EqualTo(30));
            Assert.That(replay.GoldApplied, Is.Zero);
            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gold), Is.EqualTo(30));
        }

        [Test]
        public async Task A_Second_Win_That_Day_Still_Grants_Per_Match_But_Not_First_Win_Again()
        {
            var ledger = new InMemoryEntitlementLedger();
            var faucet = CreateFaucet(ledger, new FakeTime());

            await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-1", true);
            MatchGoldGrantResult second = await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-2", true);

            Assert.That(second.MatchRewardApplied, Is.True);
            Assert.That(second.FirstWinRewardApplied, Is.False);
            Assert.That(second.GoldApplied, Is.EqualTo(10));
            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gold), Is.EqualTo(40));
        }

        [Test]
        public async Task A_Loss_Still_Grants_Per_Match_Without_Consuming_The_Days_First_Win()
        {
            var ledger = new InMemoryEntitlementLedger();
            var faucet = CreateFaucet(ledger, new FakeTime());

            MatchGoldGrantResult loss = await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-1", false);
            MatchGoldGrantResult win = await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-2", true);

            Assert.That(loss.MatchRewardApplied, Is.True);
            Assert.That(loss.FirstWinRewardApplied, Is.False);
            Assert.That(win.FirstWinRewardApplied, Is.True);
            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gold), Is.EqualTo(40));
        }

        [Test]
        public async Task First_Win_And_Daily_Quest_Rearm_On_The_Next_Utc_Day()
        {
            var ledger = new InMemoryEntitlementLedger();
            var time = new FakeTime();
            var faucet = CreateFaucet(ledger, time);
            await faucet.GrantDailyQuestAsync(AuthenticatedAccount(), "quest-1");
            await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-1", true);

            time.Advance(24L * 60 * 60 * 1000);
            await faucet.GrantDailyQuestAsync(AuthenticatedAccount(), "quest-1");
            MatchGoldGrantResult nextDay = await faucet.GrantMatchAsync(AuthenticatedAccount(), "match-2", true);

            Assert.That(nextDay.FirstWinRewardApplied, Is.True);
            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gold), Is.EqualTo(160));
        }

        [Test]
        public async Task Faucet_Rejects_Non_Positive_Server_Policy_Rewards()
        {
            var ledger = new InMemoryEntitlementLedger();
            var zeroQuest = CreateFaucet(
                ledger, new FakeTime(), new StubGoldRewardPolicy { QuestGold = 0 });
            var negativeMatch = CreateFaucet(
                ledger, new FakeTime(), new StubGoldRewardPolicy { MatchGold = -1 });

            Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await zeroQuest.GrantDailyQuestAsync(AuthenticatedAccount(), "quest-1"));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await negativeMatch.GrantMatchAsync(AuthenticatedAccount(), "match-1", false));
            Assert.That(await ledger.HistoryAsync("player-1"), Is.Empty);
        }

        [Test]
        public async Task Concurrent_Wins_Still_Pay_Only_One_First_Win_Reward()
        {
            var ledger = new InMemoryEntitlementLedger();
            var faucet = CreateFaucet(ledger, new FakeTime());

            MatchGoldGrantResult[] results = await Task.WhenAll(
                faucet.GrantMatchAsync(AuthenticatedAccount(), "match-a", true),
                faucet.GrantMatchAsync(AuthenticatedAccount(), "match-b", true));

            Assert.That(results.Count(r => r.MatchRewardApplied), Is.EqualTo(2));
            Assert.That(results.Count(r => r.FirstWinRewardApplied), Is.EqualTo(1));
            Assert.That(await ledger.BalanceAsync("player-1", EconomyItems.Gold), Is.EqualTo(40));
        }

        [Test]
        public async Task A_Device_Token_Account_Cannot_Receive_Gold()
        {
            var ledger = new InMemoryEntitlementLedger();
            var faucet = CreateFaucet(ledger, new FakeTime());

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await faucet.GrantDailyQuestAsync(AuthenticatedAccount("device_token"), "quest-1"));
            Assert.That(await ledger.HistoryAsync("player-1"), Is.Empty);
        }
    }
}
