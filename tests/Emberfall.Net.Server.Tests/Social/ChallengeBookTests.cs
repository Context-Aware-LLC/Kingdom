using System.Linq;
using Emberfall.Net.Server.Social;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Social
{
    /// <summary>
    /// The challenge state machine (#409). The property that matters most is the last one a
    /// player would think to test: a ticket is worthless to anyone but the account it was issued to.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class ChallengeBookTests
    {
        private const string A = "player-a", B = "player-b", C = "player-c";

        private FakeTime _time = null!;
        private ChallengeBook _book = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new FakeTime();
            _book = new ChallengeBook(_time, offerTtlMs: 60_000, ticketTtlMs: 30_000);
        }

        private Challenge Accepted()
        {
            Challenge offer = _book.Open(A, B)!;
            return _book.Accept(offer.Id, B)!;
        }

        [Test]
        public void A_Player_Has_One_Live_Challenge_In_Either_Role()
        {
            Assert.That(_book.Open(A, B), Is.Not.Null);

            Assert.That(_book.Open(A, C), Is.Null, "A is already challenging someone");
            Assert.That(_book.Open(C, B), Is.Null, "B is already being challenged");
            Assert.That(_book.Open(B, A), Is.Null);
            Assert.That(_book.Open(A, A), Is.Null);
        }

        [Test]
        public void Only_The_Target_Can_Accept_And_Only_Once()
        {
            Challenge offer = _book.Open(A, B)!;

            Assert.That(_book.Accept(offer.Id, A), Is.Null, "the sender cannot accept their own offer");
            Assert.That(_book.Accept(offer.Id, C), Is.Null);
            Assert.That(_book.Accept("no-such-id", B), Is.Null);

            Challenge accepted = _book.Accept(offer.Id, B)!;
            Assert.That(accepted.Tickets, Has.Length.EqualTo(2));
            Assert.That(accepted.Tickets![0], Is.Not.EqualTo(accepted.Tickets[1]));
            Assert.That(_book.Accept(offer.Id, B), Is.Null, "a second accept would mint a second pair of tickets");
        }

        [Test]
        public void A_Ticket_Seats_Only_The_Player_It_Was_Issued_To_And_Only_Once()
        {
            Challenge challenge = Accepted();
            string forA = challenge.Tickets![0], forB = challenge.Tickets[1];

            Assert.That(_book.Redeem(forA, C), Is.Null, "a stranger holding the ticket");
            Assert.That(_book.Redeem(forA, B), Is.Null, "the OTHER player holding the wrong ticket");
            Assert.That(_book.Redeem("no-such-ticket", A), Is.Null);

            ChallengeSeat seat = _book.Redeem(forA, A)!;
            Assert.That(seat.ChallengeId, Is.EqualTo(challenge.Id));
            Assert.That(seat.OpponentPlayerId, Is.EqualTo(B));
            Assert.That(_book.Redeem(forA, A), Is.Null, "single-use");

            Assert.That(_book.Redeem(forB, B)!.OpponentPlayerId, Is.EqualTo(A));
        }

        [Test]
        public void An_Unanswered_Offer_Expires_And_Frees_Both_Players()
        {
            Challenge offer = _book.Open(A, B)!;
            _time.Advance(60_000);

            Assert.That(_book.Accept(offer.Id, B), Is.Null);
            Assert.That(_book.Close(offer.Id, A), Is.Null,
                "an expired challenge is left for Sweep, which is what tells both players");
            Assert.That(_book.Sweep().Select(c => c.Id), Is.EqualTo(new[] { offer.Id }));
            Assert.That(_book.Sweep(), Is.Empty);
            Assert.That(_book.Open(A, C), Is.Not.Null);
        }

        [Test]
        public void Accepting_Restarts_The_Clock_And_An_Unused_Ticket_Dies_With_It()
        {
            Challenge offer = _book.Open(A, B)!;
            _time.Advance(59_000);
            Challenge accepted = _book.Accept(offer.Id, B)!;

            _time.Advance(29_000);
            Assert.That(_book.Sweep(), Is.Empty, "the ticket's wait is not the offer's");

            _time.Advance(1_000);
            Assert.That(_book.Redeem(accepted.Tickets![0], A), Is.Null);
            Assert.That(_book.Sweep().Count, Is.EqualTo(1));
        }

        [Test]
        public void Either_Party_Can_Close_And_A_Stranger_Cannot()
        {
            Challenge offer = _book.Open(A, B)!;

            Assert.That(_book.Close(offer.Id, C), Is.Null);
            Assert.That(_book.Close(offer.Id, B), Is.SameAs(offer));
            Assert.That(_book.Close(offer.Id, A), Is.Null, "already gone");
            Assert.That(_book.HasLive(A), Is.False);
        }

        [Test]
        public void A_Departing_Player_Drops_An_Offer_But_Not_An_Accepted_Challenge()
        {
            Challenge offer = _book.Open(A, B)!;
            Assert.That(_book.DropOfferFor(B), Is.SameAs(offer));
            Assert.That(_book.HasLive(A), Is.False);

            Challenge accepted = Accepted();
            Assert.That(_book.DropOfferFor(A), Is.Null,
                "both players are on their way to /ws; the social socket no longer carries it");
            Assert.That(_book.Redeem(accepted.Tickets![1], B), Is.Not.Null);
        }

        [Test]
        public void Finish_Ends_The_Challenge_And_Kills_Its_Tickets()
        {
            Challenge accepted = Accepted();

            _book.Finish(accepted.Id);

            Assert.That(_book.Redeem(accepted.Tickets![0], A), Is.Null);
            Assert.That(_book.Open(A, B), Is.Not.Null);
        }
    }
}
