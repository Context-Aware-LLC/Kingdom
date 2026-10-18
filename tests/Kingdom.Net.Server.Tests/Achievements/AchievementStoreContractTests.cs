using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.Tests.Accounts;
using Npgsql;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Achievements
{
    /// <summary>The achievement-store contract, run against both implementations.</summary>
    [Category("Contract")]
    public abstract class AchievementStoreContractTests
    {
        private static readonly DateTimeOffset First = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

        protected IAchievementStore Store { get; private set; } = null!;

        protected abstract Task<IAchievementStore> CreateAsync();

        [SetUp]
        public async Task CreateStore() => Store = await CreateAsync();

        [Test]
        public async Task Earning_Returns_Only_What_Was_New()
        {
            IReadOnlyList<string> first = await Store.EarnAsync("p", new[] { "a", "b" }, "m1", First);
            IReadOnlyList<string> second = await Store.EarnAsync("p", new[] { "b", "c" }, "m2", First.AddHours(1));

            Assert.That(first, Is.EquivalentTo(new[] { "a", "b" }));
            Assert.That(second, Is.EquivalentTo(new[] { "c" }));
        }

        [Test]
        public async Task The_First_Earning_Is_The_One_Kept()
        {
            await Store.EarnAsync("p", new[] { "a" }, "m1", First);
            await Store.EarnAsync("p", new[] { "a" }, "m2", First.AddDays(3));

            EarnedAchievement kept = (await Store.ReadAsync("p")).Single();
            Assert.That(kept, Is.EqualTo(new EarnedAchievement("a", "m1", First)));
        }

        [Test]
        public async Task A_Read_Is_Per_Player_And_Oldest_First()
        {
            await Store.EarnAsync("p", new[] { "late" }, "m2", First.AddHours(2));
            await Store.EarnAsync("p", new[] { "early" }, "m1", First);
            await Store.EarnAsync("someone-else", new[] { "early" }, "m3", First);

            IReadOnlyList<EarnedAchievement> mine = await Store.ReadAsync("p");

            Assert.That(mine.Select(a => a.AchievementId), Is.EqualTo(new[] { "early", "late" }));
            Assert.That(await Store.ReadAsync("nobody"), Is.Empty);
        }

        [Test]
        public async Task Earning_Nothing_Writes_Nothing()
        {
            Assert.That(await Store.EarnAsync("p", Array.Empty<string>(), "m1", First), Is.Empty);
            Assert.That(await Store.ReadAsync("p"), Is.Empty);
        }
    }

    [TestFixture]
    [Category("Contract")]
    public sealed class InMemoryAchievementStoreContractTests : AchievementStoreContractTests
    {
        protected override Task<IAchievementStore> CreateAsync()
            => Task.FromResult<IAchievementStore>(new InMemoryAchievementStore());
    }

    /// <summary>The identical suite against Postgres. Gated on <c>KINGDOM_LEDGER_DB</c> (see
    /// <see cref="PostgresProbe"/>); empties the table before each test.</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class PostgresAchievementStoreContractTests : AchievementStoreContractTests
    {
        private string _conn = null!;

        [OneTimeSetUp]
        public async Task OneTimeSetUp()
        {
            _conn = PostgresProbe.RequireOrIgnore();
            await new PostgresAchievementStore(_conn).EnsureSchemaAsync();
        }

        protected override async Task<IAchievementStore> CreateAsync()
        {
            await using (var conn = new NpgsqlConnection(_conn))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("truncate table achievement_earned", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            return new PostgresAchievementStore(_conn);
        }
    }
}
