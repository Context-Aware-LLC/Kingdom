namespace Emberfall.Net.Server
{
    /// <summary>
    /// Every tunable the B1 server owns, in one record (b1-match-server-design.md §7):
    /// the values are B1-ratified defaults pending a decision memo — match-lifecycle.md §4
    /// gives them as examples, not hard numbers — so the memo lands as a one-line change here.
    /// All time is integer milliseconds from <see cref="ITimeSource"/>; no float anywhere.
    /// </summary>
    public sealed record ServerOptions
    {
        // ── Clocks (design §7) ───────────────────────────────────────────────────
        public long MulliganMs { get; init; } = 45_000;
        public long TurnMs { get; init; } = 75_000;
        /// <summary>The rope: a second turn_timer broadcast this long before the turn deadline.</summary>
        public long RopeMs { get; init; } = 15_000;
        public long DisconnectGraceMs { get; init; } = 60_000;
        /// <summary>Consecutive clock-fired auto-end-turns before the seat idle-forfeits (§7).</summary>
        public int IdleForfeitAutoTurns { get; init; } = 2;

        // ── Liveness (design §11) ────────────────────────────────────────────────
        public long HeartbeatTimeoutMs { get; init; } = 30_000;

        // ── Policy gate (design §6) ──────────────────────────────────────────────
        /// <summary>Token-bucket capacity (whole tokens).</summary>
        public int RateCapacity { get; init; } = 10;
        /// <summary>Refill rate, whole tokens per second (integer math: 1 token/s = 1 milli-token/ms).</summary>
        public int RateRefillPerSecond { get; init; } = 2;
        public int InTurnIntentCost { get; init; } = 1;
        public int OutOfTurnIntentCost { get; init; } = 4;
        /// <summary>Consecutive rejects-while-empty before the connection is dropped (§6.5).</summary>
        public int EmptyRejectsToDisconnect { get; init; } = 3;
        /// <summary>Pre-decode frame cap (§6.5) — far below ProtocolCodec.MaxMessageBytes on purpose.</summary>
        public int MaxFrameBytes { get; init; } = 16 * 1024;
        /// <summary>Protocol-offense strikes (oversized/malformed/replayed frames) before close (§6.1).</summary>
        public int MaxStrikes { get; init; } = 3;

        // ── Host (design §5/§11) ─────────────────────────────────────────────────
        /// <summary>Max concurrent matches; new pairings refused at cap (RC Q4 ruling).</summary>
        public int MaxSessions { get; init; } = 64;
        public string EngineContractVersion { get; init; } = "interfaces.v3";
    }
}
