using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Net.Server.Matchmaking
{
    /// <summary>
    /// Tunables for the launch queue. All ratings are integer points and all time comes from the
    /// server clock, so queue tests do not sleep and there is no floating-point boundary drift.
    /// </summary>
    public sealed record MatchmakingQueueOptions
    {
        /// <summary>Rating used by today's guest path until the account store supplies MMR.</summary>
        public int DefaultRating { get; init; } = 1_000;
        public int InitialRatingBand { get; init; } = 100;
        public int RatingBandStep { get; init; } = 100;
        public long BandWideningIntervalMs { get; init; } = 10_000;
    }

    /// <summary>
    /// In-memory FIFO matchmaking with a search band that grows as either player waits. The
    /// oldest ticket stays at the head; within that ticket's search, the earliest compatible
    /// partner wins. A long-waiting player can therefore accept a fresh arrival without a newer,
    /// narrower ticket bypassing it.
    /// </summary>
    public sealed class BandWideningMatchmakingQueue : IMatchmakingQueue
    {
        private sealed record Entry(MatchmakingTicket Ticket, long EnqueuedAtMs);

        private readonly object _lock = new object();
        private readonly List<Entry> _waiting = new List<Entry>();
        private readonly Dictionary<string, Entry> _byId =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly ITimeSource _time;
        private readonly MatchmakingQueueOptions _options;

        public BandWideningMatchmakingQueue(ITimeSource time, MatchmakingQueueOptions options)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (_options.InitialRatingBand < 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Initial rating band cannot be negative.");
            if (_options.RatingBandStep < 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Rating band step cannot be negative.");
            if (_options.BandWideningIntervalMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Band widening interval must be positive.");
        }

        public int Count
        {
            get { lock (_lock) return _waiting.Count; }
        }

        public void Enqueue(MatchmakingTicket ticket)
        {
            if (ticket is null) throw new ArgumentNullException(nameof(ticket));
            if (string.IsNullOrWhiteSpace(ticket.Id))
                throw new ArgumentException("A matchmaking ticket id is required.", nameof(ticket));

            lock (_lock)
            {
                if (_byId.ContainsKey(ticket.Id))
                    throw new InvalidOperationException($"Matchmaking ticket '{ticket.Id}' is already queued.");

                var entry = new Entry(ticket, _time.NowMs);
                _waiting.Add(entry);
                _byId.Add(ticket.Id, entry);
            }
        }

        public bool Remove(string ticketId)
        {
            if (ticketId is null) throw new ArgumentNullException(nameof(ticketId));
            lock (_lock)
            {
                if (!_byId.Remove(ticketId, out var entry)) return false;
                return _waiting.Remove(entry);
            }
        }

        public MatchmakingPair? TryDequeueMatch()
        {
            lock (_lock)
            {
                long nowMs = _time.NowMs;
                if (_waiting.Count < 2) return null;
                const int first = 0;
                for (int second = 1; second < _waiting.Count; second++)
                {
                    if (!Compatible(_waiting[first], _waiting[second], nowMs)) continue;

                    var older = _waiting[first];
                    var partner = _waiting[second];
                    _waiting.RemoveAt(second);
                    _waiting.RemoveAt(first);
                    _byId.Remove(older.Ticket.Id);
                    _byId.Remove(partner.Ticket.Id);
                    return new MatchmakingPair(older.Ticket, partner.Ticket);
                }
                return null;
            }
        }

        public Task WaitForMatchOpportunityAsync(CancellationToken ct)
        {
            // The delay only schedules reevaluation. Eligibility itself always reads ITimeSource,
            // keeping policy boundaries deterministic in tests and independent of timer drift.
            return Task.Delay(TimeSpan.FromMilliseconds(_options.BandWideningIntervalMs), ct);
        }

        private bool Compatible(Entry first, Entry second, long nowMs)
        {
            long difference = Math.Abs((long)first.Ticket.Rating - second.Ticket.Rating);
            long acceptedBand = Math.Max(BandAt(first, nowMs), BandAt(second, nowMs));
            return difference <= acceptedBand;
        }

        private long BandAt(Entry entry, long nowMs)
        {
            long waitedMs = Math.Max(0, nowMs - entry.EnqueuedAtMs);
            long intervals = waitedMs / _options.BandWideningIntervalMs;
            if (_options.RatingBandStep == 0) return _options.InitialRatingBand;
            if (intervals > (long.MaxValue - _options.InitialRatingBand) / _options.RatingBandStep)
                return long.MaxValue;
            return _options.InitialRatingBand + intervals * _options.RatingBandStep;
        }
    }
}
