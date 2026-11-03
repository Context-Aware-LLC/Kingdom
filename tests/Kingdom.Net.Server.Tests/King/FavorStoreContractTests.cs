using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Tests.Accounts;
using Npgsql;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary>The Favor-store contract, run against both implementations.</summary>
    [Category("Contract")]
    public abstract class FavorStoreContractTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

        protected IFavorStore Store { get; private set; } = null!;

        protected abstract Task<IFavorStore> CreateAsync();

        /// <summary>Writes a reign's row, unpaid, as an operator script would: nothing on
        /// <see cref="IFavorStore"/> strikes a reign or names a Favorite without paying it.</summary>
        protected abstract Task SeedReignAsync(int reign, string? favoriteId, string? favoriteName, bool struck);

        [SetUp]
        public async Task CreateStore() => Store = await CreateAsync();

        private static FavorScore Score(int reign, string player, int score, DateTimeOffset at, string? name = null)
            => new FavorScore(reign, player, name ?? ("name-" + player), score, at);

        [Test]
        public async Task Keeps_the_better_score_higher()
        {
            Assert.That(await Store.RecordAsync(Score(1, "p", 7, T0), false, default), Is.Null);
            Assert.That(await Store.RecordAsync(Score(1, "p", 5, T0.AddHours(1)), false, default), Is.EqualTo(7));

            Assert.That(await Store.BestAsync(1, "p", default), Is.EqualTo(7));
            // reached_at stays that of the 7: q ties it at 30 minutes in, and p, who got there first, leads.
            await Store.RecordAsync(Score(1, "q", 7, T0.AddMinutes(30)), false, default);
            IReadOnlyList<FavorRow> top = await Store.TopAsync(1, false, 10, default);
            Assert.That(top.Select(r => r.DisplayName), Is.EqualTo(new[] { "name-p", "name-q" }));
        }

        [Test]
        public async Task Keeps_the_better_score_lower()
        {
            await Store.RecordAsync(Score(1, "p", 3, T0), true, default);
            Assert.That(await Store.RecordAsync(Score(1, "p", 5, T0.AddHours(1)), true, default), Is.EqualTo(3));
            Assert.That(await Store.BestAsync(1, "p", default), Is.EqualTo(3));

            await Store.RecordAsync(Score(1, "n", -2, T0), true, default);
            Assert.That(await Store.RecordAsync(Score(1, "n", 0, T0.AddHours(1)), true, default), Is.EqualTo(-2));
            Assert.That(await Store.BestAsync(1, "n", default), Is.EqualTo(-2));
        }

        [Test]
        public async Task A_better_score_replaces_the_row_and_returns_the_prior_best()
        {
            await Store.RecordAsync(Score(1, "p", 4, T0, "Old"), false, default);
            Assert.That(await Store.RecordAsync(Score(1, "p", 9, T0.AddHours(1), "New"), false, default), Is.EqualTo(4));

            FavorRow only = (await Store.TopAsync(1, false, 10, default)).Single();
            Assert.That(only, Is.EqualTo(new FavorRow(1, "New", 9, "p")));
        }

        [Test]
        public async Task Ties_go_to_whoever_got_there_first()
        {
            await Store.RecordAsync(Score(1, "late", 6, T0.AddHours(2)), false, default);
            await Store.RecordAsync(Score(1, "early", 6, T0), false, default);

            IReadOnlyList<FavorRow> top = await Store.TopAsync(1, false, 10, default);

            Assert.That(top.Select(r => (r.Place, r.DisplayName)),
                Is.EqualTo(new[] { (1, "name-early"), (2, "name-late") }));
            Assert.That(await Store.PlaceAsync(1, "early", false, default), Is.EqualTo(1));
            Assert.That(await Store.PlaceAsync(1, "late", false, default), Is.EqualTo(2));
            Assert.That(await Store.PlaceAsync(1, "nobody", false, default), Is.Null);
        }

        [Test]
        public async Task Equal_score_does_not_refresh_reached_at()
        {
            await Store.RecordAsync(Score(1, "a", 6, T0), false, default);
            await Store.RecordAsync(Score(1, "b", 6, T0.AddHours(1)), false, default);
            // a hits 6 again, later than b: a must still be ahead.
            Assert.That(await Store.RecordAsync(Score(1, "a", 6, T0.AddHours(5)), false, default), Is.EqualTo(6));

            Assert.That(await Store.PlaceAsync(1, "a", false, default), Is.EqualTo(1));
        }

        [Test]
        public async Task Lower_is_better_orders_ascending()
        {
            await Store.RecordAsync(Score(1, "slow", 9, T0), true, default);
            await Store.RecordAsync(Score(1, "fast", 2, T0.AddHours(1)), true, default);

            IReadOnlyList<FavorRow> top = await Store.TopAsync(1, true, 10, default);

            Assert.That(top.Select(r => r.Score), Is.EqualTo(new[] { 2, 9 }));
            Assert.That(await Store.PlaceAsync(1, "slow", true, default), Is.EqualTo(2));
        }

        [Test]
        public async Task Top_honours_the_limit()
        {
            for (int i = 0; i < 5; i++) await Store.RecordAsync(Score(1, "p" + i, i, T0), false, default);
            Assert.That((await Store.TopAsync(1, false, 3, default)).Select(r => r.Score), Is.EqualTo(new[] { 4, 3, 2 }));
        }

        [Test]
        public async Task Reigns_are_isolated()
        {
            await Store.RecordAsync(Score(2, "p", 8, T0), false, default);

            Assert.That(await Store.TopAsync(1, false, 10, default), Is.Empty);
            Assert.That(await Store.BestAsync(1, "p", default), Is.Null);
            Assert.That(await Store.PlaceAsync(1, "p", false, default), Is.Null);
            Assert.That(await Store.BestAsync(2, "p", default), Is.EqualTo(8));
        }

        [Test]
        public async Task Reigns_played_lists_each_reign_the_player_scored_in_once()
        {
            await Store.RecordAsync(Score(3, "p", 2, T0), false, default);
            await Store.RecordAsync(Score(1, "p", 5, T0), false, default);
            await Store.RecordAsync(Score(1, "p", 9, T0.AddHours(1)), false, default);
            await Store.RecordAsync(Score(2, "q", 4, T0), false, default);

            Assert.That(await Store.ReignsPlayedAsync("p", default), Is.EqualTo(new[] { 1, 3 }));
            Assert.That(await Store.ReignsPlayedAsync("nobody", default), Is.Empty);
        }

        [Test]
        public async Task Roll_is_newest_first_and_honours_the_limit()
        {
            await SeedReignAsync(1, "a", "Alice", false);
            await SeedReignAsync(2, "b", "Bob", false);
            await SeedReignAsync(3, "c", "Cy", false);

            IReadOnlyList<RollRow> roll = await Store.RollAsync(2, default);

            Assert.That(roll.Select(r => (r.Reign, r.FavoriteName)), Is.EqualTo(new[] { (3, "Cy"), (2, "Bob") }));
        }

        [Test]
        public async Task Struck_favorite_reads_as_none()
        {
            await SeedReignAsync(1, "a", "Alice", true);
            await SeedReignAsync(2, null, null, false);

            IReadOnlyList<RollRow> roll = await Store.RollAsync(10, default);

            Assert.That(roll.Select(r => (r.Reign, r.FavoriteName)), Is.EqualTo(new (int, string?)[] { (2, null), (1, null) }));
        }

        // ── The Tuesday sweep's half ──────────────────────────────────────────────────────

        [Test]
        public async Task Unpaid_reigns_are_every_earlier_reign_not_yet_paid()
        {
            await SeedReignAsync(3, null, null, true);
            await Store.ClosePaidAsync(2, "a", "Alice", default);

            Assert.That(await Store.UnpaidReignsAsync(5, default),
                Is.EqualTo(new[] { new UnpaidReign(1, false), new UnpaidReign(3, true), new UnpaidReign(4, false) }));
            Assert.That(await Store.UnpaidReignsAsync(1, default), Is.Empty);
            Assert.That(await Store.UnpaidReignsAsync(0, default), Is.Empty);
        }

        [Test]
        public async Task Closing_is_once_and_keeps_a_strike()
        {
            await SeedReignAsync(1, null, null, true);
            await Store.ClosePaidAsync(1, "a", "Alice", default);
            await Store.ClosePaidAsync(2, "a", "Alice", default);
            await Store.ClosePaidAsync(2, "b", "Bob", default);

            IReadOnlyList<RollRow> roll = await Store.RollAsync(10, default);

            Assert.That(roll.Select(r => (r.Reign, r.FavoriteName)), Is.EqualTo(new (int, string?)[] { (2, "Alice"), (1, null) }));
            Assert.That(await Store.UnpaidReignsAsync(3, default), Is.Empty);
        }

        [Test]
        public async Task A_prize_is_written_once()
        {
            await Store.RecordPrizeAsync(new FavorPrize(1, "a", 1, 500, null, false), default);
            await Store.RecordPrizeAsync(new FavorPrize(1, "a", 2, 300, null, false), default);

            Assert.That(await Store.PrizeAsync(1, "a", default), Is.EqualTo(new FavorPrize(1, "a", 1, 500, null, false)));
            Assert.That(await Store.PrizeAsync(1, "b", default), Is.Null);
        }

        [Test]
        public async Task Holding_a_place_counts_only_earlier_reigns()
        {
            await Store.RecordPrizeAsync(new FavorPrize(2, "a", 1, 500, null, false), default);

            Assert.That(await Store.HasHeldPlaceAsync("a", 1, 2, default), Is.False);
            Assert.That(await Store.HasHeldPlaceAsync("a", 1, 3, default), Is.True);
            Assert.That(await Store.HasHeldPlaceAsync("a", 2, 3, default), Is.False);
            Assert.That(await Store.HasHeldPlaceAsync("b", 1, 3, default), Is.False);
        }

        [Test]
        public async Task Unsettled_backs_are_a_paid_reigns_top_places_until_settled_once()
        {
            foreach ((string player, int place) in new[] { ("a", 1), ("b", 2), ("c", 3), ("d", 4) })
                await Store.RecordPrizeAsync(new FavorPrize(1, player, place, 100, null, false), default);
            await Store.RecordPrizeAsync(new FavorPrize(2, "e", 1, 100, null, false), default);   // reign 2 unpaid
            await Store.ClosePaidAsync(1, "a", "Alice", default);

            Assert.That((await Store.UnsettledBacksAsync(3, default)).Select(p => p.PlayerId), Is.EqualTo(new[] { "a", "b", "c" }));

            await Store.SettleBackAsync(1, "b", 102, false, default);
            await Store.SettleBackAsync(1, "b", null, true, default);

            Assert.That((await Store.UnsettledBacksAsync(3, default)).Select(p => p.PlayerId), Is.EqualTo(new[] { "a", "c" }));
            Assert.That(await Store.PrizeAsync(1, "b", default), Is.EqualTo(new FavorPrize(1, "b", 2, 100, 102, false)));
        }

        [Test]
        public async Task A_struck_reigns_first_waits_for_no_back()
        {
            await SeedReignAsync(3, null, null, true);
            await Store.RecordPrizeAsync(new FavorPrize(3, "f", 1, 100, null, false), default);
            await Store.RecordPrizeAsync(new FavorPrize(3, "g", 2, 100, null, false), default);
            await Store.ClosePaidAsync(3, "f", "Fay", default);

            Assert.That((await Store.UnsettledBacksAsync(3, default)).Select(p => p.PlayerId), Is.EqualTo(new[] { "g" }));
        }
    }

    [TestFixture]
    [Category("Contract")]
    public sealed class InMemoryFavorStoreContractTests : FavorStoreContractTests
    {
        private InMemoryFavorStore _store = null!;

        protected override Task<IFavorStore> CreateAsync()
        {
            _store = new InMemoryFavorStore();
            return Task.FromResult<IFavorStore>(_store);
        }

        protected override Task SeedReignAsync(int reign, string? favoriteId, string? favoriteName, bool struck)
        {
            _store.SeedReign(reign, favoriteId, favoriteName, struck);
            return Task.CompletedTask;
        }
    }

    /// <summary>The identical suite against Postgres, gated on <c>KINGDOM_LEDGER_DB</c>.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class PostgresFavorStoreContractTests : FavorStoreContractTests
    {
        private string _conn = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _conn = PostgresProbe.RequireOrIgnore();
            await new PostgresFavorStore(_conn).EnsureSchemaAsync();
        }

        protected override async Task<IFavorStore> CreateAsync()
        {
            await using (var conn = new NpgsqlConnection(_conn))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("truncate table favor_score, favor_prize, king_reign", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            return new PostgresFavorStore(_conn);
        }

        protected override async Task SeedReignAsync(int reign, string? favoriteId, string? favoriteName, bool struck)
        {
            await using var conn = new NpgsqlConnection(_conn);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "insert into king_reign (reign, favorite_id, favorite_name, struck) values ($1, $2, $3, $4)", conn);
            cmd.Parameters.AddWithValue(reign);
            cmd.Parameters.AddWithValue((object?)favoriteId ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)favoriteName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(struck);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
