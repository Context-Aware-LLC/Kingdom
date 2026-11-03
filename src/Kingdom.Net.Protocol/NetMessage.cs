using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Core.Serialization;

namespace Kingdom.Net.Protocol
{
    /// <summary>
    /// The closed wire vocabulary (dev-docs/contracts/wire-protocol.md:7; hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/netcode-architecture.md v2
    /// §3.2/§3.3). One sealed type per message; the envelope discriminator strings live in
    /// <see cref="ProtocolCodec"/>. Bodies are canonical, float-free, integer-only.
    /// </summary>
    public abstract record NetMessage;

    // ── Client → Server ──────────────────────────────────────────────────────────

    /// <summary>
    /// The connect handshake — FIRST frame on a fresh socket, before pairing (C-G1-8).
    /// <para><see cref="DeviceToken"/> is the device identity from telemetry-spec §0 / accounts
    /// posture decision 6a: client-generated once, persisted on device, stable across restarts.
    /// It is the reason an emitted telemetry row can be attributed at all.</para>
    /// <para><b>The token never leaves the server.</b> It is hashed into an account id on arrival
    /// and is never placed in <c>PlayerId</c>, which is broadcast to the opponent in
    /// <c>OpponentInfo</c> — putting identity there would hand every player's stable token to a
    /// stranger.</para>
    /// <para><see cref="ClientContractVersion"/> is the first client→server carrier of the
    /// contract version; the server previously never learned it (<c>WireContractVersion</c>
    /// travels only server→client on <see cref="MatchStartMsg"/>). Carried here, enforced
    /// separately.</para>
    /// <para><see cref="Platform"/>/<see cref="BuildId"/> are funnel provenance (telemetry-spec
    /// §4 <c>app_install</c>) and are optional — absent means empty, never a refused connect.</para>
    /// <para><see cref="DisplayName"/> is <b>peer-authored text</b> — see the comment on the field.</para>
    /// </summary>
    public sealed record HelloMsg(
        string DeviceToken,
        string ClientContractVersion = "",
        string Platform = "",
        string BuildId = "",
        // ── VULN-002 (security/known-vulnerabilities) — ARMED as of EF-073. This field is the arming
        //    point the LATENT entry named, and it is now live, so this comment's job has changed from
        //    "do not add this" to "here is what is holding it up".
        //
        //    WHAT THIS STRING IS: a value the sender's client chose, rendered on the OTHER player's
        //    machine. PlayerName.Check runs on the sender before a local save, but that is advice the
        //    sender can decline — by patching the client or editing profile.json in a text editor.
        //    Nothing about this field arriving means it was validated.
        //
        //    WHAT DEFENDS IT — all four are load-bearing; do not remove one because the others exist:
        //      1. MatchGate.HandleFreshAsync calls the SAME PlayerName.Check on arrival and drops the
        //         name to a server-assigned fallback if it fails. This is the fix; the rest is depth.
        //      2. MatchSession only ever puts an already-checked name in OpponentInfo — an unvalidated
        //         name echoed there would be the server laundering peer input into trusted state.
        //      3. PlayerName reserves the strings our own UI writes ("You", "Opponent", "SYSTEM", …),
        //         which no character-class rule can catch because every letter in them is legal.
        //      4. VersusOverlay renders it with richText OFF and overflowMode Ellipsis, so markup is
        //         inert and the plate CLIPS in pixels rather than trusting MaxLength's character count.
        //
        //    RETIREMENT TRIGGER: if a second renderer ever draws this string, it owns obligation 4
        //    for itself — a length bound is not a clip. Add it to VULN-002 §4 when you write it.
        string DisplayName = "",
        // ── EF-074b — carries the Supabase JWT the player received at sign-in.
        //    Empty is the tooling-only tokenless path, refused unless ServerOptions.AllowTokenlessMatches;
        //    the client never connects without one (Sam, 2026-10-18).
        //    The server verifies this token in EF-074c; this layer is transport only.
        string AuthToken = "") : NetMessage;

    /// <summary>One line of a submitted decklist on the wire: a card definition id and a copy count.
    /// The float-free integer-only shape of <c>Kingdom.Core.Collection.DeckEntry</c>, restated here
    /// because this assembly deliberately never references Collection (csproj: "never references
    /// Kingdom.Core"). The server rebuilds a <c>DeckList</c> from these and revalidates it.</summary>
    public readonly struct DeckCardLine
    {
        public readonly int CardId;
        public readonly int Count;
        public DeckCardLine(int cardId, int count) { CardId = cardId; Count = count; }
    }

