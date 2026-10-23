using System;
using Kingdom.Net.Client.Gate;

namespace Kingdom.Net.Client.Telemetry
{
    /// <summary>
    /// The milestones, one method each: builds the row with this process's stamp and the clock's
    /// time, queues it durably, and asks the uploader for a debounced flush. The host decides WHEN
    /// each one happened; this decides what the row says. Never throws.
    ///
    /// <para><b><c>exit</c> does not flush.</b> It is written from <c>OnApplicationQuit</c>, where
    /// a request would be abandoned mid-flight; it is on disk, and the next launch sends it under
    /// this process's session id.</para>
    /// </summary>
    public sealed class FunnelRecorder
    {
        private readonly FunnelQueue _queue;
        private readonly FunnelUploader? _uploader;
        private readonly IClientClock _clock;
        private readonly FunnelStamp _stamp;
        private bool _paused;

        public FunnelRecorder(FunnelQueue queue, FunnelUploader? uploader, IClientClock clock, FunnelStamp stamp)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _uploader = uploader;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _stamp = stamp;
        }

        public FunnelStamp Stamp => _stamp;

        /// <summary>True once this process has recorded a <c>sign_in</c> row reading
        /// <c>signed_in</c>.</summary>
        public bool HasSignedIn { get; private set; }

        public void Launch() => Record(now => FunnelRow.Plain(FunnelMilestone.Launch, _stamp, now));

        public void Gate(UpdateGateResult result)
        {
            if (result == null) { GateError(); return; }
            Record(now => FunnelRow.Gate(_stamp, now, FunnelOutcome.ForGate(result.Verdict), result.Attempts));
        }

        /// <summary>The gate check threw rather than returning a verdict.</summary>
        public void GateError(int attempts = 0)
            => Record(now => FunnelRow.Gate(_stamp, now, FunnelOutcome.Error, attempts));

        public void SignedIn(string? cause)
        {
            HasSignedIn = true;
            Record(now => FunnelRow.SignIn(_stamp, now, FunnelOutcome.SignedIn, cause));
        }

        public void SignInFailed(string? cause)
            => Record(now => FunnelRow.SignIn(_stamp, now, FunnelOutcome.Failed, cause));

        public void SignInSkipped(string? cause)
            => Record(now => FunnelRow.SignIn(_stamp, now, FunnelOutcome.Skipped, cause));

        public void BoardPlayable(string matchId)
            => Record(now => FunnelRow.BoardPlayable(_stamp, now, matchId));

        public void Queue(string source) => Record(now => FunnelRow.Queue(_stamp, now, source));

        /// <summary>A pause while already paused records nothing.</summary>
        public void Pause()
        {
            if (_paused) return;
            _paused = true;
            Record(now => FunnelRow.Plain(FunnelMilestone.Pause, _stamp, now));
        }

        /// <summary>Only a resume that follows a recorded pause is a row. Unity also reports an
        /// unpause as every object starts, which is a launch, not a resume.</summary>
        public void Resume()
        {
            if (!_paused) return;
            _paused = false;
            Record(now => FunnelRow.Plain(FunnelMilestone.Resume, _stamp, now));
        }

        public void Exit() => Record(now => FunnelRow.Plain(FunnelMilestone.Exit, _stamp, now), flush: false);

        private void Record(Func<long, FunnelRow> make, bool flush = true)
        {
            try
            {
                _queue.Enqueue(make(_clock.NowMs));
                if (flush) _uploader?.RequestFlush();
            }
            catch (Exception) { /* FunnelQueue never throws; a broken clock must not reach the caller */ }
        }
    }
}
