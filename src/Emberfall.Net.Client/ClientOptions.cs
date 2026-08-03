using System;

namespace Emberfall.Net.Client
{
    /// <summary>The session layer's only clock (integer ms), mirroring the server's
    /// <c>ITimeSource</c> so every timer path is deterministic under test
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b3-networked-session-design.md §4.2, §11 — no <c>Task.Delay</c> in tests).</summary>
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

        /// <summary>A requested resync unanswered for this long is re-sent (b3 §4.5).
        /// <para>Without this the budget cannot fire in the case it was written for: it counts
        /// REQUESTS, not unanswered ones, so a client that asks once and is never answered sits at
        /// 1-of-3 forever. One lost frame — a swallowed <c>SendAsync</c> throw, or a reorder the
        /// server strikes — wedges the match permanently with heartbeats still flowing.</para></summary>
        public long ResyncTimeoutMs { get; init; } = 5_000;

        /// <summary>Editor/dev builds capture the raw frame on mismatch (b3 §6.3). Release counts +
        /// telemetry. Off by default; the loopback/soak harness turns it on.</summary>
        public bool DevCapture { get; init; }

        /// <summary>
        /// The guest device token sent in the connect <c>HelloMsg</c> (C-G1-8; telemetry-spec §0,
        /// accounts posture decision 6a). Generated ONCE per device and persisted by the host app —
        /// a token regenerated per launch is the counter it replaced, wearing a UUID.
        /// <para>A UUID by convention, because Supabase (the ADR-0001 destination) keys users on
        /// UUIDs, so guests convert one-to-one when identity lands.</para>
        /// <para>Empty means the server refuses the connect. That is deliberate: an unattributable
        /// match is worse than a failed one, because it silently pollutes the balance sample.</para>
        /// </summary>
        public string DeviceToken { get; init; } = "";

        /// <summary>Client build provenance for the funnel (telemetry-spec §4). Optional.</summary>
        public string Platform { get; init; } = "";
        public string BuildId { get; init; } = "";

        /// <summary>
        /// This device's display name, sent in the hello for the opponent's nameplate (EF-073).
        /// Optional — empty is the honest first-run state ("never set") and never a refused connect.
        ///
        /// <para><b>Pass the raw profile name, not a UI placeholder.</b> "You", "Opponent" and
        /// "Unnamed player" are strings our own chrome writes, so the server RESERVES them
        /// (VULN-002 §4.3) and will refuse one — sending a placeholder here gets the seat id on the
        /// opponent's plate, which is the same thing an empty name gets, only after a detour. The
        /// fallback is the renderer's job.</para>
        ///
        /// <para><b>Setting this does not make it true.</b> The server re-runs
        /// <c>PlayerName.Check</c> on arrival and substitutes its own fallback if this fails; that
        /// is the check that counts, and this end cannot make a name legal by asserting it.</para>
        /// </summary>
        public string DisplayName { get; init; } = "";

        /// <summary>The contract version this client was built against, sent in the hello so the
        /// server can compare (it previously never learned the client's).</summary>
        public string ContractVersion { get; init; } = "interfaces.v3";

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
    public sealed record Misattribution(string MatchId, int Seat, ulong PendingSeq, string RejectReason, ulong StateVersion);

    /// <summary>§6.3 capture: enough to replay the decode locally in a unit test (the playbook input).</summary>
    public sealed record DesyncCapture(string MatchId, int Seat, ulong StateVersion, ulong EnvelopeSeq, string WireHash, string LocalHash, byte[] RawFrame);
}
