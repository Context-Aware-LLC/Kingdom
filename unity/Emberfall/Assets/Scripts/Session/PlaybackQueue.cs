using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Session
{
    /// <summary>
    /// The presentation playback queue (decisions/ADR-0004's latency-smoother;
    /// dev-docs/contracts/gameplay-and-session.md:44). Buffers (projected snapshot, projected events) entries per
    /// accepted command and releases them at watchable pace, decoupling animation cadence from
    /// authority cadence — under a networked session a reconnect burst queues up and plays out
    /// instead of teleporting the board.
    ///
    /// UnityEngine-free on purpose (clock injected via <c>now</c>), but this asmdef is a Unity
    /// assembly so dedicated tests are deferred to an edit-mode harness (design §11 finding 6).
    /// </summary>
    public sealed class PlaybackQueue
    {
        /// <summary>One accepted command's presentation payload. Events attach after construction
        /// because IMatchSession fires OnSnapshot before OnEvents for the same command.</summary>
        public sealed class Entry
        {
            public Entry(GameState snapshot, bool instant)
            {
                Snapshot = snapshot;
                Instant = instant;
            }

            public GameState Snapshot { get; }
            public IReadOnlyList<GameEvent> Events { get; internal set; } = System.Array.Empty<GameEvent>();
            /// <summary>Instant entries (mulligan picks, initial state) skip pacing — they resolve
            /// simultaneously in fiction and shouldn't drip one by one (old MatchRunner rule kept).</summary>
            public bool Instant { get; }
            /// <summary>Extra pacing beyond the normal per-command delay, e.g. a spell-cast reveal
            /// beat (MatchRunner's SpellRevealClassifier) that should hold the whole entry — state
            /// AND events — until the reveal has had time to read before its effect becomes visible.
            /// 0 for the overwhelming majority of entries.</summary>
            public float ExtraHoldSeconds { get; internal set; }
        }

        private readonly Queue<Entry> _queue = new Queue<Entry>();
        private Entry _newest;
        private double _lastRelease = double.NegativeInfinity;

        public int Count => _queue.Count;

        public void Enqueue(GameState snapshot, bool instant)
        {
            _newest = new Entry(snapshot, instant);
            _queue.Enqueue(_newest);
        }

        /// <summary>Attach the event batch to the most recent entry (same-command pairing: the
        /// session pushed its snapshot synchronously just before this batch).</summary>
        public void AttachEventsToNewest(IReadOnlyList<GameEvent> events)
        {
            if (_newest != null && events != null && events.Count > 0)
                _newest.Events = events;
        }

        /// <summary>Add pacing to the most recent entry on top of the normal per-command delay
        /// (MatchRunner.SpellRevealClassifier). A no-op if there's no newest entry to hold.</summary>
        public void SetExtraHoldForNewest(float seconds)
        {
            if (_newest != null) _newest.ExtraHoldSeconds = seconds;
        }

        /// <summary>Release the head entry if it's instant, pacing is off, or the window elapsed.
        /// The window is the normal per-command delay plus that entry's own ExtraHoldSeconds (0
        /// unless something asked for a reveal beat) — skipped entirely when delaySeconds is 0
        /// (replay/perf testing wants zero waiting, full stop, not just the normal pacing).</summary>
        public bool TryDequeue(double now, float delaySeconds, out Entry entry)
        {
            entry = null;
            if (_queue.Count == 0) return false;

            var head = _queue.Peek();
            if (!head.Instant && delaySeconds > 0f && now - _lastRelease < delaySeconds + head.ExtraHoldSeconds)
                return false;

            entry = _queue.Dequeue();
            if (_newest == entry) _newest = null;
            if (!entry.Instant) _lastRelease = now;
            return true;
        }
    }
}
