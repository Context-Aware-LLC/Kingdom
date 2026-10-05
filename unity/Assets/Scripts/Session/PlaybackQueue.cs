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
            /// <summary>Extra pacing beyond the normal per-command delay: a play-reveal beat
            /// (MatchRunner's PlayRevealClassifier) that should hold the whole entry — state AND
            /// events — until the reveal has had time to read before its effect becomes visible.
            /// 0 for an entry that played no card.</summary>
            public float ExtraHoldSeconds { get; internal set; }

            /// <summary>The reveal to show while this entry holds, if it played a card. Carried on
            /// the entry rather than fired at classify time because those are different moments:
            /// classification happens when the command RESOLVES, which under any backlog is
            /// several entries before this one reaches the front (see <see cref="TryBeginHead"/>).</summary>
            public PlayReveal? Reveal { get; internal set; }

            /// <summary>The hero power delivery to play while this entry holds, if its command
            /// was one. Stored and fired on the same terms as <see cref="Reveal"/>.</summary>
            public HeroPowerBeat? PowerBeat { get; internal set; }

            /// <summary>Latched by <see cref="TryBeginHead"/> so the begin signal fires once.</summary>
            internal bool Begun;

            /// <summary>Clock reading when this entry reached the front — the origin its
            /// <see cref="ExtraHoldSeconds"/> is measured from. Meaningless until Begun.</summary>
            internal double BegunAt;
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

        /// <summary>Attach a reveal beat to the most recent entry: its hold pads that entry's
        /// pacing window, and the reveal itself is handed back by <see cref="TryBeginHead"/> when
        /// the entry actually starts holding. A no-op if there's no newest entry.</summary>
        public void SetRevealForNewest(PlayReveal reveal)
        {
            if (_newest == null) return;
            _newest.Reveal = reveal;
            _newest.ExtraHoldSeconds = reveal.HoldSeconds;
        }

        /// <summary>Attach a hero power delivery to the most recent entry, on the same terms as
        /// <see cref="SetRevealForNewest"/>.</summary>
        public void SetPowerBeatForNewest(HeroPowerBeat beat)
        {
            if (_newest == null) return;
            _newest.PowerBeat = beat;
            _newest.ExtraHoldSeconds = beat.HoldSeconds;
        }

        /// <summary>
        /// How long from <paramref name="now"/> until the head entry releases, given the same
        /// <paramref name="delaySeconds"/> <see cref="TryDequeue"/> will be asked with.
        ///
        /// <para>A beat that has to LAND on the release cannot assume that is its own hold away:
        /// mid-burst the normal pacing window is the longer of the two, and a short beat started
        /// when the entry reached the front would be over before the board redrew.</para>
        /// </summary>
        public double SecondsUntilHeadRelease(double now, float delaySeconds)
        {
            if (_queue.Count == 0) return 0d;
            Entry head = _queue.Peek();
            if (head.Instant || delaySeconds <= 0f) return 0d;

            double pacing = _lastRelease + delaySeconds - now;
            double hold = (head.Begun ? head.BegunAt : now) + head.ExtraHoldSeconds - now;
            double wait = pacing > hold ? pacing : hold;
            return wait > 0d ? wait : 0d;
        }

        /// <summary>
        /// Hands back the head entry exactly once, the first time it is asked for after reaching
        /// the front, and stamps that moment as the start of its own hold window.
        ///
        /// <para>This exists because "a card was played" and "that card's beat is on screen" are
        /// different moments. Classification happens when the command resolves at authority speed;
        /// an AI turn resolves four commands in four frames and then plays them back over three
        /// seconds. Firing the reveal at classify time put the card on screen over its target
        /// while the PREVIOUS action was still animating, and destroyed it about when the spell
        /// actually landed.</para>
        /// </summary>
        public bool TryBeginHead(double now, out Entry entry)
        {
            entry = null;
            if (_queue.Count == 0) return false;

            var head = _queue.Peek();
            if (head.Begun) return false;

            head.Begun = true;
            head.BegunAt = now;
            entry = head;
            return true;
        }

        /// <summary>
        /// Release the head entry if it's instant, pacing is off, or both of its windows elapsed.
        ///
        /// <para>THE TWO WINDOWS MEASURE FROM DIFFERENT ORIGINS, and that is the whole point.
        /// Normal pacing measures from the previous RELEASE, so it throttles a burst without
        /// making a lone command wait — idle longer than the delay and your play resolves at once.
        /// A reveal hold measures from when this entry reached the FRONT, so it always elapses in
        /// full.</para>
        ///
        /// <para>They used to be one sum against the last release, which meant a hold was
        /// silently skipped for any entry arriving after a pause — i.e. for every deliberate human
        /// play, the exact case the beat exists for. It only ever fired mid-burst.</para>
        ///
        /// <para>Both are skipped when delaySeconds is 0: replay/perf testing wants zero waiting,
        /// full stop, not just the normal pacing.</para>
        /// </summary>
        public bool TryDequeue(double now, float delaySeconds, out Entry entry)
        {
            entry = null;
            if (_queue.Count == 0) return false;

            // Defensive: the hold is measured from BegunAt, so the head must have been stamped.
            // MatchRunner's loop calls TryBeginHead first; this makes a caller that doesn't
            // merely lose the reveal signal rather than lose the hold as well.
            TryBeginHead(now, out _);

            var head = _queue.Peek();
            if (!head.Instant && delaySeconds > 0f &&
                (now - _lastRelease < delaySeconds || now - head.BegunAt < head.ExtraHoldSeconds))
                return false;

            entry = _queue.Dequeue();
            if (_newest == entry) _newest = null;
            if (!entry.Instant) _lastRelease = now;
            return true;
        }
    }
}
