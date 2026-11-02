using System;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Ranking;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The guard on the failure this worker was written to end: <see cref="GoldFaucet"/> shipped
    /// complete and tested with ZERO production callers, so a finished match paid nobody. Every test
    /// here drives the worker rather than the faucet for that reason — a faucet test cannot tell the
    /// difference between "wired" and "wired to nothing".
    ///
    /// <para><b>These call <c>PayAsync</c> and never start the hosted service.</b> The queue is how
    /// the work gets off the match thread; it is not part of what a correct payment is, and driving
    /// a <c>BackgroundService</c>'s start/stop from a test fixture proved to be its own source of
    /// noise — several instances into one process, results stopped being observed at all. What
    /// matters, and what these assert, is that one finished match turns into the right ledger rows
    /// exactly once.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class MatchRewardWorkerTests
    {
        private sealed class StubGoldRewardPolicy : IGoldRewardPolicy
        {
            public long MatchGold => 10;
            public long FirstWinGold => 20;
            public long DailyQuestGold(string questId) => 50;
        }

        private static (MatchRewardWorker Worker, InMemoryEntitlementLedger Ledger) WorkerOver(
            InMemoryAccountStore accounts,
            Func<AccountId, bool>? isBotAccount = null)
        {
            var ledger = new InMemoryEntitlementLedger();
            var worker = new MatchRewardWorker(
                new GoldFaucet(ledger, new FakeTime(), new StubGoldRewardPolicy()),
                accounts,
                isBotAccount);
            return (worker, ledger);
        }

        private static async Task<InMemoryEntitlementLedger> PayAsync(
            InMemoryAccountStore accounts,
            params RankedMatchResult[] results)
        {
            var (worker, ledger) = WorkerOver(accounts);
            foreach (RankedMatchResult result in results) await worker.PayAsync(result);
            return ledger;
        }

        private static async Task<(InMemoryAccountStore Accounts, string P0, string P1)> TwoPlayersAsync()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account p0 = await accounts.ResolveOrCreateAsync("supabase", "subject-0");
            Account p1 = await accounts.ResolveOrCreateAsync("supabase", "subject-1");
            return (accounts, p0.PlayerId, p1.PlayerId);
        }

        private static RankedMatchResult Result(
            string match,
            string p0PlayerId,
            string p1PlayerId,
            MatchOutcome outcome,
            TelemetrySource source = TelemetrySource.Ladder,
            MatchEndReason reason = MatchEndReason.Win)
            => Result(match, AccountId.FromAuthenticatedAccount(p0PlayerId),
                      AccountId.FromAuthenticatedAccount(p1PlayerId), outcome, source, reason);

        private static RankedMatchResult Result(
            string match,
            AccountId p0,
            AccountId p1,
            MatchOutcome outcome,
            TelemetrySource source = TelemetrySource.Ladder,
            MatchEndReason reason = MatchEndReason.Win)
            => new RankedMatchResult(
                new MatchId(match),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                p0, "p0", p1, "p1", outcome, source, reason);

        [Test]
        public async Task A_Friend_Challenge_Win_Pays_The_Match_Reward_But_Never_The_First_Win()
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger = await PayAsync(accounts,
                Result("match-1", p0, p1, MatchOutcome.Player0Win, TelemetrySource.Friendly));

            long winner = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long loser = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                // The match reward alone, the same as the loser's: no first win from a friendly.
                Assert.That(winner, Is.EqualTo(10));
                Assert.That(loser, Is.EqualTo(10));
            });
        }

        [TestCase(MatchEndReason.Concede)]
        [TestCase(MatchEndReason.Timeout)]
        public async Task A_Forfeiting_Seat_Is_Paid_Nothing_And_The_Winner_Is_Still_Paid(MatchEndReason reason)
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger = await PayAsync(accounts,
                Result("match-1", p0, p1, MatchOutcome.Player1Win, reason: reason));

            long conceder = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long winner = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(conceder, Is.EqualTo(0));
                // The winner's match reward and first win of the day, as in any win.
                Assert.That(winner, Is.EqualTo(30));
            });
        }

        [Test]
        public async Task A_Finished_Ladder_Match_Pays_Both_Seats_And_The_Winner_Twice()
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger =
                await PayAsync(accounts, Result("match-1", p0, p1, MatchOutcome.Player0Win));

            long winner = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long loser = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                // Winner: the match reward plus the first win of the day.
                Assert.That(winner, Is.EqualTo(30));
                // Loser: the match reward alone. Losing pays, or a losing streak reads as wasted time.
                Assert.That(loser, Is.EqualTo(10));
            });
        }

        [Test]
        public async Task The_First_Win_Bonus_Is_Paid_Once_Per_Utc_Day_Across_Matches()
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts,
                Result("match-1", p0, p1, MatchOutcome.Player0Win),
                Result("match-2", p0, p1, MatchOutcome.Player0Win));

            // 10 + 20 for the first win, 10 for the second. The day bonus does not repeat.
            Assert.That(await ledger.BalanceAsync(p0, EconomyItems.Gold), Is.EqualTo(40));
        }

        [Test]
        public async Task Replaying_One_Match_Pays_Exactly_Once()
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts,
                Result("match-1", p0, p1, MatchOutcome.Player0Win),
                Result("match-1", p0, p1, MatchOutcome.Player0Win));

            long winner = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long loser = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(winner, Is.EqualTo(30));
                Assert.That(loser, Is.EqualTo(10));
            });
        }

        [Test]
        public async Task Bot_fill_pays_the_human_and_never_the_roster_bot()
        {
            var (accounts, human, bot) = await TwoPlayersAsync();
            var (worker, ledger) = WorkerOver(
                accounts,
                account => account == AccountId.FromAuthenticatedAccount(bot));

            await worker.PayAsync(Result(
                "match-1", human, bot, MatchOutcome.Player0Win, TelemetrySource.BotFill));

            long humanGold = await ledger.BalanceAsync(human, EconomyItems.Gold);
            long botGold = await ledger.BalanceAsync(bot, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(humanGold, Is.EqualTo(30));
                Assert.That(botGold, Is.Zero);
            });
        }

        [Test]
        public async Task Bot_fill_without_a_roster_identity_predicate_fails_closed()
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts,
                Result("match-1", p0, p1, MatchOutcome.Player0Win, TelemetrySource.BotFill));

            long p0Gold = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long p1Gold = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(p0Gold, Is.Zero);
                Assert.That(p1Gold, Is.Zero);
            });
        }

        [Test]
        public async Task Ai_soak_does_not_pay()
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts, Result("match-1", p0, p1, MatchOutcome.Player0Win, TelemetrySource.AiSoak));

            long winner = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long loser = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(winner, Is.Zero);
                Assert.That(loser, Is.Zero);
            });
        }

        [Test]
        public async Task A_Draw_Pays_The_Match_Reward_And_No_First_Win()
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger =
                await PayAsync(accounts, Result("match-1", p0, p1, MatchOutcome.Draw));

            long first = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long second = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(10));
                Assert.That(second, Is.EqualTo(10));
            });
        }

        [Test]
        public async Task A_Seat_With_No_Account_Behind_It_Is_Skipped_Without_Costing_The_Other()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account p0 = await accounts.ResolveOrCreateAsync("supabase", "subject-0");
            // A tokenless tooling seat never reaches the account store at all: its AccountId is derived from the
            // device token, so the lookup misses. That is the shape this asserts — not an error,
            // just nobody to pay.
            AccountId tokenless = AccountId.FromDeviceToken("device-token-tokenless");

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts,
                Result("match-1", AccountId.FromAuthenticatedAccount(p0.PlayerId), tokenless,
                       MatchOutcome.Player0Win));

            long paid = await ledger.BalanceAsync(p0.PlayerId, EconomyItems.Gold);
            long unpaid = await ledger.BalanceAsync(tokenless.Value, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(paid, Is.EqualTo(30));
                Assert.That(unpaid, Is.Zero);
            });
        }

        [Test]
        public async Task An_Unattributed_Match_Pays_Nobody()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account p0 = await accounts.ResolveOrCreateAsync("supabase", "subject-0");

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts,
                Result("match-1", AccountId.FromAuthenticatedAccount(p0.PlayerId),
                       AccountId.Unattributed, MatchOutcome.Player0Win));

            Assert.That(await ledger.BalanceAsync(p0.PlayerId, EconomyItems.Gold), Is.Zero);
        }
    }

    [TestFixture]
    [Category("Unit")]
    public sealed class CompositeRankedMatchSinkTests
    {
        private sealed class CountingSink : IRankedMatchSink
        {
            public int Records;
            public void Record(RankedMatchResult result) => Records++;
        }

        private sealed class ThrowingSink : IRankedMatchSink
        {
            public void Record(RankedMatchResult result) => throw new InvalidOperationException("boom");
        }

        [Test]
        public void One_Broken_Sink_Does_Not_Cost_The_Others_Their_Delivery()
        {
            var first = new CountingSink();
            var last = new CountingSink();
            var sink = new CompositeRankedMatchSink(first, new ThrowingSink(), last);

            sink.Record(new RankedMatchResult(
                new MatchId("match-1"),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                AccountId.FromAuthenticatedAccount("p0"), "p0",
                AccountId.FromAuthenticatedAccount("p1"), "p1",
                MatchOutcome.Player0Win,
                TelemetrySource.Ladder));

            Assert.Multiple(() =>
            {
                Assert.That(first.Records, Is.EqualTo(1));
                Assert.That(last.Records, Is.EqualTo(1), "a sink after the thrower was skipped");
            });
        }
    }
}
