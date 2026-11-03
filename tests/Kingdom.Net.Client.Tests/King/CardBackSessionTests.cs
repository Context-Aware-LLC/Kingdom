using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.King
{
    /// <summary>
    /// Each seat's card back reaches both clients on the match start (#421), so the King's prize
    /// backs are seen by the opponent too. Read by engine seat, not by "mine" and "theirs".
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class CardBackSessionTests : ClientTestBase
    {
        private static NetworkedMatchSession Client(LoopbackServerPeer peer, int seat)
            => new NetworkedMatchSession(seat == 0 ? peer.Transport0 : peer.Transport1,
                new ClientOptions { DeviceToken = "test-device-" + seat });

        [Test]
        public async Task Both_clients_read_each_seats_back()
        {
            using var peer = new LoopbackServerPeer(cardBacks: (42, 7));
            using NetworkedMatchSession c0 = Client(peer, 0);
            using NetworkedMatchSession c1 = Client(peer, 1);
            Assert.That(c0.CardBackOf(0), Is.EqualTo(CardBackDefinitions.DefaultId), "the default until the match starts");

            await StartPairAsync(peer, c0, c1);

            foreach (NetworkedMatchSession c in new[] { c0, c1 })
            {
                Assert.That(c.CardBackOf(0), Is.EqualTo(42), $"seat {c.Seat} reads seat 0's back");
                Assert.That(c.CardBackOf(1), Is.EqualTo(7), $"seat {c.Seat} reads seat 1's back");
            }
        }

        [Test]
        public async Task A_seat_outside_the_match_reads_the_default()
        {
            using var peer = new LoopbackServerPeer(cardBacks: (42, 7));
            using NetworkedMatchSession c0 = Client(peer, 0);
            using NetworkedMatchSession c1 = Client(peer, 1);
            await StartPairAsync(peer, c0, c1);

            Assert.That(c0.CardBackOf(2), Is.EqualTo(CardBackDefinitions.DefaultId));
            Assert.That(c0.CardBackOf(-1), Is.EqualTo(CardBackDefinitions.DefaultId));
        }
    }
}
