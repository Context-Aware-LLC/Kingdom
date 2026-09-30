using System;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
{
    [TestFixture]
    [Category("Unit")]
    public sealed class EconomyServiceTests
    {
        private static readonly DateTimeOffset At =
            DateTimeOffset.FromUnixTimeMilliseconds(1_770_000_000_000);

        private sealed class FixedTime : ITimeSource
        {
            public long NowMs => At.ToUnixTimeMilliseconds();
        }

        private static Account AuthenticatedAccount(string playerId = "player-1")
            => new Account(playerId, "google", "subject-hash", At);

        [Test]
        public async Task Wallet_Projects_Gold_And_Gems_From_The_Authoritative_Ledger()
        {
            var ledger = new InMemoryEntitlementLedger();
            await ledger.GrantAsync(new EntitlementGrant(
                "player-1", EconomyItems.Gold, 375, "quest", "quest-1", At));
            await ledger.GrantAsync(new EntitlementGrant(
                "player-1", EconomyItems.Gems, 550, "play_billing", "store-1", At));
            await ledger.GrantAsync(new EntitlementGrant(
                "player-1", EconomyItems.Shards, 125, "duplicate", "dupe-1", At));
            var economy = new EconomyService(ledger, new FixedTime());

            WalletBalances wallet = await economy.GetWalletAsync(AuthenticatedAccount());

            Assert.Multiple(() =>
            {
                Assert.That(wallet.Gold, Is.EqualTo(375));
                Assert.That(wallet.Gems, Is.EqualTo(550));
                Assert.That(wallet.Shards, Is.EqualTo(125));
            });
        }

        [Test]
        public async Task Wallet_Projects_A_Missing_Shard_Row_As_An_Authoritative_Zero()
        {
            var economy = new EconomyService(new InMemoryEntitlementLedger(), new FixedTime());

            WalletBalances wallet = await economy.GetWalletAsync(AuthenticatedAccount());

            Assert.That(wallet.Shards, Is.EqualTo(0));
        }

        [Test]
        public async Task Pack_Purchase_Atomically_Spends_200_Gold_And_Grants_One_Pack()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 200, "quest", "quest-1", At));
            var economy = new EconomyService(ledger, new FixedTime());
            var pack = new BoosterPack(SetId.EMBERFALL);

            PackPurchaseOutcome result = await economy.PurchasePackAsync(
                account, pack, EconomyCurrency.Gold, "purchase-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(PackPurchaseStatus.Purchased));
                Assert.That(result.PurchaseTransactionId, Is.EqualTo("purchase-1"));
                Assert.That(result.Wallet.Gold, Is.EqualTo(0));
                Assert.That(result.Wallet.Gems, Is.EqualTo(0));
            });
            Assert.That(await ledger.BalanceAsync(account.PlayerId, pack.Id), Is.EqualTo(1));
        }

        [Test]
        public async Task A_multi_pack_purchase_spends_the_total_and_grants_every_pack()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 1000, "quest", "quest-1", At));
            var economy = new EconomyService(ledger, new FixedTime());
            var pack = new BoosterPack(SetId.EMBERFALL);

            PackPurchaseOutcome result = await economy.PurchasePackAsync(
                account, pack, EconomyCurrency.Gold, "purchase-1", 3);

            Assert.That(result.Status, Is.EqualTo(PackPurchaseStatus.Purchased));
            Assert.That(result.Wallet.Gold, Is.EqualTo(400));
            Assert.That(await ledger.BalanceAsync(account.PlayerId, pack.Id), Is.EqualTo(3));
        }

        [Test]
        public async Task A_multi_pack_purchase_the_wallet_cannot_cover_takes_nothing()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 599, "quest", "quest-1", At));
            var economy = new EconomyService(ledger, new FixedTime());
            var pack = new BoosterPack(SetId.EMBERFALL);

            PackPurchaseOutcome result = await economy.PurchasePackAsync(
                account, pack, EconomyCurrency.Gold, "purchase-1", 3);

            Assert.That(result.Status, Is.EqualTo(PackPurchaseStatus.InsufficientFunds));
            Assert.That(result.Wallet.Gold, Is.EqualTo(599));
            Assert.That(await ledger.BalanceAsync(account.PlayerId, pack.Id), Is.Zero);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(EconomyService.MaxPackQuantity + 1)]
        public void A_quantity_outside_the_bound_is_refused(int quantity)
        {
            var economy = new EconomyService(new InMemoryEntitlementLedger(), new FixedTime());

            Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await economy.PurchasePackAsync(AuthenticatedAccount(),
                    new BoosterPack(SetId.EMBERFALL), EconomyCurrency.Gold, "purchase-1", quantity));
        }

        [Test]
        public async Task Pack_Purchase_With_199_Gems_Leaves_The_Wallet_And_Pack_Unchanged()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gems, 199, "play_billing", "store-1", At));
            var economy = new EconomyService(ledger, new FixedTime());
            var pack = new BoosterPack(SetId.EMBERFALL);

            PackPurchaseOutcome result = await economy.PurchasePackAsync(
                account, pack, EconomyCurrency.Gems, "purchase-1");

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(PackPurchaseStatus.InsufficientFunds));
                Assert.That(result.Wallet.Gold, Is.EqualTo(0));
                Assert.That(result.Wallet.Gems, Is.EqualTo(199));
            });
            Assert.That(await ledger.BalanceAsync(account.PlayerId, pack.Id), Is.EqualTo(0));
            Assert.That(await ledger.HistoryAsync(account.PlayerId), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Pack_Purchase_Rejects_A_Device_Token_Account_Before_Any_Ledger_Write()
        {
            var ledger = new InMemoryEntitlementLedger();
            var account = new Account("player-1", "device_token", "subject-hash", At);
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 200, "quest", "quest-1", At));
            var economy = new EconomyService(ledger, new FixedTime());

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await economy.PurchasePackAsync(
                    account, new BoosterPack(SetId.EMBERFALL), EconomyCurrency.Gold, "purchase-1"));

            Assert.That(await ledger.HistoryAsync(account.PlayerId), Has.Count.EqualTo(1));
            Assert.That(await ledger.BalanceAsync(account.PlayerId, EconomyItems.Gold), Is.EqualTo(200));
        }

        [Test]
        public async Task Retrying_A_Pack_Purchase_Replays_Without_Spending_Twice()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            await ledger.GrantAsync(new EntitlementGrant(
                account.PlayerId, EconomyItems.Gold, 400, "quest", "quest-1", At));
            var economy = new EconomyService(ledger, new FixedTime());
            var pack = new BoosterPack(SetId.EMBERFALL);

            PackPurchaseOutcome first = await economy.PurchasePackAsync(
                account, pack, EconomyCurrency.Gold, "purchase-1");
            PackPurchaseOutcome replay = await economy.PurchasePackAsync(
                account, pack, EconomyCurrency.Gold, "purchase-1");

            Assert.Multiple(() =>
            {
                Assert.That(first.Status, Is.EqualTo(PackPurchaseStatus.Purchased));
                Assert.That(replay.Status, Is.EqualTo(PackPurchaseStatus.Replayed));
                Assert.That(replay.Wallet.Gold, Is.EqualTo(200));
            });
            Assert.That(await ledger.BalanceAsync(account.PlayerId, pack.Id), Is.EqualTo(1));
            Assert.That(await ledger.HistoryAsync(account.PlayerId), Has.Count.EqualTo(3));
        }

        [TestCase(GoldRewardKind.DailyQuest, "daily_quest")]
        [TestCase(GoldRewardKind.FirstWinOfDay, "first_win")]
        [TestCase(GoldRewardKind.Match, "match_reward")]
        public async Task Gold_Faucets_Grant_A_Caller_Supplied_Amount(
            GoldRewardKind kind,
            string expectedSource)
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            var economy = new EconomyService(ledger, new FixedTime());

            WalletBalances wallet = await economy.GrantGoldAsync(
                account, kind, 75, "event-1");

            Assert.That(wallet.Gold, Is.EqualTo(75));
            var history = await ledger.HistoryAsync(account.PlayerId);
            Assert.Multiple(() =>
            {
                Assert.That(history, Has.Count.EqualTo(1));
                Assert.That(history[0].Source, Is.EqualTo(expectedSource));
                Assert.That(history[0].Qty, Is.EqualTo(75));
            });
        }

        [Test]
        public async Task Retrying_The_Same_Gold_Reward_Does_Not_Grant_Twice()
        {
            var ledger = new InMemoryEntitlementLedger();
            Account account = AuthenticatedAccount();
            var economy = new EconomyService(ledger, new FixedTime());

            await economy.GrantGoldAsync(account, GoldRewardKind.Match, 25, "match-1");
            WalletBalances replay = await economy.GrantGoldAsync(
                account, GoldRewardKind.Match, 25, "match-1");

            Assert.That(replay.Gold, Is.EqualTo(25));
            Assert.That(await ledger.HistoryAsync(account.PlayerId), Has.Count.EqualTo(1));
        }
    }
}
