using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.King;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary>
    /// The Tuesday sweep: gold by place for every closed reign, then the top three's card backs.
    /// The failures worth a test are the ones nobody reports: a reign paid twice after a crash, a
    /// reign never paid after downtime, the next player promoted over a deleted one, and a back
    /// lost because its art landed after the reign closed.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class KingPayoutSweepTests
    {
        private static readonly long ReignOne = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        private static readonly long Hour = 60L * 60 * 1000;
        private static readonly FavorPrizeSettings Prizes = new FavorPrizeSettings(new long[] { 500, 300, 200, 100, 50 }, 150, 50);
        private static readonly int[] PlaceBacks = { 101, 102, 103 };
        private static readonly int[] Pool = { 201, 202 };

        private sealed class Clock : ITimeSource
        {
            public long NowMs { get; set; }
        }

        /// <summary>Card backs as the ledger keys them, without the catalog: the prize backs do not
        /// exist in <see cref="CardBackDefinitions"/> yet, so the real seam cannot name them.</summary>
        private sealed class FakeBacks : IPrizeBacks
        {
            private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<(string Player, int Back)> Owned { get; } = new HashSet<(string, int)>();
            public List<string> Applied { get; } = new List<string>();
            public int Calls { get; private set; }

            public Task GrantAsync(Account account, int backId, string eventId, CancellationToken ct)
            {
                Calls++;
                string key = backId + ":" + account.PlayerId + ":" + eventId;
                if (_keys.Add(key))
                {
                    Applied.Add(key);
                    Owned.Add((account.PlayerId, backId));
                }
                return Task.CompletedTask;
            }

            public Task<bool> OwnsAsync(Account account, int backId, CancellationToken ct)
                => Task.FromResult(Owned.Contains((account.PlayerId, backId)));
        }

        /// <summary>The in-memory store, made to fail once on a chosen call.</summary>
        private sealed class FailingStore : IFavorStore
        {
            private readonly IFavorStore _inner;
            public int FailOnRecordPrize { get; set; }
            public int FailOnSettleBack { get; set; }
            private int _recordPrizes;
            private int _settles;

            public FailingStore(IFavorStore inner) => _inner = inner;

            public Task RecordPrizeAsync(FavorPrize prize, CancellationToken ct = default)
            {
                if (++_recordPrizes == FailOnRecordPrize) throw new InvalidOperationException("killed mid-reign");
                return _inner.RecordPrizeAsync(prize, ct);
            }

            public Task SettleBackAsync(int reign, string playerId, int? backId, bool choiceOpen, CancellationToken ct = default)
            {
                if (++_settles == FailOnSettleBack) throw new InvalidOperationException("killed mid-pass");
                return _inner.SettleBackAsync(reign, playerId, backId, choiceOpen, ct);
            }

            public Task<int?> RecordAsync(FavorScore score, bool lowerIsBetter, CancellationToken ct = default) => _inner.RecordAsync(score, lowerIsBetter, ct);
            public Task<int?> BestAsync(int reign, string playerId, CancellationToken ct = default) => _inner.BestAsync(reign, playerId, ct);
            public Task<IReadOnlyList<FavorRow>> TopAsync(int reign, bool lowerIsBetter, int limit, CancellationToken ct = default) => _inner.TopAsync(reign, lowerIsBetter, limit, ct);
            public Task<int?> PlaceAsync(int reign, string playerId, bool lowerIsBetter, CancellationToken ct = default) => _inner.PlaceAsync(reign, playerId, lowerIsBetter, ct);
            public Task<IReadOnlyList<RollRow>> RollAsync(int limit, CancellationToken ct = default) => _inner.RollAsync(limit, ct);
            public Task<IReadOnlyList<int>> ReignsPlayedAsync(string playerId, CancellationToken ct = default) => _inner.ReignsPlayedAsync(playerId, ct);
            public Task<IReadOnlyList<UnpaidReign>> UnpaidReignsAsync(int beforeReign, CancellationToken ct = default) => _inner.UnpaidReignsAsync(beforeReign, ct);
            public Task<bool> HasHeldPlaceAsync(string playerId, int place, int beforeReign, CancellationToken ct = default) => _inner.HasHeldPlaceAsync(playerId, place, beforeReign, ct);
            public Task ClosePaidAsync(int reign, string? favoriteId, string? favoriteName, CancellationToken ct = default) => _inner.ClosePaidAsync(reign, favoriteId, favoriteName, ct);
            public Task<FavorPrize?> PrizeAsync(int reign, string playerId, CancellationToken ct = default) => _inner.PrizeAsync(reign, playerId, ct);
            public Task<IReadOnlyList<FavorPrize>> UnsettledBacksAsync(int throughPlace, CancellationToken ct = default) => _inner.UnsettledBacksAsync(throughPlace, ct);
            public Task<IReadOnlyList<FavorPrize>> OpenChoicesAsync(string playerId, CancellationToken ct = default) => _inner.OpenChoicesAsync(playerId, ct);
            public Task<bool> CloseChoiceAsync(int reign, string playerId, int? backId, CancellationToken ct = default) => _inner.CloseChoiceAsync(reign, playerId, backId, ct);
            public Task<IReadOnlyList<FavorPrize>> UndeliveredChoicesAsync(string? playerId, CancellationToken ct = default) => _inner.UndeliveredChoicesAsync(playerId, ct);
            public Task MarkChoiceDeliveredAsync(int reign, string playerId, CancellationToken ct = default) => _inner.MarkChoiceDeliveredAsync(reign, playerId, ct);
            public Task<ClosedChoice?> ClosedChoiceAsync(int reign, string playerId, CancellationToken ct = default) => _inner.ClosedChoiceAsync(reign, playerId, ct);
        }

        private sealed class Harness
        {
            public Clock Time { get; } = new Clock();
            public InMemoryAccountStore Accounts { get; }
            public InMemoryFavorStore Store { get; } = new InMemoryFavorStore();
            public FailingStore Failing { get; }
            public InMemoryEntitlementLedger Ledger { get; } = new InMemoryEntitlementLedger();
            public FakeBacks Backs { get; } = new FakeBacks();
            private int _subjects;

            public Harness()
            {
                Accounts = new InMemoryAccountStore(Time);
                Failing = new FailingStore(Store);
            }

            public KingPayoutSweep Sweep(IReadOnlyList<int>? placeBacks = null, IReadOnlyList<int>? pool = null, FavorPrizeSettings? prizes = null)
                => new KingPayoutSweep(Failing, Accounts, new EconomyService(Ledger, Time), Backs,
                    prizes ?? Prizes, ReignOne, Time, placeBacks ?? PlaceBacks, pool ?? Pool);

            public async Task<string> PlayerAsync()
                => (await Accounts.ResolveOrCreateAsync("supabase", "subject-" + _subjects++)).PlayerId;

            /// <summary>Banks a board for <paramref name="reign"/> in the order given, best first,
            /// whichever way that reign's commission sorts.</summary>
            public async Task BoardAsync(int reign, params string[] bestFirst)
            {
                bool lower = CommissionCatalog.LowerIsBetter(KingSchedule.For(reign).Commission);
                for (int i = 0; i < bestFirst.Length; i++)
                {
                    int score = lower ? i + 1 : 100 - i;
                    await Store.RecordAsync(new FavorScore(reign, bestFirst[i], "name-" + bestFirst[i], score,
                        DateTimeOffset.FromUnixTimeMilliseconds(KingCalendar.StartMs(reign, ReignOne) + Hour)), lower);
                }
            }

            /// <summary>Sets the clock <paramref name="hours"/> after <paramref name="reign"/> closed.</summary>
            public void AfterClose(int reign, double hours)
                => Time.NowMs = KingCalendar.StartMs(reign + 1, ReignOne) + (long)(hours * Hour);

            public Task<long> GoldAsync(string playerId) => Ledger.BalanceAsync(playerId, EconomyItems.Gold);

            public async Task<IReadOnlyList<string>> FavorGoldKeysAsync(string playerId)
                => (await Ledger.HistoryAsync(playerId)).Where(g => g.Item == EconomyItems.Gold)
                    .Select(g => g.ExternalTxnId).ToList();

            public async Task<bool> PaidAsync(int reign)
                => !(await Store.UnpaidReignsAsync(reign + 1)).Any(r => r.Reign == reign);

            public async Task<string?> FavoriteAsync(int reign)
                => (await Store.RollAsync(100)).Single(r => r.Reign == reign).FavoriteName;
        }

        // ── Gold ──────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Pays_gold_by_place()
        {
            var h = new Harness();
            var p = new List<string>();
            for (int i = 0; i < 6; i++) p.Add(await h.PlayerAsync());
            await h.BoardAsync(1, p.ToArray());
            h.AfterClose(1, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            long[] gold = new long[6];
            for (int i = 0; i < 6; i++) gold[i] = await h.GoldAsync(p[i]);
            Assert.That(gold, Is.EqualTo(new long[] { 500, 300, 200, 100, 50, 0 }), "sixth is past the paid places");
            Assert.That(await h.PaidAsync(1), Is.True);
            Assert.That(await h.FavoriteAsync(1), Is.EqualTo("name-" + p[0]));
            Assert.That(await h.Store.PrizeAsync(1, p[3]), Is.EqualTo(new FavorPrize(1, p[3], 4, 100, null, false)));
            Assert.That(await h.Store.PrizeAsync(1, p[5]), Is.Null);
        }

        [Test]
        public async Task Fewer_entrants_than_places_pays_those_there_are()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync();
            await h.BoardAsync(1, a, b);
            h.AfterClose(1, 3);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.EqualTo(500));
            Assert.That(await h.GoldAsync(b), Is.EqualTo(300));
            Assert.That(await h.PaidAsync(1), Is.True);
        }

        [Test]
        public async Task A_place_without_gold_still_earns_its_back()
        {
            // Fewer gold places than back places: the top three earn backs whatever the gold says.
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync(), c = await h.PlayerAsync();
            await h.BoardAsync(1, a, b, c);
            h.AfterClose(1, 2);

            await h.Sweep(prizes: new FavorPrizeSettings(new long[] { 500, 0 }, 150, 50)).SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(b), Is.Zero);
            Assert.That(await h.GoldAsync(c), Is.Zero);
            Assert.That(h.Backs.Owned, Is.EquivalentTo(new[] { (a, 101), (b, 102), (c, 103) }));
        }

        // ── Backs ─────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task First_time_at_a_place_grants_that_places_back()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync(), c = await h.PlayerAsync(), d = await h.PlayerAsync();
            await h.BoardAsync(1, a, b, c, d);
            h.AfterClose(1, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(h.Backs.Owned, Is.EquivalentTo(new[] { (a, 101), (b, 102), (c, 103) }), "fourth earns no back");
            Assert.That(await h.Store.PrizeAsync(1, a), Is.EqualTo(new FavorPrize(1, a, 1, 500, 101, false)));
            Assert.That(await h.Store.UnsettledBacksAsync(3), Is.Empty);
        }

        [Test]
        public async Task Second_time_at_the_same_place_opens_a_choice()
        {
            var h = new Harness();
            string a = await h.PlayerAsync();
            await h.BoardAsync(1, a);
            await h.BoardAsync(2, a);
            h.Backs.Owned.Add((a, 201));   // one pool back still to collect
            h.AfterClose(2, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.Store.PrizeAsync(2, a), Is.EqualTo(new FavorPrize(2, a, 1, 500, null, true)));
            Assert.That(h.Backs.Applied, Has.Count.EqualTo(1), "the first place back, once");
            Assert.That(await h.GoldAsync(a), Is.EqualTo(1000), "no pool gold beside a choice");
        }

        [Test]
        public async Task A_new_place_earns_its_own_back_even_after_another()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), x = await h.PlayerAsync(), y = await h.PlayerAsync();
            await h.BoardAsync(1, x, y, a);
            await h.BoardAsync(2, a);
            h.AfterClose(2, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(h.Backs.Owned.Where(o => o.Player == a).Select(o => o.Back), Is.EquivalentTo(new[] { 103, 101 }));
            Assert.That((await h.Store.PrizeAsync(2, a))!.ChoiceOpen, Is.False);
        }

        [Test]
        public async Task Owning_the_whole_pool_pays_PoolEmptyGold()
        {
            var h = new Harness();
            string a = await h.PlayerAsync();
            await h.BoardAsync(1, a);
            await h.BoardAsync(2, a);
            h.Backs.Owned.Add((a, 201));
            h.Backs.Owned.Add((a, 202));
            h.AfterClose(2, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.EqualTo(500 + 500 + 150));
            Assert.That(await h.FavorGoldKeysAsync(a), Has.One.Contains("king-pool:2"));
            Assert.That(await h.Store.PrizeAsync(2, a), Is.EqualTo(new FavorPrize(2, a, 1, 500, null, false)));
        }

        [Test]
        public async Task Backs_wait_while_no_place_backs_are_configured()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync(), c = await h.PlayerAsync();
            await h.BoardAsync(1, a, b, c);
            h.AfterClose(1, 2);

            await h.Sweep(Array.Empty<int>(), Array.Empty<int>()).SweepAsync(CancellationToken.None);

            Assert.That(await h.PaidAsync(1), Is.True, "gold closes the reign without the art");
            Assert.That(await h.GoldAsync(a), Is.EqualTo(500));
            Assert.That(h.Backs.Calls, Is.Zero);
            Assert.That((await h.Store.UnsettledBacksAsync(3)).Select(p => p.PlayerId), Is.EqualTo(new[] { a, b, c }));
        }

        [Test]
        public async Task A_later_sweep_with_place_backs_settles_reign_ones_rows()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync(), c = await h.PlayerAsync();
            await h.BoardAsync(1, a, b, c);
            h.AfterClose(1, 2);
            await h.Sweep(Array.Empty<int>(), Array.Empty<int>()).SweepAsync(CancellationToken.None);

            h.AfterClose(1, 24 * 20);   // the art lands weeks later
            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(h.Backs.Owned, Is.EquivalentTo(new[] { (a, 101), (b, 102), (c, 103) }));
            Assert.That(await h.Store.UnsettledBacksAsync(3), Is.Empty);
            Assert.That(await h.GoldAsync(a), Is.EqualTo(500), "the late pass pays no gold twice");
        }

        [Test]
        public async Task Two_waiting_rows_at_one_place_give_its_back_to_the_earlier_reign()
        {
            var h = new Harness();
            string a = await h.PlayerAsync();
            await h.BoardAsync(1, a);
            await h.BoardAsync(2, a);
            h.AfterClose(2, 2);
            await h.Sweep(Array.Empty<int>(), Array.Empty<int>()).SweepAsync(CancellationToken.None);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.Store.PrizeAsync(1, a), Is.EqualTo(new FavorPrize(1, a, 1, 500, 101, false)));
            Assert.That(await h.Store.PrizeAsync(2, a), Is.EqualTo(new FavorPrize(2, a, 1, 500, null, true)));
            Assert.That(h.Backs.Applied, Has.Count.EqualTo(1));
        }

        [Test]
        public void Every_prize_back_is_in_the_catalog()
        {
            // Vacuous until the art lands; then a typo'd id would fail every back pass for ever.
            Assert.That(CardBackDefinitions.PlaceBackIds.Count, Is.EqualTo(0).Or.EqualTo(3));
            foreach (int id in CardBackDefinitions.PlaceBackIds.Concat(CardBackDefinitions.PoolIds))
                Assert.That(CardBackDefinitions.TryGet(id, out _), Is.True, "back " + id);
            Assert.That(CardBackDefinitions.PoolIds.Intersect(CardBackDefinitions.PlaceBackIds), Is.Empty,
                "a place back in the pool could be picked instead of earned");
        }

        [Test]
        public async Task The_ledger_seam_grants_a_back_once_per_reign()
        {
            var ledger = new InMemoryEntitlementLedger();
            var accounts = new InMemoryAccountStore(new Clock());
            Account account = await accounts.ResolveOrCreateAsync("supabase", "subject-0");
            var backs = new LedgerPrizeBacks(ledger);

            await backs.GrantAsync(account, CardBackDefinitions.DefaultId, "king:1", CancellationToken.None);
            await backs.GrantAsync(account, CardBackDefinitions.DefaultId, "king:1", CancellationToken.None);

            string item = CardBackOwnership.Item(CardBackDefinitions.DefaultId);
            Assert.That(await ledger.BalanceAsync(account.PlayerId, item), Is.EqualTo(1));
            Assert.That(await backs.OwnsAsync(account, CardBackDefinitions.DefaultId, CancellationToken.None), Is.True);
            Assert.That((await ledger.HistoryAsync(account.PlayerId)).Single().ExternalTxnId,
                Is.EqualTo(item + ":" + account.PlayerId + ":king:1"), "the key reverse-favor-prize.sql matches");
        }

        // ── When ──────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Waits_two_hours_after_close()
        {
            var h = new Harness();
            string a = await h.PlayerAsync();
            await h.BoardAsync(1, a);
            h.Time.NowMs = KingCalendar.StartMs(2, ReignOne) + 2 * Hour - 1;

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.Zero);
            Assert.That(await h.PaidAsync(1), Is.False);
        }

        [Test]
        public async Task Runs_after_two_hours()
        {
            var h = new Harness();
            string a = await h.PlayerAsync();
            await h.BoardAsync(1, a);
            h.AfterClose(1, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.EqualTo(500));
        }

        [Test]
        public async Task Two_missed_tuesdays_both_pay()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync();
            await h.BoardAsync(1, a, b);
            await h.BoardAsync(2, b, a);
            await h.BoardAsync(3, a);
            h.AfterClose(2, 5);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.EqualTo(500 + 300));
            Assert.That(await h.GoldAsync(b), Is.EqualTo(300 + 500));
            Assert.That(await h.PaidAsync(1), Is.True);
            Assert.That(await h.PaidAsync(2), Is.True);
            Assert.That(await h.PaidAsync(3), Is.False, "reign 3 is the current one");
        }

        [Test]
        public async Task The_current_reign_never_pays()
        {
            var h = new Harness();
            string a = await h.PlayerAsync();
            await h.BoardAsync(2, a);
            h.AfterClose(1, 24 * 6);   // late in reign 2

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.Zero);
            Assert.That(await h.PaidAsync(2), Is.False);
            Assert.That(await h.PaidAsync(1), Is.True, "reign 1 closed empty");
        }

        [Test]
        public async Task Nothing_pays_before_reign_one_closes()
        {
            var h = new Harness();
            string a = await h.PlayerAsync();
            await h.BoardAsync(1, a);
            h.Time.NowMs = ReignOne - Hour;

            await h.Sweep().SweepAsync(CancellationToken.None);
            h.Time.NowMs = ReignOne + 3 * Hour;
            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.Zero);
            Assert.That(await h.PaidAsync(1), Is.False);
        }

        // ── Killed halfway ────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_sweep_killed_mid_reign_pays_nobody_twice()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync(), c = await h.PlayerAsync();
            await h.BoardAsync(1, a, b, c);
            h.AfterClose(1, 2);
            h.Failing.FailOnRecordPrize = 2;   // after the second player's gold is granted

            Assert.ThrowsAsync<InvalidOperationException>(() => h.Sweep().SweepAsync(CancellationToken.None));
            Assert.That(await h.PaidAsync(1), Is.False, "paid_at is written last");
            Assert.That(await h.GoldAsync(b), Is.EqualTo(300), "b was paid before the kill");
            Assert.That(h.Backs.Calls, Is.Zero, "no back before the reign closes");

            await h.Sweep().SweepAsync(CancellationToken.None);

            foreach ((string player, long gold) in new[] { (a, 500L), (b, 300L), (c, 200L) })
            {
                Assert.That(await h.FavorGoldKeysAsync(player), Has.Count.EqualTo(1), "one grant per key");
                Assert.That(await h.GoldAsync(player), Is.EqualTo(gold));
            }
            Assert.That(h.Backs.Applied, Has.Count.EqualTo(3));
            Assert.That(await h.PaidAsync(1), Is.True);
        }

        [Test]
        public async Task A_back_pass_killed_midway_grants_nothing_twice()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync(), c = await h.PlayerAsync();
            await h.BoardAsync(1, a, b, c);
            await h.BoardAsync(2, a);
            h.Backs.Owned.Add((a, 201));
            h.Backs.Owned.Add((a, 202));   // so reign 2 pays pool gold, the other keyed grant here
            h.AfterClose(2, 2);
            h.Failing.FailOnSettleBack = 2;   // after b's back is granted, before its row settles

            Assert.ThrowsAsync<InvalidOperationException>(() => h.Sweep().SweepAsync(CancellationToken.None));
            Assert.That(await h.PaidAsync(2), Is.True, "the gold pass finished first");

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(h.Backs.Applied, Is.EquivalentTo(new[] { "101:" + a + ":king:1", "102:" + b + ":king:1", "103:" + c + ":king:1" }));
            Assert.That(h.Backs.Calls, Is.EqualTo(4), "b's grant was retried, and was a duplicate");
            Assert.That(await h.FavorGoldKeysAsync(a), Has.Count.EqualTo(3), "two places and one pool");
            Assert.That(await h.GoldAsync(a), Is.EqualTo(500 + 500 + 150));
            Assert.That(await h.Store.UnsettledBacksAsync(3), Is.Empty);
        }

        // ── Nobody, the deleted and the struck ────────────────────────────────────────────

        [Test]
        public async Task An_empty_reign_closes_with_no_favorite()
        {
            var h = new Harness();
            h.AfterClose(1, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.PaidAsync(1), Is.True);
            Assert.That(await h.FavoriteAsync(1), Is.Null);
        }

        [Test]
        public async Task A_deleted_player_is_skipped_and_nobody_is_promoted()
        {
            var h = new Harness();
            string b = await h.PlayerAsync();
            await h.BoardAsync(1, "deleted-player", b);   // a score with no account behind it
            h.AfterClose(1, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(b), Is.EqualTo(300), "second place's gold, not first's");
            Assert.That(h.Backs.Owned, Is.EquivalentTo(new[] { (b, 102) }));
            Assert.That(await h.Store.PrizeAsync(1, "deleted-player"), Is.Null);
            Assert.That(await h.FavoriteAsync(1), Is.Null, "nobody is named in the deleted player's place");
        }

        [Test]
        public async Task A_favorite_struck_before_the_sweep_is_skipped_and_nobody_is_promoted()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync();
            await h.BoardAsync(1, a, b);
            h.Store.SeedReign(1, null, null, struck: true);   // strike-favorite.sql, run before the sweep
            h.AfterClose(1, 2);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(await h.GoldAsync(a), Is.Zero);
            Assert.That(await h.Store.PrizeAsync(1, a), Is.Null);
            Assert.That(await h.GoldAsync(b), Is.EqualTo(300));
            Assert.That(h.Backs.Owned, Is.EquivalentTo(new[] { (b, 102) }));
            Assert.That(await h.PaidAsync(1), Is.True);
            Assert.That(await h.FavoriteAsync(1), Is.Null, "the strike survives the close");
        }

        [Test]
        public async Task A_favorite_struck_before_the_backs_land_gets_no_back()
        {
            var h = new Harness();
            string a = await h.PlayerAsync(), b = await h.PlayerAsync();
            await h.BoardAsync(1, a, b);
            h.AfterClose(1, 2);
            await h.Sweep(Array.Empty<int>(), Array.Empty<int>()).SweepAsync(CancellationToken.None);
            h.Store.Strike(1);

            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(h.Backs.Owned, Is.EquivalentTo(new[] { (b, 102) }));
        }
    }
}
