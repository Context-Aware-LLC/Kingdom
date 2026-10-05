using System;
using System.Collections;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>Whose projection the presentation events carry (dev-docs/contracts/gameplay-and-session.md:5).
    /// ActiveSeat (two-human hot-seat view flip) is reserved — it has no input driver yet
    /// (design §11 finding 8); NetworkedGameBootstrap wires the server-dealt seat.</summary>
    public enum PresentationViewer { FixedSeat0, ActiveSeat }

    /// <summary>
    /// Unity-side session pump (M2 WS A4; dev-docs/contracts/gameplay-and-session.md:5). Speaks ONLY
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

        /// <summary>Optional: classify a queued-but-not-yet-visible command as a card play worth
        /// a reveal beat. Null = no reveal beats, instant-effect behavior. Set by the scene's
        /// bootstrap (NetworkedGameBootstrap), which is where the card database lives — this class stays
        /// card-type-agnostic, same as everywhere else in it.</summary>
        public Func<GameState, IReadOnlyList<GameEvent>, PlayReveal?> PlayRevealClassifier { get; set; }

        /// <summary>Fires the instant a card-play entry BEGINS its extra hold — i.e. when it
        /// reaches the front of the playback queue, not when its command resolved — so the reveal
        /// visual is on screen for exactly the beat the entry is held for.</summary>
        public event Action<PlayReveal> OnPlayReveal;

        /// <summary>Per accepted command: decides whether the batch is a hero power with a
        /// delivery to show. Null = no delivery beats. Set by the scene's bootstrap, on the same
        /// terms as <see cref="PlayRevealClassifier"/>.</summary>
        public Func<GameState, IReadOnlyList<GameEvent>, HeroPowerBeat?> HeroPowerBeatClassifier { get; set; }

        /// <summary>Fires when a hero power entry reaches the front of the playback queue. The
        /// second argument is how many seconds from now the entry releases — the moment the
        /// delivery has to land on, which mid-burst is later than the beat's own hold.</summary>
        public event Action<HeroPowerBeat, float> OnHeroPowerBeat;

        private IMatchSession[] _sessions;   // [0]=P0, [1]=P1
        private IIntentSource[] _sources;    // index-parallel to _sessions
        private readonly PlaybackQueue _playback = new PlaybackQueue();
        private bool _running;

        // Ruling 3 (SeatOwnership, b3 §3.1 / a4 §12): how many seats this runner drives and which
        // it renders from. Retires the emergent "same instance at both indices + inert remote
        // source" invariant B3's RC pass found load-bearing in three places (mirrored board, not a
        // crash). Both = hot-seat (default, bit-identical); Single(seat) = networked.
        private SeatOwnership _owner = SeatOwnership.Both;

        // RC-F10 (b3 §7.1-bis): a networked Dead/Ended must stop the runner from OUTSIDE — the
        // loop's only truth source is the mirror, which stops changing after a dead socket.
        private bool _stopRequested;
        private bool _endedFired;
        private MatchOutcome _stopOutcome = MatchOutcome.InProgress;

        /// <summary>
        /// Wire and start pumping a match. Sessions come from LocalMatch.Start (hot-seat / vs-AI)
        /// or, in M2 B3, a NetworkedMatchSession — construction is the caller's business.
        /// <paramref name="owner"/> (ruling 3) says how many seats this runner drives: default
        /// <see cref="SeatOwnership.Both"/> is hot-seat and bit-identical to the pre-ruling
        /// behaviour; <see cref="SeatOwnership.Single"/> is a networked client that owns one seat.
        /// </summary>
        public void StartMatch(
            IMatchSession p0Session, IIntentSource p0Source,
            IMatchSession p1Session, IIntentSource p1Source,
            PresentationViewer viewerMode = PresentationViewer.FixedSeat0,
            SeatOwnership owner = null)
        {
            if (viewerMode == PresentationViewer.ActiveSeat)
                throw new NotSupportedException(
                    "ActiveSeat is reserved for a future two-human mode (a4 design §11 finding 8).");

            _owner = owner ?? SeatOwnership.Both;
            _sessions = new[] { p0Session, p1Session };
            _sources  = new[] { p0Source, p1Source };

            // TRUTH cadence: each OWNED source rides its own session's projection stream. Single()
            // fans only to the seat it owns, so a non-inert remote source can never be fed a stream
            // for a seat it does not own (the RC-F1 third load-bearing site, now a contract).
            for (int i = 0; i < 2; i++)
            {
                if (!_owner.Owns(i)) continue;
                var source = _sources[i];
                _sessions[i].OnSnapshot += st => source.NotifyStateUpdate(st);
            }

            // PRESENTATION cadence: the viewer session feeds the playback queue. OnSnapshot
            // fires before OnEvents for the same command (IMatchSession contract), so the
            // entry exists by the time its batch attaches.
            var view = _sessions[_owner.ViewSeat]; // explicit — no longer the hardcoded _sessions[0]
            view.OnSnapshot += st => _playback.Enqueue(st, instant: st.Phase == TurnPhase.Mulligan);
            view.OnEvents   += batch =>
            {
                _playback.AttachEventsToNewest(batch);

                // view.Current is already the post-command snapshot here (OnSnapshot fires before
                // OnEvents for the same command, per this class's own cadence contract above), so
                // the classifier can resolve the played card's definition straight off it.
                //
                // The reveal is STORED on the entry, not fired here: this runs at authority speed,
                // and the entry it belongs to may be several playback releases away. PlaybackLoop
                // fires it when the entry actually starts holding (PlaybackQueue.TryBeginHead).
                var classify = PlayRevealClassifier;
                PlayReveal? reveal = classify?.Invoke(view.Current, batch);
                if (reveal.HasValue)
                    _playback.SetRevealForNewest(reveal.Value);

                // A hero power plays no card, so a batch is never both.
                HeroPowerBeat? beat = HeroPowerBeatClassifier?.Invoke(view.Current, batch);
                if (beat.HasValue)
                    _playback.SetPowerBeatForNewest(beat.Value);
            };

            _running = true;

            // Initial state: OWNED sources replan off it, presentation renders it immediately.
            for (int i = 0; i < 2; i++)
                if (_owner.Owns(i))
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
                // Head reached the front → its hold window just started. Show its reveal now, so
                // the beat the entry is held for is the beat the card is on screen for.
                if (_playback.TryBeginHead(Time.timeAsDouble, out var begun))
                {
                    if (begun.Reveal.HasValue)
                        OnPlayReveal?.Invoke(begun.Reveal.Value);
                    if (begun.PowerBeat.HasValue)
                        OnHeroPowerBeat?.Invoke(begun.PowerBeat.Value,
                            (float)_playback.SecondsUntilHeadRelease(Time.timeAsDouble, commandDelaySeconds));
                }

                if (_playback.TryDequeue(Time.timeAsDouble, commandDelaySeconds, out var entry))
                {
                    OnStateUpdated?.Invoke(entry.Snapshot);
                    if (entry.Events.Count > 0)
                        OnMatchEvent?.Invoke(entry.Events);

                    if (entry.Snapshot.Outcome != MatchOutcome.InProgress)
                    {
                        _endedFired = true;
                        OnMatchEnded?.Invoke(entry.Snapshot.Outcome);
                        yield break;
                    }
                }
                yield return null;
            }

            // RC-F10: StopMatch() broke the loop (Dead/Ended off a stale mirror whose Outcome never
            // flips). Fire OnMatchEnded once, AFTER the queue drained, so the last animation still
            // plays — death stops input, not the story of how it ended.
            if (_stopRequested && !_endedFired)
            {
                _endedFired = true;
                OnMatchEnded?.Invoke(_stopOutcome);
            }
        }

        /// <summary>Stop the runner from OUTSIDE (RC-F10, b3 §7.1-bis). NetworkedGameBootstrap calls
        /// this from <c>OnConnectionStateChanged</c> on Dead/Ended, because the submit loop's only
        /// truth source is the mirror, which a dead socket strands at Outcome=InProgress forever.
        /// Stops submits immediately; lets PlaybackLoop drain before OnMatchEnded. Hot-seat never
        /// calls it. <paramref name="finalOutcome"/> is what the banner shows (e.g. a forfeit loss).</summary>
        public void StopMatch(MatchOutcome finalOutcome = MatchOutcome.InProgress)
        {
            if (!_running && _playback.Count == 0 && _endedFired) return;
            _stopRequested = true;
            _stopOutcome = finalOutcome;
            _running = false;
        }

        private int PickSeat(GameState state)
        {
            if (state.Phase == TurnPhase.Mulligan)
                // Both → 0-then-1 (client-serialized, unchanged); Single(s) → s until s is done, then
                // -1 (each networked client drives ONLY its own confirm — RC-F11 simultaneous mulligan).
                return _owner.PickMulligan(state);
            return state.SeatOf(state.ActivePlayer);
        }

        private void OnDestroy()
        {
            _running = false;
        }
    }
}
