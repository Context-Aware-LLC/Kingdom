using Emberfall.Net.Protocol;

namespace Emberfall.Net.Server
{
    /// <summary>
    /// The closed input union of a <see cref="MatchSession"/> (b1-match-server-design.md §4).
    /// Everything that can happen to a match — bytes, clock, socket lifecycle — arrives as one
    /// of these through <see cref="MatchSession.Dispatch"/>, on one logical thread.
    /// </summary>
    public abstract record SessionCommand
    {
        /// <summary>Raw frame from the seat's CURRENT connection. Decode happens inside Dispatch
        /// so strike/reject ordering is total (§6).</summary>
        public sealed record Frame(int Seat, byte[] Bytes) : SessionCommand;

        /// <summary>Clock pulse. The session re-evaluates every armed deadline against NowMs —
        /// the real host ticks ~1 Hz (commit 2); tests tick explicitly.</summary>
        public sealed record ClockTick : SessionCommand;

        /// <summary>The seat's connection died (socket close/fault or writer overflow).</summary>
        public sealed record Disconnected(int Seat) : SessionCommand;

        /// <summary>A NEW connection presented a decoded <see cref="ReconnectMsg"/> (routed by the
        /// gate, which owns pre-session decode for unbound sockets).</summary>
        public sealed record Reconnect(ReconnectMsg Message, IServerConnection Connection) : SessionCommand;
    }
}
