using Kingdom.Net.Protocol;

namespace Kingdom.Net.Server
{
    /// <summary>
    /// The closed input union of a <see cref="MatchSession"/> (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §4).
    /// Everything that can happen to a match — bytes, clock, socket lifecycle — arrives as one
    /// of these through <see cref="MatchSession.Dispatch"/>, on one logical thread.
    ///
    /// Frames and disconnects are keyed by the ORIGINATING connection object, never by seat
    /// number: the session resolves the seat by reference identity, so a socket that was never
    /// bound to a seat (a forged or superseded reconnect) can neither inject frames into a live
    /// seat's policy state nor vacate it.
    /// </summary>
    public abstract record SessionCommand
    {
        /// <summary>Raw frame from a connection. Decode happens inside Dispatch so strike/reject
        /// ordering is total (§6). Dropped silently if the connection is not bound to a seat.</summary>
        public sealed record Frame(IServerConnection Origin, byte[] Bytes) : SessionCommand;

        /// <summary>Clock pulse. The session re-evaluates every armed deadline against NowMs —
        /// the real host ticks ~1 Hz (SessionPump); tests tick explicitly.</summary>
        public sealed record ClockTick : SessionCommand;

        /// <summary>The connection died (socket close/fault or writer overflow). Vacates the seat
        /// it is bound to; ignored if it was already superseded by a reconnect.</summary>
        public sealed record Disconnected(IServerConnection Origin) : SessionCommand;

        /// <summary>A NEW connection presented a decoded <see cref="ReconnectMsg"/> (routed by the
        /// gate, which owns pre-session decode for unbound sockets).</summary>
        public sealed record Reconnect(ReconnectMsg Message, IServerConnection Connection) : SessionCommand;
    }
}
