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

        /// <summary>
        /// Successive acquires do not keep handing out the same bot.
        ///
        /// <para><b>The failure this guards is silent and looks like a working feature.</b>
        /// <c>TryAcquire</c> used to return the first idle entry it enumerated, and <c>_ready</c> is a
        /// Dictionary keyed by username that enumerates in insertion order in practice — so whichever
        /// bot <c>bot-roster.json</c> listed first won every acquire where it was free. Every other
        /// roster entry only ever appeared while that one was mid-match. Nothing failed, no test went
        /// red, and adding bots to the roster changed nothing a player could see.</para>
        ///
        /// <para>Sixteen draws from a four-bot pool: under the old first-idle rule this sees exactly
        /// one name, and under a uniform draw the chance of seeing one name is 4·(1/4)^16 ≈ 9e-10.
        /// The assertion is "more than one", not a distribution test — a chi-square on a CSPRNG would
        /// be testing the platform, and would flake.</para>
        /// </summary>
        [Test]
        public void Acquire_Does_Not_Always_Return_The_Same_Bot()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            var pool = new BotPool(db);
            foreach (string name in new[] { "ember_knight", "tide_warden", "grave_toll", "verdant_grove" })
                pool.MarkReady(MakeAccount(name));

            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            for (int i = 0; i < 16; i++)
            {
                var handle = pool.TryAcquire();
                Assert.That(handle, Is.Not.Null, "a released pool always has an idle bot");
                seen.Add(handle!.DisplayName);
                handle.Release();   // back to a full pool, so every draw sees all four
            }

            Assert.That(seen, Has.Count.GreaterThan(1),
                "16 acquires from a 4-bot pool returned one name — the draw is not random, so a "
                + "player meets the same opponent every match and the roster's size is decorative");
        }
    }
}
