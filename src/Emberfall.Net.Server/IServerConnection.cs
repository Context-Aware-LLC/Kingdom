namespace Emberfall.Net.Server
{
    /// <summary>
    /// The session's view of one client socket. The contract that keeps the single-consumer
    /// model safe (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §4, RC F5): <see cref="TryEnqueue"/> NEVER blocks —
    /// the real implementation (commit 2) owns a bounded writer queue + pump task; a full queue
    /// or dead writer returns false and the session routes the seat to the Disconnected path.
    /// A slow client can stall only itself, never the opponent's events or the timers.
    /// </summary>
    public interface IServerConnection
    {
        /// <summary>Queue one encoded frame for send. False = writer dead/overflowed (treat as disconnect).</summary>
        bool TryEnqueue(byte[] frame);

        /// <summary>Request a close (idempotent, non-blocking). 1008 = policy violation, 1013 = server full.</summary>
        void Close(int closeCode, string reason);
    }
}
