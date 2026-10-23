using System;

namespace Kingdom.Session
{
    /// <summary>
    /// The first-session funnel's <c>session_id</c>: one value per process, minted on first read.
    ///
    /// <para><b>The one place it is minted.</b> Crash reporting tags every Sentry event with it, and
    /// the funnel (pre-publication item 2) puts it on every client telemetry row and on the update
    /// gate's request. A Sentry event reaches the funnel as a <c>crash</c> drop only by carrying the
    /// SAME value as the rows around it, so nothing else may mint one. Read <see cref="Id"/>; never
    /// construct a second.</para>
    ///
    /// <para><b>Per process, not persisted.</b> A relaunch is a new session. A row queued by an earlier
    /// process keeps the id it was written with — that is the queue's job, not this class's.</para>
    ///
    /// <para><b>Not an identity.</b> A random GUID that names nothing about the player or the device;
    /// the device token (<see cref="DeviceIdentity"/>) and the account are separate keys.</para>
    /// </summary>
    public static class FunnelSession
    {
        private static readonly Lazy<string> _id =
            new Lazy<string>(() => Guid.NewGuid().ToString("N"), isThreadSafe: true);

        /// <summary>This process's session id: 32 lowercase hex characters, stable until exit.</summary>
        public static string Id => _id.Value;
    }
}
