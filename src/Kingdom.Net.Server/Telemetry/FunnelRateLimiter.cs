using System;
using System.Collections.Generic;

namespace Kingdom.Net.Server.Telemetry
{
    /// <summary>
    /// A token bucket per key, counted in ROWS, for <c>POST /telemetry/client</c>. A burst sized to
    /// one session's rows, then a steady refill: a launch that queued a session's worth of rows
    /// offline flushes them in one go, and a client looping on the route gets a row every few seconds
    /// and nothing more.
    ///
    /// <para><b>In memory, and that is enough:</b> the server is one Fly machine, and a restart that
    /// forgets every bucket hands each device one extra burst — rows that are counting claims anyway.</para>
    ///
    /// <para><b>Bounded.</b> The key is client-asserted (a device token hashes to whatever the client
    /// chose), so a caller rotating keys must not grow this table without limit. Past
    /// <c>maxKeys</c> the table sheds every bucket that has refilled to full — indistinguishable
    /// from a fresh one, so dropping it changes nothing — and if that frees nothing, a NEW key is
    /// refused. Refusing is a 429, which the client answers by keeping its rows queued: telemetry
    /// waits, play does not.</para>
    /// </summary>
    public sealed class FunnelRateLimiter
    {
        private readonly ITimeSource _time;
        private readonly int _burst;
        private readonly long _refillMsPerRow;
        private readonly int _maxKeys;
        private readonly object _lock = new object();

        /// <summary>Milli-rows, so refill is integer arithmetic (the same trick as <see cref="TokenBucket"/>).</summary>
        private readonly Dictionary<string, (long Milli, long LastMs)> _buckets =
            new Dictionary<string, (long, long)>(StringComparer.Ordinal);

        public FunnelRateLimiter(ITimeSource time, int burst, long refillMsPerRow, int maxKeys = 20_000)
        {
            if (burst < 1) throw new ArgumentOutOfRangeException(nameof(burst));
            if (refillMsPerRow < 1) throw new ArgumentOutOfRangeException(nameof(refillMsPerRow));
            _time = time;
            _burst = burst;
            _refillMsPerRow = refillMsPerRow;
            _maxKeys = Math.Max(1, maxKeys);
        }

        public int Burst => _burst;

        /// <summary>Takes <paramref name="rows"/> from the key's bucket, all or none.</summary>
        public bool TryTake(string key, int rows)
        {
            if (rows <= 0) return true;
            long now = _time.NowMs;
            long cap = _burst * 1000L;
            lock (_lock)
            {
                long milli;
                if (_buckets.TryGetValue(key, out var b))
                {
                    milli = Math.Min(cap, b.Milli + Math.Max(0, now - b.LastMs) * 1000L / _refillMsPerRow);
                }
                else
                {
                    if (_buckets.Count >= _maxKeys && !Shed(now, cap)) return false;
                    milli = cap;
                }

                long cost = rows * 1000L;
                if (milli < cost)
                {
                    _buckets[key] = (milli, now);
                    return false;
                }
                _buckets[key] = (milli - cost, now);
                return true;
            }
        }

        /// <summary>Live keys, for the memory-bound test.</summary>
        public int KeyCount { get { lock (_lock) return _buckets.Count; } }

        private bool Shed(long now, long cap)
        {
            var full = new List<string>();
            foreach (var kv in _buckets)
            {
                long milli = kv.Value.Milli + Math.Max(0, now - kv.Value.LastMs) * 1000L / _refillMsPerRow;
                if (milli >= cap) full.Add(kv.Key);
            }
            foreach (var k in full) _buckets.Remove(k);
            return _buckets.Count < _maxKeys;
        }
    }
}
