using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Tests.Support;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.King;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.King
{
    /// <summary>
    /// The client's surface for the commission card (#421), against the real server session: the
    /// latest progress off the snapshot path, and the final off the match end. The client never
    /// computes a score; it only reads the one the server sent its seat.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class CommissionSessionTests : ClientTestBase
    {
        private static NetworkedMatchSession Client(LoopbackServerPeer peer, int seat)
            => new NetworkedMatchSession(seat == 0 ? peer.Transport0 : peer.Transport1,
                new ClientOptions { DeviceToken = "test-device-" + seat });

        [Test]
        public async Task The_card_arrives_with_the_match_and_follows_each_snapshot()
        {
            using var peer = new LoopbackServerPeer(king: new KingSeating(2, CommissionIds.WasteNot, 9, 4, "A", "B"));
            using NetworkedMatchSession c0 = Client(peer, 0);
            using NetworkedMatchSession c1 = Client(peer, 1);
            await StartPairAsync(peer, c0, c1);

            Assert.That(c0.Commission, Is.EqualTo(new CommissionProgress(CommissionIds.WasteNot, null, true, 9)),
                "the match start's snapshot carries the first card");
            Assert.That(c1.Commission!.Best, Is.EqualTo(4), "each seat holds its own");

            var seen = new List<CommissionProgress>();
            c0.OnCommission += seen.Add;
            await ConfirmMulligansAsync(peer, c0, c1);

            Assert.That(seen, Is.Not.Empty, "an applied snapshot raises the card's update");
            Assert.That(seen[seen.Count - 1], Is.EqualTo(c0.Commission));

            int active = c0.Current.SeatOf(c0.Current.ActivePlayer);
            NetworkedMatchSession actor = active == 0 ? c0 : c1;
            Task<CommandResult> end = actor.SubmitAsync(new EndTurnIntent(actor.Player));
            await peer.RelayAsync();
            await end;

            Assert.That(actor.Commission!.Score, Is.GreaterThan(0), "the ender's Waste Not score, as the server counted it");
        }

        [Test]
        public async Task The_final_arrives_with_the_match_end()
        {
            using var peer = new LoopbackServerPeer(king: new KingSeating(2, CommissionIds.Technically, 40, 40, "A", "B"));
            using NetworkedMatchSession c0 = Client(peer, 0);
            using NetworkedMatchSession c1 = Client(peer, 1);
            await StartPairAsync(peer, c0, c1);
            await ConfirmMulligansAsync(peer, c0, c1);
            Assert.That(c0.EndCommission, Is.Null, "nothing until the server says");

            Task<CommandResult> concede = c1.SubmitAsync(new ConcedeIntent(c1.Player));
            await peer.RelayAsync();
            await concede;

            Assert.That(c0.EndCommission, Is.Not.Null);
            Assert.That(c0.EndCommission!.Id, Is.EqualTo(CommissionIds.Technically));
            Assert.That(c0.EndCommission.NewBest, Is.True);
            Assert.That(c1.EndCommission, Is.Null, "a lost win-only commission has no final");
        }

        [Test]
        public async Task A_friend_match_has_no_card()
        {
            using var peer = new LoopbackServerPeer();
            using NetworkedMatchSession c0 = Client(peer, 0);
            using NetworkedMatchSession c1 = Client(peer, 1);
            int raised = 0;
            c0.OnCommission += _ => raised++;
            await StartPairAsync(peer, c0, c1);
            await ConfirmMulligansAsync(peer, c0, c1);

            Assert.That(c0.Commission, Is.Null);
            Assert.That(raised, Is.Zero);
        }
    }
}
