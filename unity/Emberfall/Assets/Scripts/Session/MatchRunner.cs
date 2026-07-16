using System;
using System.Collections;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>Whose projection the presentation events carry (a4-session-seam-design.md §4).
    /// ActiveSeat (two-human hot-seat view flip) is reserved — it has no input driver yet
    /// (design §11 finding 8); GameBootstrap wires FixedSeat0.</summary>
    public enum PresentationViewer { FixedSeat0, ActiveSeat }

    /// <summary>
    /// Unity-side session pump (M2 WS A4; a4-session-seam-design.md §4). Speaks ONLY
    /// <see cref="IMatchSession"/> + <see cref="IIntentSource"/> — never a concrete host, never
    /// raw state — so B3's NetworkedMatchSession slots in without touching this class or any
    /// subscriber. Async-tolerant: SubmitAsync may resolve this frame (local) or much later
    /// (networked); the loop just waits.
    ///
    /// Two cadences (design §5/§7):
    ///  • TRUTH — intent sources get NotifyStateUpdate the moment their session's projection
    ///    lands (AI replans immediately; input gating reasons on truth).
    ///  • PRESENTATION — OnStateUpdated/OnMatchEvent/OnMatchEnded fire from the playback queue
    ///    at commandDelaySeconds pace. Signatures unchanged from M1 (presentation events stable),
    ///    but per entry OnStateUpdated now fires BEFORE OnMatchEvent: snapshots are fresh
    ///    projected objects, so event handlers resolving ids need the post-command state (the old
    ///    events-first order only worked because state was mutated in place).
    /// </summary>
    public sealed class MatchRunner : MonoBehaviour
    {
        [Tooltip("Playback pacing: pause between released command entries so state changes land " +
                 "at a watchable pace. 0 = fastest (replay/perf testing). Mulligan picks always " +
                 "play at full speed. Pacing no longer throttles the authority — commands resolve " +
                 "at truth speed and queue here.")]
        [SerializeField, Range(0f, 3f)] private float commandDelaySeconds = 0.75f;

        /// <summary>Per-entry pacing delay in seconds; settable at runtime (e.g. a speed toggle).</summary>
        public float CommandDelaySeconds
        {
            get => commandDelaySeconds;
            set => commandDelaySeconds = Mathf.Max(0f, value);
        }

        /// <summary>Fires per released playback entry with that command's projected event batch.</summary>
        public event Action<IReadOnlyList<GameEvent>> OnMatchEvent;

        /// <summary>Fires per released playback entry with the projected snapshot (match start included).</summary>
        public event Action<GameState> OnStateUpdated;

        /// <summary>Fires once, after the terminal entry has played out (the lethal blow is visible
        /// before the banner).</summary>
        public event Action<MatchOutcome> OnMatchEnded;

        private IMatchSession[] _sessions;   // [0]=P0, [1]=P1
        private IIntentSource[] _sources;    // index-parallel to _sessions
        private readonly PlaybackQueue _playback = new PlaybackQueue();
        private bool _running;

        /// <summary>
        /// Wire and start pumping a match. Sessions come from LocalMatch.Start (hot-seat / vs-AI)
        /// or, in M2 B3, a NetworkedMatchSession — construction is the caller's business.
        /// </summary>
        public void StartMatch(
            IMatchSession p0Session, IIntentSource p0Source,
            IMatchSession p1Session, IIntentSource p1Source,
            PresentationViewer viewerMode = PresentationViewer.FixedSeat0)
        {
            if (viewerMode == PresentationViewer.ActiveSeat)
                throw new NotSupportedException(
                    "ActiveSeat is reserved for a future two-human mode (a4 design §11 finding 8).");

            _sessions = new[] { p0Session, p1Session };
            _sources  = new[] { p0Source, p1Source };

            // TRUTH cadence: each source rides its own session's projection stream.
            for (int i = 0; i < 2; i++)
            {
                var source = _sources[i];
                _sessions[i].OnSnapshot += st => source.NotifyStateUpdate(st);
            }

            // PRESENTATION cadence: the viewer session feeds the playback queue. OnSnapshot
            // fires before OnEvents for the same command (IMatchSession contract), so the
            // entry exists by the time its batch attaches.
            var view = _sessions[0]; // FixedSeat0 — the local human/AI seat 0 view
            view.OnSnapshot += st => _playback.Enqueue(st, instant: st.Phase == TurnPhase.Mulligan);
            view.OnEvents   += batch => _playback.AttachEventsToNewest(batch);

            _running = true;

            // Initial state: sources replan off it, presentation renders it immediately.
            for (int i = 0; i < 2; i++)
                _sources[i].NotifyStateUpdate(_sessions[i].Current);
            _playback.Enqueue(view.Current, instant: true);

            StartCoroutine(SubmitLoop());
            StartCoroutine(PlaybackLoop());
        }

        private IEnumerator SubmitLoop()
        {
            while (_running)
            {
                // Turn/mulligan flags are PUBLIC — identical on every projection, so any
                // session's Current answers "who acts?" (a4 design §4, confirmed §11).
                var state = _sessions[0].Current;

                if (state.Outcome != MatchOutcome.InProgress)
                {
                    _running = false;   // PlaybackLoop fires OnMatchEnded after the drain
                    yield break;
                }

                int seat = PickSeat(state);
                if (seat < 0 || !_sources[seat].TryGetIntent(out var intent))
                {
                    yield return null;
                    continue;
                }

                var task = _sessions[seat].SubmitAsync(intent);
                while (!task.IsCompleted)
                    yield return null;   // async-tolerant: local resolves now, networked later

                if (task.IsFaulted)
                {
                    // Sessions resolve rejects normally — a fault is transport/engine breakage.
                    Debug.LogError($"[MatchRunner] SubmitAsync faulted: {task.Exception?.GetBaseException()}");
                    yield return null;
                    continue;
                }

                var result = task.Result;
                if (!result.Accepted)
                    _sources[seat].OnRejected(result.Reason.Value);

                yield return null;
            }
        }

        private IEnumerator PlaybackLoop()
        {
            while (_running || _playback.Count > 0)
            {
                if (_playback.TryDequeue(Time.timeAsDouble, commandDelaySeconds, out var entry))
                {
                    OnStateUpdated?.Invoke(entry.Snapshot);
                    if (entry.Events.Count > 0)
                        OnMatchEvent?.Invoke(entry.Events);

                    if (entry.Snapshot.Outcome != MatchOutcome.InProgress)
                    {
                        OnMatchEnded?.Invoke(entry.Snapshot.Outcome);
                        yield break;
                    }
                }
                yield return null;
            }
        }

        private int PickSeat(GameState state)
        {
            if (state.Phase == TurnPhase.Mulligan)
            {
                if (!state.Mulligan0Done) return 0;
                if (!state.Mulligan1Done) return 1;
                return -1;
            }
            return state.SeatOf(state.ActivePlayer);
        }

        private void OnDestroy()
        {
            _running = false;
        }
    }
}
