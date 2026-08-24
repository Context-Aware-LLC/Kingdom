using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Bots;
using Emberfall.Net.Server.Data;
using Emberfall.Net.Server.Decks;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Net.Server.Tests.Bots
{
    [TestFixture]
    [Category("Unit")]
    public sealed class BotPoolTests
    {
        private static ResolvedBotAccount MakeAccount(string username)
            => new ResolvedBotAccount(
                username,
                AccountId.FromAuthenticatedAccount("player-" + username),
                username,
                new SeatDeck(CardClass.EMBERWARD, FixtureCard.Ids("ember_recruit")));

        [Test]
        public void TryAcquire_Returns_Null_When_No_Bot_Is_Ready()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db);
            Assert.That(pool.TryAcquire(), Is.Null);
        }

        [Test]
        public void TryAcquire_Returns_A_Ready_Bot_And_Marks_It_Busy()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db);
            pool.MarkReady(MakeAccount("ember_knight"));

            var handle = pool.TryAcquire();
            Assert.That(handle, Is.Not.Null);
            Assert.That(handle!.DisplayName, Is.EqualTo("ember_knight"));

            Assert.That(pool.TryAcquire(), Is.Null, "the only ready bot is now busy");
        }

        [Test]
        public void Release_Returns_A_Bot_To_The_Pool()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db);
            pool.MarkReady(MakeAccount("ember_knight"));

            var handle = pool.TryAcquire()!;
            handle.Release();

            var reacquired = pool.TryAcquire();
            Assert.That(reacquired, Is.Not.Null);
        }

        [Test]
        public void Two_Ready_Bots_Can_Be_Acquired_Concurrently()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db);
            pool.MarkReady(MakeAccount("ember_knight"));
            pool.MarkReady(MakeAccount("tide_warden"));

            var first = pool.TryAcquire();
            var second = pool.TryAcquire();
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(first!.DisplayName, Is.Not.EqualTo(second!.DisplayName));
            Assert.That(pool.TryAcquire(), Is.Null);
        }
    }
}