    /// <summary>
    /// EF-071 — a client-supplied decklist, submitted at handshake so the match is dealt from the
    /// player's own deck instead of the fixed <c>PrebuiltDecks</c> pair. Sent as the SECOND fresh-socket
    /// frame (after <see cref="HelloMsg"/>) by a client that connected to <c>/ws?deck=1</c>; a plain
    /// <c>/ws</c> client submits nothing and is dealt the prebuilt fallback, so this is purely additive.
    ///
    /// <para><b>SECURITY — this carries client-authored card-identity strings, the exact shape
    /// VULN-001 (security/known-vulnerabilities, LATENT — trigger "Client-supplied deck lists") warns
    /// about.</b> Nothing here is trusted: the server rebuilds a <c>DeckList</c> + <c>Character</c> from
    /// these fields and re-runs <c>QueueEligibility.Check</c> / <c>DeckValidator.Validate</c> against its
    /// own <c>ICardDatabase</c>, which resolves every id through <c>TryGet</c> (never <c>Get</c>) so an
    /// unknown id is a rejection, never an exception and never a mutation (VULN-001 §4 rule 1). An
    /// illegal deck is refused at the handshake and no id in it ever reaches match state.</para>
    ///
    /// <para><see cref="PrimaryClass"/>/<see cref="SecondaryClass"/> are the deck's pairing, needed
    /// for the L4 class-membership check and for L5, which is now the pairing's own
    /// well-formedness; the match's hero (and hero power) is the PRIMARY class (dual-class ruling
    /// — one hero power, from the primary).</para>
    ///
    /// <para><b><c>CharacterId</c> was the first field until 2026-09-04</b> (ADR-0009). Its own
    /// doc said it was "not an identity the server trusts", and it was true: the server echoed it
    /// into a throwaway <c>Character</c> built only so the validator's signature lined up. The
    /// deck carries its pairing now, so the object being validated and the deck being played are
    /// one thing rather than two that happened to agree.</para>
    /// </summary>
    public sealed record SubmitDeckMsg(
        CardClass PrimaryClass,
        CardClass SecondaryClass,
        IReadOnlyList<DeckCardLine> Cards) : NetMessage;

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
        /// <summary>The King's commission as it stands for THIS recipient (#421), or null in a match
        /// that plays for no King, which is how the client knows to hide the card. Optional key
        /// <c>commission</c>. It is not game state: <see cref="Hash"/> covers <see cref="State"/>
        /// alone, so this rides outside the hash and never moves it.</summary>
        public CommissionProgress? Commission { get; }

        internal SnapshotMsg(ulong stateVersion, string hash, GameState state, CommissionProgress? commission = null)
        {
            StateVersion = stateVersion;
            Hash = hash;
            State = state;
            Commission = commission;
        }

        /// <summary>The one public construction path: hash computed internally, version read off the state.</summary>
        public static SnapshotMsg Of(GameState projected, CommissionProgress? commission = null)
            => new SnapshotMsg(projected.StateVersion, GameStateHasher.Hash(projected), projected, commission);

        /// <summary>Recompute the canonical hash of <see cref="State"/> and compare to <see cref="Hash"/>
        /// (the client-side mirror-integrity check).</summary>
        public bool VerifyIntegrity() => string.Equals(GameStateHasher.Hash(State), Hash, System.StringComparison.Ordinal);
    }

    /// <summary>The ordered projected semantic-event batch for one applied command (netcode §3.3).</summary>
    public sealed record EventsMsg(ulong StateVersion, IReadOnlyList<GameEvent> Events) : NetMessage;

    /// <summary>Intent refused; carries the envelope seq of the offending <see cref="IntentMsg"/>.</summary>
    public sealed record RejectMsg(RejectReason Reason, ulong IntentSeq) : NetMessage;

    /// <summary>Turn clock (deadline in server ticks; tick semantics are B2's).</summary>
    public sealed record TurnTimerMsg(PlayerId ActivePlayer, ulong DeadlineTick) : NetMessage;

    /// <summary>Match start: seat assignment, contract version pin, opponent metadata, initial
    /// projected snapshot. <b>The seed is server-side only and is NEVER sent</b> (ADR-0004 / decision 19).
    /// <see cref="ReconnectToken"/> is the B1 additive field (dev-docs/contracts/wire-protocol.md:41, RC F2):
    /// the opaque credential the client must present in <see cref="ReconnectMsg"/>. Empty on decode
    /// when an older encoder omitted it (A5 §8 forward-compat).</summary>
    public sealed record MatchStartMsg(
        PlayerId PlayerId,
        int Seat,
        string WireContractVersion,
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
    /// <param name="Findings">The Council's Findings the RECEIVING seat committed this match, as
    /// <see cref="AchievementTexts"/> ids, so each seat is sent its own message. Empty outside a
    /// Find Game match. Additive and optional on the wire: an older peer omits it and decodes as
    /// none, which is why it did not move <see cref="ProtocolContract.Version"/>.</param>
    /// <param name="Commission">The receiving seat's final commission score (#421), optional key
    /// <c>commission</c>. Null in a match that plays for no King, and when a commission that needs a
    /// win was not won, because then nothing scores.</param>
    public sealed record MatchEndMsg(
        MatchOutcome Outcome, MatchEndReason Reason, IReadOnlyList<string>? Findings = null,
        CommissionFinal? Commission = null) : NetMessage
    {
        public IReadOnlyList<string> FindingIds => Findings ?? Array.Empty<string>();
    }

    /// <summary>
    /// One seat's commission card mid-match (#421). Server-authored, per seat: the client words and
    /// draws it and never computes a score of its own.
    /// </summary>
    /// <param name="Id">A <see cref="CommissionIds"/> id.</param>
    /// <param name="Score">Null until the seat qualifies; the card shows how it is scored until then.</param>
    /// <param name="CountsIfYouWin">True while the commission needs a win and the match is live: the
    /// number is greyed, with "Counts if you win".</param>
    /// <param name="Best">The seat's best this reign when the match began, or null.</param>
    public sealed record CommissionProgress(string Id, int? Score, bool CountsIfYouWin, int? Best);

    /// <summary>One seat's final commission score (#421), for the match-end proclamation.</summary>
    /// <param name="NewBest">True when it beats the seat's best this reign. A tie is not new: the
    /// board gives a tie to whoever reached it first.</param>
    public sealed record CommissionFinal(string Id, int Score, bool NewBest);

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
        /// <summary>A semantic event kind is not in this client's closed engine contract.</summary>
        UnknownEventKind,
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
