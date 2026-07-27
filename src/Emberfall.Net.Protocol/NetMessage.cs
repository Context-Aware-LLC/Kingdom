using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// The closed wire vocabulary (a5-wire-protocol-design.md §5; netcode-architecture.md v2
    /// §3.2/§3.3). One sealed type per message; the envelope discriminator strings live in
    /// <see cref="ProtocolCodec"/>. Bodies are canonical, float-free, integer-only.
    /// </summary>
    public abstract record NetMessage;

    // ── Client → Server ──────────────────────────────────────────────────────────

    /// <summary>
    /// The connect handshake — FIRST frame on a fresh socket, before pairing (C-G1-8).
    /// <para><see cref="DeviceToken"/> is the guest identity from telemetry-spec §0 / accounts
    /// posture decision 6a: client-generated once, persisted on device, stable across restarts.
    /// It is the reason an emitted telemetry row can be attributed at all.</para>
    /// <para><b>The token never leaves the server.</b> It is hashed into an account id on arrival
    /// and is never placed in <c>PlayerId</c>, which is broadcast to the opponent in
    /// <c>OpponentInfo</c> — putting identity there would hand every player's stable token to a
    /// stranger.</para>
    /// <para><see cref="ClientContractVersion"/> is the first client→server carrier of the
    /// contract version; the server previously never learned it (<c>EngineContractVersion</c>
    /// travels only server→client on <see cref="MatchStartMsg"/>). Carried here, enforced
    /// separately.</para>
    /// <para><see cref="Platform"/>/<see cref="BuildId"/> are funnel provenance (telemetry-spec
    /// §4 <c>app_install</c>) and are optional — absent means empty, never a refused connect.</para>
    /// </summary>
    public sealed record HelloMsg(
        // VULN-002 (security/known-vulnerabilities, LATENT — trigger rows "A player display name, or
        // any player-typed text the opponent sees" and "Adding a field to HelloMsg"). ADDING A
        // DISPLAY NAME HERE ARMS IT. A player profile with a validated DisplayName already exists
        // on-device (Emberfall.Core.Collection.PlayerName); carrying it here inverts the direction of
        // trust — the opponent's client renders a string THEY chose, and their client's validator is
        // advice they can decline. Read VULN-002 §4 first: validate on arrival with the same
        // PlayerName.Check (that assembly is UnityEngine-free so the server can call it), never echo
        // an unvalidated name into OpponentInfo, and reserve the strings our own UI authors ("You",
        // "Opponent", "Unnamed player"). §2 fact 1 of that entry is this record's current shape.
        string DeviceToken,
        string ClientContractVersion = "",
        string Platform = "",
        string BuildId = "") : NetMessage;

    /// <summary>A player intent + the client's last-applied StateVersion (stale-client detection,
    /// netcode §3.2). Which decoded intents a client may SUBMIT is the server's §4.6 allowlist —
    /// a separate policy gate from the codec's tag registry (design §5, two gates).</summary>
    public sealed record IntentMsg(ulong LastVersion, PlayerIntent Intent) : NetMessage;

    /// <summary>Idle ack of the last applied StateVersion (progress signal for B2 timers).</summary>
    public sealed record AckMsg(ulong Version) : NetMessage;

    /// <summary>Liveness; resets the disconnect timer. Empty body.</summary>
    public sealed record HeartbeatMsg : NetMessage;

    /// <summary>Rejoin a live match (token semantics are B1/B2 policy; A5 only carries the shape).</summary>
    public sealed record ReconnectMsg(PlayerId PlayerId, string Token, ulong LastVersion) : NetMessage;

    /// <summary>Client-detected integrity failure → request a fresh snapshot (design §9 OQ-A).</summary>
    public sealed record ResyncMsg(ulong LastVersion, ResyncReason Reason) : NetMessage;

    public enum ResyncReason { HashMismatch, DecodeError }

    // ── Server → Client ──────────────────────────────────────────────────────────

    /// <summary>
    /// Full projected snapshot + per-message canonical hash (ADR-0002/0003). Constructible only
    /// via <see cref="Of"/> (which computes the hash itself — a server that forgets to hash cannot
    /// be written, design §1/§6) or by the decoder (hash from the wire). The hash is a per-recipient
    /// MIRROR-INTEGRITY check: the client re-hashes its decoded mirror via
    /// <see cref="GameStateHasher"/> and sends <see cref="ResyncMsg"/> on mismatch — never a
    /// cross-client desync comparator (state-sync §3).
    /// </summary>
    public sealed record SnapshotMsg : NetMessage
    {
        public ulong StateVersion { get; }
        /// <summary>SHA-256 lowercase hex of the CANONICAL serialized form (never the JSON bytes).</summary>
        public string Hash { get; }
        /// <summary>The projected <see cref="GameState"/> for this recipient (projection is the
        /// server's job — B1 composes <c>GameStateProjector.Project</c> → this).</summary>
        public GameState State { get; }

        internal SnapshotMsg(ulong stateVersion, string hash, GameState state)
        {
            StateVersion = stateVersion;
            Hash = hash;
            State = state;
        }

        /// <summary>The one public construction path: hash computed internally, version read off the state.</summary>
        public static SnapshotMsg Of(GameState projected)
            => new SnapshotMsg(projected.StateVersion, GameStateHasher.Hash(projected), projected);

        /// <summary>Recompute the canonical hash of <see cref="State"/> and compare to <see cref="Hash"/>
        /// (the client-side mirror-integrity check).</summary>
        public bool VerifyIntegrity() => string.Equals(GameStateHasher.Hash(State), Hash, System.StringComparison.Ordinal);
    }

    /// <summary>The ordered projected D7 batch for one applied command (netcode §3.3).</summary>
    public sealed record EventsMsg(ulong StateVersion, IReadOnlyList<GameEvent> Events) : NetMessage;

    /// <summary>Intent refused; carries the envelope seq of the offending <see cref="IntentMsg"/>.</summary>
    public sealed record RejectMsg(RejectReason Reason, ulong IntentSeq) : NetMessage;

    /// <summary>Turn clock (deadline in server ticks; tick semantics are B2's).</summary>
    public sealed record TurnTimerMsg(PlayerId ActivePlayer, ulong DeadlineTick) : NetMessage;

    /// <summary>Match start: seat assignment, contract version pin, opponent metadata, initial
    /// projected snapshot. <b>The seed is server-side only and is NEVER sent</b> (ADR-0004 / decision 19).
    /// <see cref="ReconnectToken"/> is the B1 additive field (b1-match-server-design.md §6-bis, RC F2):
    /// the opaque credential the client must present in <see cref="ReconnectMsg"/>. Empty on decode
    /// when an older encoder omitted it (A5 §8 forward-compat).</summary>
    public sealed record MatchStartMsg(
        PlayerId PlayerId,
        int Seat,
        string EngineContractVersion,
        OpponentInfo Opponent,
        SnapshotMsg Snapshot,
        string ReconnectToken = "") : NetMessage;

    public readonly struct OpponentInfo
    {
        public readonly PlayerId PlayerId;
        public readonly string DisplayName;
        public OpponentInfo(PlayerId playerId, string displayName)
        {
            PlayerId = playerId;
            DisplayName = displayName;
        }
    }

    /// <summary>Terminal result + why the match ended.</summary>
    public sealed record MatchEndMsg(MatchOutcome Outcome, MatchEndReason Reason) : NetMessage;

    public enum MatchEndReason { Win, Concede, Timeout, Disconnect }

    // ── Decode surface ───────────────────────────────────────────────────────────

    public enum ProtocolError
    {
        None,
        /// <summary>Not valid JSON / not the wire subset (floats, dup keys, trailing data, bad UTF-8…).</summary>
        Malformed,
        /// <summary>Envelope <c>v</c> is not <see cref="ProtocolCodec.Version"/>.</summary>
        VersionMismatch,
        /// <summary>Envelope <c>type</c> is not in the message registry.</summary>
        UnknownType,
        /// <summary>Intent <c>$type</c> tag is not in the [ReplayTag] registry → server maps to
        /// <see cref="RejectReason.UnknownIntent"/>.</summary>
        UnknownIntentTag,
        /// <summary>An enum name failed strict parse, or a required field is missing/mis-typed.</summary>
        BadField,
        /// <summary>Payload exceeds <see cref="ProtocolCodec.MaxMessageBytes"/>.</summary>
        TooLarge,
    }

    /// <summary>
    /// Result of <see cref="ProtocolCodec.Decode"/>. Decode NEVER throws on wire input — the server
    /// feeds attacker-controlled bytes straight into this (state-sync §6.2 T-class); every failure
    /// is a value the caller maps to reject/disconnect policy.
    /// </summary>
    public readonly struct DecodeResult
    {
        public readonly bool Ok;
        public readonly NetMessage? Message;
        /// <summary>Envelope matchId — valid whenever the envelope itself parsed (even if the body failed).</summary>
        public readonly MatchId MatchId;
        /// <summary>Envelope seq — valid whenever the envelope itself parsed.</summary>
        public readonly ulong Seq;
        public readonly ProtocolError Error;
        public readonly string? Detail;

        private DecodeResult(bool ok, NetMessage? message, MatchId matchId, ulong seq, ProtocolError error, string? detail)
        {
            Ok = ok;
            Message = message;
            MatchId = matchId;
            Seq = seq;
            Error = error;
            Detail = detail;
        }

        internal static DecodeResult Success(NetMessage message, MatchId matchId, ulong seq)
            => new DecodeResult(true, message, matchId, seq, ProtocolError.None, null);

        internal static DecodeResult Fail(ProtocolError error, string detail, MatchId matchId = default, ulong seq = 0)
            => new DecodeResult(false, null, matchId, seq, error, detail);
    }
}
