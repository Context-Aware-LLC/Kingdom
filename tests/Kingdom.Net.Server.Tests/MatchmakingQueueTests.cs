using System;
using Kingdom.Net.Server.Matchmaking;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    [TestFixture]
    [Category("Contract")]
    public sealed class MatchmakingQueueTests
    {
        private FakeTime _time = null!;
        private BandWideningMatchmakingQueue _queue = null!;

        [SetUp]
        public void SetUp()
        {
            _time = new FakeTime();
            _queue = new BandWideningMatchmakingQueue(_time, new MatchmakingQueueOptions
            {
                InitialRatingBand = 50,
                RatingBandStep = 100,
                BandWideningIntervalMs = 1_000,
            });
        }

        [Test]
        public void Equal_Ratings_Pair_Immediately_In_FIFO_Order()
        {
            _queue.Enqueue(Ticket("first", 1_000));
            _queue.Enqueue(Ticket("second", 1_000));

            var pair = _queue.TryDequeueMatch();

            Assert.That(pair, Is.Not.Null);
            Assert.That(pair!.First.Id, Is.EqualTo("first"));
            Assert.That(pair.Second.Id, Is.EqualTo("second"));
            Assert.That(_queue.Count, Is.Zero);
        }

        [Test]
        public void Oldest_Ticket_Gets_Its_Earliest_Compatible_Partner()
        {
            _queue.Enqueue(Ticket("low-old", 1_000));
            _queue.Enqueue(Ticket("high-old", 2_000));
            _queue.Enqueue(Ticket("high-new", 1_975));
            _queue.Enqueue(Ticket("low-new", 1_025));

            var low = _queue.TryDequeueMatch();
            var high = _queue.TryDequeueMatch();

            Assert.That((low!.First.Id, low.Second.Id), Is.EqualTo(("low-old", "low-new")));
            Assert.That((high!.First.Id, high.Second.Id), Is.EqualTo(("high-old", "high-new")));
        }

        [Test]
        public void Later_Compatible_Players_Do_Not_Bypass_An_Unmatched_FIFO_Head()
        {
            _queue.Enqueue(Ticket("head", 1_000));
            _queue.Enqueue(Ticket("later-a", 2_000));
            _queue.Enqueue(Ticket("later-b", 2_025));

            Assert.That(_queue.TryDequeueMatch(), Is.Null);
            Assert.That(_queue.Count, Is.EqualTo(3));
        }

        [Test]
        public void Search_Band_Widens_Only_On_Whole_Intervals()
        {
            _queue.Enqueue(Ticket("a", 1_000));
            _queue.Enqueue(Ticket("b", 1_250));

            _time.Advance(1_999);
            Assert.That(_queue.TryDequeueMatch(), Is.Null, "band is 150 before the second interval");

            _time.Advance(1);
            Assert.That(_queue.TryDequeueMatch(), Is.Not.Null, "band reaches 250 at the boundary");
        }

        [Test]
        public void Older_Tickets_Can_Accept_Fresh_Arrivals_Through_Their_Wider_Band()
        {
            _queue.Enqueue(Ticket("patient", 1_000));
            _time.Advance(2_000);
            _queue.Enqueue(Ticket("fresh", 1_250));

            var pair = _queue.TryDequeueMatch();

            Assert.That((pair!.First.Id, pair.Second.Id), Is.EqualTo(("patient", "fresh")));
        }

        [Test]
        public void Cancelled_Tickets_Are_Removed_And_Cannot_Match()
        {
            _queue.Enqueue(Ticket("gone", 1_000));

            Assert.That(_queue.Remove("gone"), Is.True);
            Assert.That(_queue.Remove("gone"), Is.False);
            _queue.Enqueue(Ticket("next", 1_000));
            Assert.That(_queue.TryDequeueMatch(), Is.Null);
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void Duplicate_Live_Ticket_Ids_Are_Rejected()
        {
            _queue.Enqueue(Ticket("same", 1_000));

            Assert.That(
                () => _queue.Enqueue(Ticket("same", 1_001)),
                Throws.TypeOf<InvalidOperationException>());
        }

        private static MatchmakingTicket Ticket(string id, int rating) => new MatchmakingTicket(id, rating);
    }
}
