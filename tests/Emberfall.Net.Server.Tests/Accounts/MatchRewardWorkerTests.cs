using System;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Ranking;
using Emberfall.Net.Server.Telemetry;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Accounts
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
            InMemoryAccountStore accounts)
        {
            var ledger = new InMemoryEntitlementLedger();
            var worker = new MatchRewardWorker(
                new GoldFaucet(ledger, new FakeTime(), new StubGoldRewardPolicy()), accounts);
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
            TelemetrySource source = TelemetrySource.Ladder)
            => Result(match, AccountId.FromAuthenticatedAccount(p0PlayerId),
                      AccountId.FromAuthenticatedAccount(p1PlayerId), outcome, source);

        private static RankedMatchResult Result(
            string match,
            AccountId p0,
            AccountId p1,
            MatchOutcome outcome,
            TelemetrySource source = TelemetrySource.Ladder)
            => new RankedMatchResult(
                new MatchId(match),
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                p0, "p0", p1, "p1", outcome, source);

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

        [TestCase(TelemetrySource.BotFill)]
        [TestCase(TelemetrySource.AiSoak)]
        public async Task Only_A_Ladder_Match_Pays(TelemetrySource source)
        {
            var (accounts, p0, p1) = await TwoPlayersAsync();

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts, Result("match-1", p0, p1, MatchOutcome.Player0Win, source));

            long winner = await ledger.BalanceAsync(p0, EconomyItems.Gold);
            long loser = await ledger.BalanceAsync(p1, EconomyItems.Gold);

            // The whole anti-farm posture rests on this: a bot cannot be beaten for Gold, so the
            // uncapped per-match reward needs a second real account to exploit rather than a script.
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
            // A guest never reaches the account store at all: its AccountId is derived from the
            // device token, so the lookup misses. That is the shape this asserts — not an error,
            // just nobody to pay.
            AccountId guest = AccountId.FromDeviceToken("device-token-guest");

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts,
                Result("match-1", AccountId.FromAuthenticatedAccount(p0.PlayerId), guest,
                       MatchOutcome.Player0Win));

            long paid = await ledger.BalanceAsync(p0.PlayerId, EconomyItems.Gold);
            long unpaid = await ledger.BalanceAsync(guest.Value, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(paid, Is.EqualTo(30));
                Assert.That(unpaid, Is.Zero);
            });
        }

        [Test]
        public async Task A_Guest_Account_On_The_Store_Is_Refused_Rather_Than_Paid()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account guest = await accounts.ResolveOrCreateAsync("device_token", "token-hash");
            Account durable = await accounts.ResolveOrCreateAsync("supabase", "subject-1");

            InMemoryEntitlementLedger ledger = await PayAsync(
                accounts,
                Result("match-1", guest.PlayerId, durable.PlayerId, MatchOutcome.Player0Win));

            long guestGold = await ledger.BalanceAsync(guest.PlayerId, EconomyItems.Gold);
            long durableGold = await ledger.BalanceAsync(durable.PlayerId, EconomyItems.Gold);

            // Currency may not move for an unlinked account (EconomyAccountPolicy). The worker asks
            // before granting so the faucet's throw never becomes a logged error per guest match.
            Assert.Multiple(() =>
            {
                Assert.That(guestGold, Is.Zero);
                Assert.That(durableGold, Is.EqualTo(10));
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
