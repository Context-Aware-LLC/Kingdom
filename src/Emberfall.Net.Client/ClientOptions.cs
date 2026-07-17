using System;

namespace Emberfall.Net.Client
{
    /// <summary>The session layer's only clock (integer ms), mirroring the server's
    /// <c>ITimeSource</c> so every timer path is deterministic under test
    /// (b3-networked-session-design.md §4.2, §11 — no <c>Task.Delay</c> in tests).</summary>
    public interface IClientClock
    {
        long NowMs { get; }
    }

    /// <summary>Wall clock for real play (commit 2 wires this in).</summary>
    public sealed class SystemClientClock : IClientClock
    {
        public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Cadence + budget knobs, one record mirroring b1 §7's <c>ServerOptions</c> so the pending
    /// timer-values memo is a one-line change on both sides (b3 §4.2). Defaults are b1 §11's.
    /// </summary>
    public sealed record ClientOptions
    {
        /// <summary>Send a heartbeat after this much send-idle time (b1 §11: 10s).</summary>
        public long HeartbeatIdleMs { get; init; } = 10_000;

        /// <summary>No inbound frame of any type for this long ⇒ <see cref="ConnectionState.Reconnecting"/>
        /// (b3 §4.2: 30s — any inbound frame proves liveness, not "3 missed echoes").</summary>
        public long InboundSilenceMs { get; init; } = 30_000;

        /// <summary>Guaranteed reconnect floor from client-detected loss (b3 §4.3, decision 20: 60s).</summary>
        public long ReconnectBudgetMs { get; init; } = 60_000;

        /// <summary>Resyncs tolerated per match before escalation → <see cref="ConnectionState.Dead"/>
        /// (b3 §4.5: 3; the 4th is fatal).</summary>
        public int ResyncBudget { get; init; } = 3;

        /// <summary>Editor/dev builds capture the raw frame on mismatch (b3 §6.3). Release counts +
        /// telemetry. Off by default; the loopback/soak harness turns it on.</summary>
        public bool DevCapture { get; init; }

        public IClientClock Clock { get; init; } = new SystemClientClock();

        /// <summary>Sink for the RC-F7 interim mis-attribution log (b3 §4.4): a late <c>RejectMsg</c>
        /// for an already-resolved seq. THIS LOG IS THE EVIDENCE GATE for whether <c>appliedSeq</c>
        /// returns at G1 (§16 ruling 2). Never null-crashes; unset ⇒ dropped.</summary>
        public Action<Misattribution>? OnMisattribution { get; init; }

        /// <summary>Sink for the §6.3 desync capture (raw frame + version) — the desync-playbook input.</summary>
        public Action<DesyncCapture>? OnDesyncCapture { get; init; }
    }

    /// <summary>RC-F7 interim record: we resolved a submit <c>Ok</c> off a snapshot that a server
    /// clock produced, and the real intent came back rejected. Belief was wrong; the mirror was
    /// not (b3 §4.4). Logged, never acted on beyond the log.</summary>
    public readonly record struct Misattribution(string MatchId, int Seat, ulong PendingSeq, string RejectReason, ulong StateVersion);

    /// <summary>§6.3 capture: enough to replay the decode locally in a unit test (the playbook input).</summary>
    public readonly record struct DesyncCapture(string MatchId, int Seat, ulong StateVersion, ulong EnvelopeSeq, string WireHash, string LocalHash, byte[] RawFrame);
}
