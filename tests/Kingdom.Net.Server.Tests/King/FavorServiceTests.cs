using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary>
    /// Banking a seat's commission score into the King's Favor at match end, and the one-time
    /// qualifying gold. The failures worth a test are the ones nobody would report: a friend match
    /// on the board, a bot on the board, a loss placing, and a match that crossed Tuesday midnight
    /// scoring for the wrong King.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class FavorServiceTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        private static readonly FavorPrizeSettings Prizes = new FavorPrizeSettings(new long[] { 500, 300, 100 }, 200, 50);

        private sealed class Harness
        {
            public FakeTime Time { get; } = new FakeTime();
            public InMemoryAccountStore Accounts { get; }
            public InMemoryFavorStore Store { get; } = new InMemoryFavorStore();
            public InMemoryEntitlementLedger Ledger { get; } = new InMemoryEntitlementLedger();
            public string PlayerId { get; private set; } = "";

            public Harness() => Accounts = new InMemoryAccountStore(Time);

            public static async Task<Harness> CreateAsync()
            {
                var h = new Harness();
                h.PlayerId = (await h.Accounts.ResolveOrCreateAsync("supabase", "subject-0")).PlayerId;
                return h;
            }

            public FavorService Service(FavorPrizeSettings? prizes = null)
                => new FavorService(Store, Accounts, new EconomyService(Ledger, Time), prizes ?? Prizes);

            public Task<long> GoldAsync(string playerId) => Ledger.BalanceAsync(playerId, EconomyItems.Gold);
        }

        private static KingSeating King(string commission, int reign = 4)
            => new KingSeating(reign, commission, null, null, "Alice", "Bob", true, true);

        private static AchievementMatchFacts Facts(
            string playerId,
            TelemetrySource source = TelemetrySource.Ladder,
            SeatResult result = SeatResult.Win,
            MatchEndReason reason = MatchEndReason.Win,
            int overkill = 7,
            int finalHealth = 12,
            int selfDamage = 9,
            DateTimeOffset? endedAt = null)
            => new AchievementMatchFacts(
                "match-1", playerId, endedAt ?? Now, source, result, reason,
                TurnCount: 9, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0,
                FinalHealth: finalHealth, SelfDamage: selfDamage, OverkillBest: overkill);

        // ── Who banks ─────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Banks_a_ladder_score()
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId), King(CommissionIds.Overkill), "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(7));
            Assert.That((await h.Store.TopAsync(4, false, 10)).Single(),
                Is.EqualTo(new FavorRow(1, "Alice", 7, h.PlayerId)), "the board shows the name the seat played under");
        }

        [Test]
        public async Task Banks_a_botfill_score_for_the_human_seat()
        {
            // A bot-filled match has to count exactly like a human one, or the board gives the bot away.
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, TelemetrySource.BotFill), King(CommissionIds.Overkill),
                "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(7));
        }

        [TestCase(TelemetrySource.Friendly)]
        [TestCase(TelemetrySource.AiSoak)]
        public async Task Skips_friendly_and_aisoak(TelemetrySource source)
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, source), King(CommissionIds.Overkill), "Alice",
                CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.Null);
            Assert.That(await h.GoldAsync(h.PlayerId), Is.Zero);
        }

        [TestCase(TelemetrySource.Ladder)]
        [TestCase(TelemetrySource.BotFill)]
        public async Task Skips_superuser_accounts(TelemetrySource source)
        {
            // Bots are Superuser accounts, so this is also the bot's own seat in a bot-filled match.
            var h = await Harness.CreateAsync();
            Account bot = await h.Accounts.ResolveOrCreateAsync("bot", "roster-bot-0");
            await h.Accounts.SetKindAsync(bot.PlayerId, AccountKind.Superuser);

            await h.Service().ApplyAsync(Facts(bot.PlayerId, source), King(CommissionIds.Overkill), "ember_knight",
                CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, bot.PlayerId), Is.Null);
            Assert.That(await h.Store.TopAsync(4, false, 10), Is.Empty);
            Assert.That(await h.GoldAsync(bot.PlayerId), Is.Zero);
        }

        [Test]
        public async Task A_seat_with_no_account_row_records_nothing()
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts("device-token-hash"), King(CommissionIds.Overkill), "Alice",
                CancellationToken.None);

            Assert.That(await h.Store.TopAsync(4, false, 10), Is.Empty);
        }

        // ── What places ───────────────────────────────────────────────────────────────────

        [Test]
        public async Task Win_only_commission_on_a_loss_records_nothing()
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, result: SeatResult.Loss), King(CommissionIds.SelfStarter),
                "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.Null);
        }

        [Test]
        public async Task A_loss_still_banks_a_commission_that_needs_no_win()
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, result: SeatResult.Loss), King(CommissionIds.Overkill),
                "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(7));
        }

        [TestCase(MatchEndReason.Concede)]
        [TestCase(MatchEndReason.Timeout)]
        public async Task A_concede_win_counts(MatchEndReason reason)
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, reason: reason), King(CommissionIds.SelfStarter),
                "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(9));
        }

        [Test]
        public async Task Zero_does_not_place()
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, overkill: 0), King(CommissionIds.Overkill), "Alice",
                CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.Null);
            Assert.That(await h.GoldAsync(h.PlayerId), Is.Zero, "a score that does not place does not qualify");
        }

        [TestCase(0)]
        [TestCase(-3)]
        public async Task Technically_records_zero_and_negative_on_a_win(int health)
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, finalHealth: health), King(CommissionIds.Technically),
                "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(health));
        }

        [Test]
        public async Task Technically_on_a_loss_records_nothing()
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, result: SeatResult.Loss, finalHealth: 0),
                King(CommissionIds.Technically), "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.Null);
        }

        [Test]
        public async Task A_match_started_before_midnight_banks_to_its_starting_reign()
        {
            // Review Focus 1: the seating fixed the reign when the match began. The end time is
            // already in the next reign and must not move the score there.
            const long reignOne = 1_793_664_000_000;   // 2026-11-03T00:00Z, a Tuesday
            DateTimeOffset endedInReignFive = DateTimeOffset.FromUnixTimeMilliseconds(
                KingCalendar.StartMs(5, reignOne) + 60_000);
            Assert.That(KingCalendar.ReignAt(endedInReignFive.ToUnixTimeMilliseconds(), reignOne), Is.EqualTo(5));
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId, endedAt: endedInReignFive),
                King(CommissionIds.Overkill, reign: 4), "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(7));
            Assert.That(await h.Store.BestAsync(5, h.PlayerId), Is.Null);
        }

        // ── Qualifying gold ───────────────────────────────────────────────────────────────

        [Test]
        public async Task First_qualifying_score_in_a_reign_pays_QualifyGold()
        {
            var h = await Harness.CreateAsync();

            await h.Service().ApplyAsync(Facts(h.PlayerId), King(CommissionIds.Overkill), "Alice", CancellationToken.None);

            Assert.That(await h.GoldAsync(h.PlayerId), Is.EqualTo(Prizes.QualifyGold));
            IReadOnlyList<EntitlementGrant> history = await h.Ledger.HistoryAsync(h.PlayerId);
            Assert.That(history.Single().Source, Is.EqualTo("kings_favor"));
        }

        [Test]
        public async Task A_later_score_in_the_reign_pays_nothing()
        {
            var h = await Harness.CreateAsync();
            FavorService service = h.Service();

            await service.ApplyAsync(Facts(h.PlayerId, overkill: 3), King(CommissionIds.Overkill), "Alice",
                CancellationToken.None);
            await service.ApplyAsync(Facts(h.PlayerId, overkill: 9), King(CommissionIds.Overkill), "Alice",
                CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(9), "the better score still banks");
            Assert.That(await h.GoldAsync(h.PlayerId), Is.EqualTo(Prizes.QualifyGold), "but qualifying pays once");
        }

        [Test]
        public async Task The_next_reign_pays_QualifyGold_again()
        {
            var h = await Harness.CreateAsync();
            FavorService service = h.Service();

            await service.ApplyAsync(Facts(h.PlayerId), King(CommissionIds.Overkill, reign: 4), "Alice",
                CancellationToken.None);
            await service.ApplyAsync(Facts(h.PlayerId), King(CommissionIds.Overkill, reign: 5), "Alice",
                CancellationToken.None);

            Assert.That(await h.GoldAsync(h.PlayerId), Is.EqualTo(2 * Prizes.QualifyGold));
        }

        [Test]
        public async Task No_prize_settings_still_banks_and_pays_nothing()
        {
            var h = await Harness.CreateAsync();
            var service = new FavorService(h.Store, h.Accounts, new EconomyService(h.Ledger, h.Time), prizes: null);

            await service.ApplyAsync(Facts(h.PlayerId), King(CommissionIds.Overkill), "Alice", CancellationToken.None);

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(7));
            Assert.That(await h.GoldAsync(h.PlayerId), Is.Zero);
        }

        /// <summary>The in-memory ledger, failing its first <paramref name="failures"/> batch writes.</summary>
        private sealed class FlakyLedger : IEntitlementLedger
        {
            private readonly InMemoryEntitlementLedger _inner;
            private int _failures;
            public FlakyLedger(InMemoryEntitlementLedger inner, int failures) { _inner = inner; _failures = failures; }

            public Task<EntitlementBatchResult> ApplyBatchAsync(EntitlementBatch batch, CancellationToken ct = default)
                => _failures-- > 0
                    ? throw new InvalidOperationException("ledger is down")
                    : _inner.ApplyBatchAsync(batch, ct);
            public Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default) => _inner.GrantAsync(grant, ct);
            public Task<IAsyncDisposable> AcquirePlayerMutationLockAsync(string playerId, CancellationToken ct = default)
                => _inner.AcquirePlayerMutationLockAsync(playerId, ct);
            public Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default) => _inner.BalanceAsync(playerId, item, ct);
            public Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default) => _inner.BalancesAsync(playerId, ct);
            public Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default) => _inner.HistoryAsync(playerId, ct);
            public Task<EntitlementGrant?> FindAsync(string externalTxnId, CancellationToken ct = default) => _inner.FindAsync(externalTxnId, ct);
        }

        [Test]
        public async Task A_failed_qualifying_grant_is_paid_by_the_next_qualifying_score()
        {
            // The score banks before the gold. A grant that fails after it must not cost the gold for
            // good: the next qualifying score in the reign pays it, and only once.
            var h = await Harness.CreateAsync();
            var service = new FavorService(h.Store, h.Accounts,
                new EconomyService(new FlakyLedger(h.Ledger, failures: 1), h.Time), Prizes);

            await service.ApplyAsync(Facts(h.PlayerId, overkill: 5), King(CommissionIds.Overkill), "Alice", CancellationToken.None);
            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(5), "the score banked");
            Assert.That(await h.GoldAsync(h.PlayerId), Is.Zero, "the grant failed");

            await service.ApplyAsync(Facts(h.PlayerId, overkill: 2), King(CommissionIds.Overkill), "Alice", CancellationToken.None);
            await service.ApplyAsync(Facts(h.PlayerId, overkill: 8), King(CommissionIds.Overkill), "Alice", CancellationToken.None);

            Assert.That(await h.GoldAsync(h.PlayerId), Is.EqualTo(Prizes.QualifyGold));
            Assert.That(await h.Ledger.HistoryAsync(h.PlayerId), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_conflicting_qualifying_grant_counts_as_already_paid()
        {
            // QualifyGold changed mid-reign: the key is already paid at the old amount. The score still
            // banks, nothing more is paid, and the worker logs nothing about this match.
            var h = await Harness.CreateAsync();
            await h.Service().ApplyAsync(Facts(h.PlayerId, overkill: 3), King(CommissionIds.Overkill), "Alice",
                CancellationToken.None);
            var raised = new FavorService(h.Store, h.Accounts, new EconomyService(h.Ledger, h.Time),
                Prizes with { QualifyGold = Prizes.QualifyGold + 25 });
            var sink = new AchievementSink(NullTelemetrySink.Instance,
                new AchievementService(new InMemoryAchievementStore(), h.Accounts), raised);
            var complete = new MatchCompleteEvent(
                new MatchId("conflict-match"), Now, MatchEndReason.Win, MatchOutcome.Player0Win, 9, TelemetrySource.Ladder,
                new SeatOutcome(AccountId.FromAuthenticatedAccount(h.PlayerId), CardClass.EMBERWARD, "deck-0", "hash-0",
                    SeatResult.Win, Array.Empty<int>(), OverkillBest: 9),
                new SeatOutcome(AccountId.Unattributed, CardClass.TIDECALLER, "deck-1", "hash-1", SeatResult.Loss,
                    Array.Empty<int>()));

            var err = new System.IO.StringWriter();
            System.IO.TextWriter original = Console.Error;
            Console.SetError(err);
            try
            {
                await sink.EvaluateAsync(complete, King(CommissionIds.Overkill));
            }
            finally
            {
                Console.SetError(original);
            }

            Assert.That(await h.Store.BestAsync(4, h.PlayerId), Is.EqualTo(9), "the better score banked");
            Assert.That(await h.GoldAsync(h.PlayerId), Is.EqualTo(Prizes.QualifyGold), "paid once, at the first amount");
            Assert.That(err.ToString(), Does.Not.Contain("conflict-match").And.Not.Contain("[king]"));
        }

        // ── The prize settings ────────────────────────────────────────────────────────────

        [Test]
        public void Prize_settings_parse_from_json()
        {
            FavorPrizeSettings? parsed = FavorPrizeSettings.Parse(
                "{\"gold\":[500,300,100],\"poolEmptyGold\":200,\"qualifyGold\":50}");

            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.Gold, Is.EqualTo(new long[] { 500, 300, 100 }));
            Assert.That(parsed.PoolEmptyGold, Is.EqualTo(200));
            Assert.That(parsed.QualifyGold, Is.EqualTo(50));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("{\"gold\":[500],\"poolEmptyGold\":200}")]
        [TestCase("{\"gold\":[500],\"poolEmptyGold\":200,\"qualifyGold\":50,\"qualifygold\":5}")]
        [TestCase("{\"gold\":[500,-1],\"poolEmptyGold\":200,\"qualifyGold\":50}")]
        [TestCase("{\"gold\":500,\"poolEmptyGold\":200,\"qualifyGold\":50}")]
        [TestCase("{\"gold\":[500],\"poolEmptyGold\":\"200\",\"qualifyGold\":50}")]
        [TestCase("{\"gold\":[500],\"poolEmptyGold\":200,\"qualifyGold\":-50}")]
        [TestCase("{\"gold\":[1.5],\"poolEmptyGold\":200,\"qualifyGold\":50}")]
        public void Unset_or_invalid_prize_settings_pay_nothing(string? raw)
            => Assert.That(FavorPrizeSettings.Parse(raw), Is.Null);

        // ── The seating ───────────────────────────────────────────────────────────────────

        private const long ReignOne = 1_793_664_000_000;   // 2026-11-03T00:00Z

        [TestCase(TelemetrySource.Ladder)]
        [TestCase(TelemetrySource.BotFill)]
        public async Task A_ladder_or_botfill_match_is_seated_with_its_King_and_both_bests(TelemetrySource source)
        {
            var store = new InMemoryFavorStore();
            await store.RecordAsync(new FavorScore(2, "player-a", "A", 6, Now), lowerIsBetter: false);
            long inReignTwo = KingCalendar.StartMs(2, ReignOne) + 1_000;

            KingSeating? king = await KingSeating.ForMatchAsync(
                store, source, ReignOne, inReignTwo,
                AccountId.FromAuthenticatedAccount("player-a"), AccountId.FromAuthenticatedAccount("player-b"),
                "A", "B", true, true, CancellationToken.None);

            Assert.That(king, Is.EqualTo(new KingSeating(2, KingSchedule.For(2).Commission, 6, null, "A", "B", true, true)));
        }

        [TestCase(TelemetrySource.Friendly)]
        [TestCase(TelemetrySource.AiSoak)]
        public async Task A_friendly_or_soak_match_has_no_King(TelemetrySource source)
        {
            KingSeating? king = await KingSeating.ForMatchAsync(
                new InMemoryFavorStore(), source, ReignOne, ReignOne + 1_000,
                AccountId.FromAuthenticatedAccount("player-a"), AccountId.FromAuthenticatedAccount("player-b"),
                "A", "B", true, true, CancellationToken.None);

            Assert.That(king, Is.Null);
        }

        [Test]
        public async Task A_match_before_reign_one_has_no_King()
        {
            KingSeating? king = await KingSeating.ForMatchAsync(
                new InMemoryFavorStore(), TelemetrySource.Ladder, ReignOne, ReignOne - 1,
                AccountId.FromAuthenticatedAccount("player-a"), AccountId.FromAuthenticatedAccount("player-b"),
                "A", "B", true, true, CancellationToken.None);

            Assert.That(king, Is.Null);
        }

        // ── Through the match and the sink ────────────────────────────────────────────────

        private sealed class ThrowingFavorStore : IFavorStore
        {
            public Task<int?> RecordAsync(FavorScore score, bool lowerIsBetter, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<int?> BestAsync(int reign, string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<IReadOnlyList<FavorRow>> TopAsync(int reign, bool lowerIsBetter, int limit, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<int?> PlaceAsync(int reign, string playerId, bool lowerIsBetter, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<IReadOnlyList<RollRow>> RollAsync(int limit, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<IReadOnlyList<int>> ReignsPlayedAsync(string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<IReadOnlyList<UnpaidReign>> UnpaidReignsAsync(int beforeReign, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<bool> HasHeldPlaceAsync(string playerId, int place, int beforeReign, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task RecordPrizeAsync(FavorPrize prize, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task ClosePaidAsync(int reign, string? favoriteId, string? favoriteName, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<FavorPrize?> PrizeAsync(int reign, string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<IReadOnlyList<FavorPrize>> UnsettledBacksAsync(int throughPlace, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task SettleBackAsync(int reign, string playerId, int? backId, bool choiceOpen, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<IReadOnlyList<FavorPrize>> OpenChoicesAsync(string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<bool> CloseChoiceAsync(int reign, string playerId, int? backId, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task<IReadOnlyList<FavorPrize>> UndeliveredChoicesAsync(string? playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
            public Task MarkChoiceDeliveredAsync(int reign, string playerId, CancellationToken ct = default)
                => throw new InvalidOperationException("favor store is down");
        }

        /// <summary>One ladder match between the harness player (seat 0) and a second account,
        /// emitting through a started <see cref="AchievementSink"/>.</summary>
        private static async Task<(MatchSession Session, FakeConnection C0, FakeConnection C1, ScriptedClient Client1, string Other)> LiveMatchAsync(
            Harness h, AchievementSink sink, KingSeating king)
        {
            string other = (await h.Accounts.ResolveOrCreateAsync("supabase", "subject-1")).PlayerId;
            var id = new MatchId("m-king");
            var session = new MatchSession(
                id, SessionHarness.Seed,
                new ServerOptions { HeartbeatTimeoutMs = long.MaxValue / 4, StallGuardMs = long.MaxValue / 4 },
                h.Time, SessionHarness.Db, PrebuiltDecks.EmberwardVsTidecaller(), new InMemoryReplayStore(),
                new PlayerId("p0"), new PlayerId("p1"), sink,
                new MatchAttribution(AccountId.FromAuthenticatedAccount(h.PlayerId),
                    AccountId.FromAuthenticatedAccount(other), TelemetrySource.Ladder),
                king: king);
            var c0 = new FakeConnection();
            var c1 = new FakeConnection();
            session.Start(c0, c1);
            var client0 = new ScriptedClient(new PlayerId("p0"));
            var client1 = new ScriptedClient(new PlayerId("p1"));
            session.Dispatch(new SessionCommand.Frame(c0, client0.Intent(new MulliganIntent(new PlayerId("p0"), Array.Empty<InstanceId>()), id)));
            session.Dispatch(new SessionCommand.Frame(c1, client1.Intent(new MulliganIntent(new PlayerId("p1"), Array.Empty<InstanceId>()), id)));
            return (session, c0, c1, client1, other);
        }

        [Test]
        public async Task A_finished_match_banks_through_the_session_and_the_sink()
        {
            var h = await Harness.CreateAsync();
            var sink = new AchievementSink(NullTelemetrySink.Instance,
                new AchievementService(new InMemoryAchievementStore(), h.Accounts), h.Service());
            await sink.StartAsync(CancellationToken.None);
            var (session, _, c1, client1, other) = await LiveMatchAsync(h, sink, King(CommissionIds.Technically));

            session.Dispatch(new SessionCommand.Frame(c1,
                client1.Intent(new ConcedeIntent(new PlayerId("p1")), new MatchId("m-king"))));
            await sink.StopAsync(CancellationToken.None);

            Assert.That(session.Ended, Is.True);
            Assert.That(await h.Store.BestAsync(4, h.PlayerId),
                Is.EqualTo(session.Host.State.Players[0].Hero.CurrentHealth), "the winner banks the Health they won at");
            Assert.That((await h.Store.TopAsync(4, true, 10)).Single().DisplayName, Is.EqualTo("Alice"));
            Assert.That(await h.Store.BestAsync(4, other), Is.Null, "Technically needs a win");
        }

        [Test]
        public async Task A_voided_match_records_nothing()
        {
            var h = await Harness.CreateAsync();
            var inner = new InMemoryTelemetrySink();
            var sink = new AchievementSink(inner,
                new AchievementService(new InMemoryAchievementStore(), h.Accounts), h.Service());
            await sink.StartAsync(CancellationToken.None);
            var (session, c0, c1, _, _) = await LiveMatchAsync(h, sink, King(CommissionIds.Technically));

            session.Dispatch(new SessionCommand.Disconnected(c0));
            session.Dispatch(new SessionCommand.Disconnected(c1));
            await sink.StopAsync(CancellationToken.None);

            Assert.That(session.Ended, Is.True, "both seats gone voids the match");
            Assert.That(inner.Voids, Has.Count.EqualTo(1));
            Assert.That(inner.Completes, Is.Empty, "a void never reaches the Favor, whatever the commission");
            Assert.That(await h.Store.TopAsync(4, true, 10), Is.Empty);
            Assert.That(await h.GoldAsync(h.PlayerId), Is.Zero);
        }

        [Test]
        public async Task A_favor_failure_does_not_disturb_achievements()
        {
            var h = await Harness.CreateAsync();
            var achievements = new InMemoryAchievementStore();
            var sink = new AchievementSink(NullTelemetrySink.Instance,
                new AchievementService(achievements, h.Accounts),
                new FavorService(new ThrowingFavorStore(), h.Accounts, new EconomyService(h.Ledger, h.Time), Prizes));
            AccountId p0 = AccountId.FromAuthenticatedAccount(h.PlayerId);
            var complete = new MatchCompleteEvent(
                new MatchId("match-1"), Now, MatchEndReason.Win, MatchOutcome.Player0Win, 9, TelemetrySource.Ladder,
                new SeatOutcome(p0, CardClass.EMBERWARD, "deck-0", "hash-0", SeatResult.Win, Array.Empty<int>(),
                    OverkillBest: 7),
                new SeatOutcome(AccountId.Unattributed, CardClass.TIDECALLER, "deck-1", "hash-1", SeatResult.Loss,
                    Array.Empty<int>()));

            await sink.EvaluateAsync(complete, King(CommissionIds.Overkill));

            Assert.That((await achievements.ReadAsync(h.PlayerId)).Select(a => a.AchievementId),
                Does.Contain("against_advice"));
        }
    }
}
