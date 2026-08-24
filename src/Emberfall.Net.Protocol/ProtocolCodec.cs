using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// Envelope v=1 encode/decode (dev-docs/contracts/wire-protocol.md:5). One JSON object both
    /// directions: <c>{"v":1,"matchId":str,"seq":u64,"type":str,"body":{...}}</c>.
    /// Encode may throw on programmer error (unregistered message type); <see cref="Decode"/>
    /// NEVER throws on wire input — every failure is a <see cref="DecodeResult"/>.
    /// </summary>
    public static class ProtocolCodec
    {
        /// <summary>Protocol encoding version (this codec defines v=1; the M3 binary codec arrives
        /// as v=2 behind the same contract — design §8).</summary>
        public const int Version = 1;

        /// <summary>Transport-level sanity bound (state-sync §6.2 T-class hardening).</summary>
        public const int MaxMessageBytes = 1024 * 1024;

        /// <summary>
        /// Envelope <c>matchId</c> for frames that precede the match — today only <see cref="HelloMsg"/>,
        /// which is sent before pairing, so no match id exists yet. A placeholder is required because
        /// <c>MatchId</c> is a struct whose default <c>Value</c> is null, and a null envelope matchId
        /// fails to decode: the hello would be silently refused and the client would hang waiting for
        /// a match start that never comes.
        /// </summary>
        public static readonly MatchId PreMatch = new MatchId("pending");

        // Wire type discriminators (design §5).
        public const string TypeHello = "hello";
        public const string TypeSubmitDeck = "submit_deck";
        public const string TypeIntent = "intent";
        public const string TypeAck = "ack";
        public const string TypeHeartbeat = "heartbeat";
        public const string TypeReconnect = "reconnect";
        public const string TypeResync = "resync";
        public const string TypeSnapshot = "snapshot";
        public const string TypeEvents = "events";
        public const string TypeReject = "reject";
        public const string TypeTurnTimer = "turn_timer";
        public const string TypeMatchStart = "match_start";
        public const string TypeMatchEnd = "match_end";

        // ── Encode ───────────────────────────────────────────────────────────────

        public static byte[] Encode(NetMessage msg, MatchId matchId, ulong seq)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("v"); w.I32(Version);
            w.Name("matchId"); w.Str(matchId.Value);   // string — as-built MatchId (netcode §3.1 erratum, design §4)
            w.Name("seq"); w.U64(seq);
            w.Name("type"); w.Str(TypeOf(msg));
            w.Name("body");
            WriteBody(w, msg);
            w.EndObject();
            return w.ToUtf8();
        }

        private static string TypeOf(NetMessage msg) => msg switch
        {
            HelloMsg _ => TypeHello,
            SubmitDeckMsg _ => TypeSubmitDeck,
            IntentMsg _ => TypeIntent,
            AckMsg _ => TypeAck,
            HeartbeatMsg _ => TypeHeartbeat,
            ReconnectMsg _ => TypeReconnect,
            ResyncMsg _ => TypeResync,
            SnapshotMsg _ => TypeSnapshot,
            EventsMsg _ => TypeEvents,
            RejectMsg _ => TypeReject,
            TurnTimerMsg _ => TypeTurnTimer,
            MatchStartMsg _ => TypeMatchStart,
            MatchEndMsg _ => TypeMatchEnd,
            _ => throw new InvalidOperationException($"Message type '{msg.GetType().Name}' is not in the wire registry."),
        };

        private static void WriteBody(JsonWriter w, NetMessage msg)
        {
            switch (msg)
            {
                case HelloMsg m:
                    w.BeginObject();
                    w.Name("deviceToken"); w.Str(m.DeviceToken);
                    w.Name("clientContractVersion"); w.Str(m.ClientContractVersion);
                    w.Name("platform"); w.Str(m.Platform);
                    w.Name("buildId"); w.Str(m.BuildId);
                    w.Name("displayName"); w.Str(m.DisplayName);
                    w.Name("authToken"); w.Str(m.AuthToken);
                    w.EndObject();
                    break;
                case SubmitDeckMsg m:
                    w.BeginObject();
                    w.Name("characterId"); w.Str(m.CharacterId);
                    w.Name("primaryClass"); w.Str(m.PrimaryClass.ToString());
                    w.Name("secondaryClass"); w.Str(m.SecondaryClass.ToString());
                    w.Name("cards");
                    w.BeginArray();
                    foreach (var line in m.Cards)
                    {
                        w.BeginObject();
                        w.Name("cardId"); w.I32(line.CardId);
                        w.Name("count"); w.I32(line.Count);
                        w.EndObject();
                    }
                    w.EndArray();
                    w.EndObject();
                    break;
                case IntentMsg m:
                    w.BeginObject();
                    w.Name("lastVersion"); w.U64(m.LastVersion);
                    w.Name("intent"); IntentJsonCodec.Write(w, m.Intent);
                    w.EndObject();
                    break;
                case AckMsg m:
                    w.BeginObject();
                    w.Name("version"); w.U64(m.Version);
                    w.EndObject();
                    break;
                case HeartbeatMsg _:
                    w.BeginObject();
                    w.EndObject();
                    break;
                case ReconnectMsg m:
                    w.BeginObject();
                    w.Name("playerId"); w.Str(m.PlayerId.Value);
                    w.Name("token"); w.Str(m.Token);
                    w.Name("lastVersion"); w.U64(m.LastVersion);
                    w.EndObject();
                    break;
                case ResyncMsg m:
                    w.BeginObject();
                    w.Name("lastVersion"); w.U64(m.LastVersion);
                    w.Name("reason"); w.Str(m.Reason == ResyncReason.HashMismatch ? "hash_mismatch" : "decode_error");
                    w.EndObject();
                    break;
                case SnapshotMsg m:
                    WriteSnapshotBody(w, m);
                    break;
                case EventsMsg m:
                    w.BeginObject();
                    w.Name("stateVersion"); w.U64(m.StateVersion);
                    w.Name("events");
                    w.BeginArray();
                    foreach (var e in m.Events) WriteEvent(w, e);
                    w.EndArray();
                    w.EndObject();
                    break;
                case RejectMsg m:
                    w.BeginObject();
                    w.Name("reason"); w.Str(m.Reason.ToString());
                    w.Name("seq"); w.U64(m.IntentSeq);
                    w.EndObject();
                    break;
                case TurnTimerMsg m:
                    w.BeginObject();
                    w.Name("activePlayer"); w.Str(m.ActivePlayer.Value);
                    w.Name("deadlineTick"); w.U64(m.DeadlineTick);
                    w.EndObject();
                    break;
                case MatchStartMsg m:
                    w.BeginObject();
                    w.Name("playerId"); w.Str(m.PlayerId.Value);
                    w.Name("seat"); w.I32(m.Seat);
                    w.Name("wireContractVersion"); w.Str(m.WireContractVersion);
                    w.Name("opponent");
                    w.BeginObject();
                    w.Name("playerId"); w.Str(m.Opponent.PlayerId.Value);
                    w.Name("displayName"); w.Str(m.Opponent.DisplayName);
                    w.EndObject();
                    w.Name("snapshot");
                    WriteSnapshotBody(w, m.Snapshot);
                    w.Name("reconnectToken"); w.Str(m.ReconnectToken);
                    w.EndObject();
                    break;
                case MatchEndMsg m:
                    w.BeginObject();
                    w.Name("outcome"); w.Str(m.Outcome.ToString());
                    w.Name("reason"); w.Str(ReasonToWire(m.Reason));
                    w.EndObject();
                    break;
                default:
                    throw new InvalidOperationException($"No body writer for '{msg.GetType().Name}'.");
            }
        }

        private static void WriteSnapshotBody(JsonWriter w, SnapshotMsg m)
        {
            w.BeginObject();
            w.Name("stateVersion"); w.U64(m.StateVersion);
            w.Name("hash"); w.Str(m.Hash);
            w.Name("state"); SnapshotJsonCodec.Write(w, m.State);
            w.EndObject();
        }

        /// <summary>Design §9 OQ-E: omit src/tgt/amt when null and flags when None; the reader
        /// tolerates explicit null too.</summary>
        private static void WriteEvent(JsonWriter w, in GameEvent e)
        {
            w.BeginObject();
            w.Name("seq"); w.U64(e.Sequence);
            w.Name("kind"); w.Str(e.Kind.ToString());
            if (e.CueOverride.HasValue) { w.Name("cue"); w.Str(e.CueOverride.Value.ToString()); }
            if (e.Source.HasValue) { w.Name("src"); w.Str(WirePrimitives.InstanceToWire(e.Source.Value)); }
            if (e.Target.HasValue) { w.Name("tgt"); w.Str(WirePrimitives.InstanceToWire(e.Target.Value)); }
            if (e.Amount.HasValue) { w.Name("amt"); w.I32(e.Amount.Value); }
            if (e.Flags != EventFlags.None) { w.Name("flags"); w.Str(e.Flags.ToString()); }
            w.EndObject();
        }

        private static string ReasonToWire(MatchEndReason r) => r switch
        {
            MatchEndReason.Win => "win",
            MatchEndReason.Concede => "concede",
            MatchEndReason.Timeout => "timeout",
            MatchEndReason.Disconnect => "disconnect",
            _ => "win",
        };

        // ── Decode ───────────────────────────────────────────────────────────────

        public static DecodeResult Decode(ReadOnlySpan<byte> utf8)
        {
            if (utf8.Length > MaxMessageBytes)
                return DecodeResult.Fail(ProtocolError.TooLarge, $"payload {utf8.Length} bytes exceeds {MaxMessageBytes}");

            if (!JsonParser.TryParse(utf8, out var root, out string parseError))
                return DecodeResult.Fail(ProtocolError.Malformed, parseError);
            if (root.Kind != JKind.Object)
                return DecodeResult.Fail(ProtocolError.Malformed, "envelope is not an object");

            if (!root.TryGet("v", out var vVal) || !vVal.TryI32(out int v))
                return DecodeResult.Fail(ProtocolError.Malformed, "missing envelope 'v'");
            if (v != Version)
                return DecodeResult.Fail(ProtocolError.VersionMismatch, $"protocol v={v}, expected v={Version}");

            if (!root.TryGet("matchId", out var midVal) || !midVal.TryStr(out string matchIdStr))
                return DecodeResult.Fail(ProtocolError.Malformed, "missing envelope 'matchId'");
            var matchId = new MatchId(matchIdStr);

            if (!root.TryGet("seq", out var seqVal) || !seqVal.TryU64(out ulong seq))
                return DecodeResult.Fail(ProtocolError.Malformed, "missing envelope 'seq'", matchId);

            if (!root.TryGet("type", out var typeVal) || !typeVal.TryStr(out string type))
                return DecodeResult.Fail(ProtocolError.Malformed, "missing envelope 'type'", matchId, seq);

            if (!root.TryGet("body", out var body) || body.Kind != JKind.Object)
                return DecodeResult.Fail(ProtocolError.Malformed, "missing envelope 'body'", matchId, seq);

            switch (type)
            {
                case TypeHello: return ReadHello(body, matchId, seq);
                case TypeSubmitDeck: return ReadSubmitDeck(body, matchId, seq);
                case TypeIntent: return ReadIntent(body, matchId, seq);
                case TypeAck:
                    return ReadU64(body, "version", matchId, seq, out ulong ackV)
                        ? DecodeResult.Success(new AckMsg(ackV), matchId, seq)
                        : DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'version'", matchId, seq);
                case TypeHeartbeat: return DecodeResult.Success(new HeartbeatMsg(), matchId, seq);
                case TypeReconnect: return ReadReconnect(body, matchId, seq);
                case TypeResync: return ReadResync(body, matchId, seq);
                case TypeSnapshot: return ReadSnapshot(body, matchId, seq);
                case TypeEvents: return ReadEvents(body, matchId, seq);
                case TypeReject: return ReadReject(body, matchId, seq);
                case TypeTurnTimer: return ReadTurnTimer(body, matchId, seq);
                case TypeMatchStart: return ReadMatchStart(body, matchId, seq);
                case TypeMatchEnd: return ReadMatchEnd(body, matchId, seq);
                default:
                    return DecodeResult.Fail(ProtocolError.UnknownType, $"unknown message type '{type}'", matchId, seq);
            }
        }

        private static bool ReadU64(JVal body, string name, MatchId matchId, ulong seq, out ulong v)
        {
            v = 0;
            return body.TryGet(name, out var val) && val.TryU64(out v);
        }

        private static DecodeResult ReadIntent(JVal body, MatchId matchId, ulong seq)
        {
            if (!ReadU64(body, "lastVersion", matchId, seq, out ulong lastVersion))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'lastVersion'", matchId, seq);
            if (!body.TryGet("intent", out var intentVal))
                return DecodeResult.Fail(ProtocolError.BadField, "missing 'intent'", matchId, seq);
            if (!IntentJsonCodec.TryRead(intentVal, out var intent, out var err, out string detail))
                return DecodeResult.Fail(err, detail, matchId, seq);
            return DecodeResult.Success(new IntentMsg(lastVersion, intent), matchId, seq);
        }

        /// <summary>
        /// The device token is REQUIRED and must be non-empty — an identity-less hello is the exact
        /// hole C-G1-8 closes, so it fails here rather than producing an unattributable match.
        /// Provenance fields are optional (absent ⇒ empty): they feed the funnel, not identity.
        ///
        /// <para><b><c>displayName</c> is decoded, NOT validated, and that is deliberate</b>
        /// (VULN-002/EF-073). This is the codec, whose gate is the tag registry; whether a decoded
        /// value is <i>acceptable</i> is the server's policy gate — the same two-gate split the
        /// intent allowlist uses (design §5). Refusing a bad name here would also refuse it for the
        /// client, which decodes with this same method and has no business enforcing the server's
        /// policy. <c>MatchGate</c> runs <c>PlayerName.Check</c> on it; an absent name is empty,
        /// which is the honest encoding of "this player never set one" and never a refused connect.
        /// The 16 KiB frame ceiling is what bounds the string at this layer.</para>
        /// </summary>
        private static DecodeResult ReadHello(JVal body, MatchId matchId, ulong seq)
        {
            if (!body.TryGet("deviceToken", out var dtVal) || !dtVal.TryStr(out string deviceToken)
                || deviceToken.Length == 0)
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'deviceToken'", matchId, seq);

            // authToken is optional — absent means guest; the server accepts both (EF-074b/NOR-228).
            return DecodeResult.Success(
                new HelloMsg(deviceToken, OptStr(body, "clientContractVersion"),
                             OptStr(body, "platform"), OptStr(body, "buildId"),
                             OptStr(body, "displayName"), OptStr(body, "authToken")),
                matchId, seq);
        }

        private static string OptStr(JVal body, string name)
            => body.TryGet(name, out var v) && v.TryStr(out string s) ? s : "";

        /// <summary>
        /// EF-071 — the client-supplied decklist. Decode only enforces the wire SHAPE (well-formed
        /// object, present fields, strict-enum classes, integer counts); it never judges legality.
        /// Deck LEGALITY — size, copy caps, class membership, and every id resolving in the card DB —
        /// is the server's business (<c>DeckSubmission.Validate</c> → <c>QueueEligibility.Check</c>),
        /// because "the codec vetted it" is exactly the trust a client must never be handed (VULN-001).
        /// An empty or over-large card array is a legal DECODE and a rejected DECK; the frame itself is
        /// already bounded by <see cref="MaxMessageBytes"/> and the gate's per-frame cap.
        /// </summary>
        private static DecodeResult ReadSubmitDeck(JVal body, MatchId matchId, ulong seq)
        {
            if (!body.TryGet("characterId", out var cidVal) || !cidVal.TryStr(out string characterId))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'characterId'", matchId, seq);
            if (!body.TryGet("primaryClass", out var pcVal) || !pcVal.TryStr(out string pcs)
                || !WirePrimitives.TryEnum(pcs, out CardClass primary))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'primaryClass'", matchId, seq);
            if (!body.TryGet("secondaryClass", out var scVal) || !scVal.TryStr(out string scs)
                || !WirePrimitives.TryEnum(scs, out CardClass secondary))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'secondaryClass'", matchId, seq);
            if (!body.TryGet("cards", out var cardsVal) || cardsVal.Kind != JKind.Array)
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'cards'", matchId, seq);

            var cards = new List<DeckCardLine>(cardsVal.Items!.Count);
            foreach (var item in cardsVal.Items)
            {
                if (item.Kind != JKind.Object
                    || !item.TryGet("cardId", out var idVal) || !idVal.TryI32(out int cardId)
                    || !item.TryGet("count", out var cntVal) || !cntVal.TryI32(out int count))
                    return DecodeResult.Fail(ProtocolError.BadField, "bad deck 'cards' entry", matchId, seq);
                cards.Add(new DeckCardLine(cardId, count));
            }

            return DecodeResult.Success(new SubmitDeckMsg(characterId, primary, secondary, cards), matchId, seq);
        }

        private static DecodeResult ReadReconnect(JVal body, MatchId matchId, ulong seq)
        {
            if (!body.TryGet("playerId", out var pVal) || !pVal.TryStr(out string player))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'playerId'", matchId, seq);
            if (!body.TryGet("token", out var tVal) || !tVal.TryStr(out string token))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'token'", matchId, seq);
            if (!ReadU64(body, "lastVersion", matchId, seq, out ulong lastVersion))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'lastVersion'", matchId, seq);
            return DecodeResult.Success(new ReconnectMsg(new PlayerId(player), token, lastVersion), matchId, seq);
        }

        private static DecodeResult ReadResync(JVal body, MatchId matchId, ulong seq)
        {
            if (!ReadU64(body, "lastVersion", matchId, seq, out ulong lastVersion))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'lastVersion'", matchId, seq);
            if (!body.TryGet("reason", out var rVal) || !rVal.TryStr(out string reasonStr))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'reason'", matchId, seq);
            ResyncReason reason;
            switch (reasonStr)
            {
                case "hash_mismatch": reason = ResyncReason.HashMismatch; break;
                case "decode_error": reason = ResyncReason.DecodeError; break;
                default: return DecodeResult.Fail(ProtocolError.BadField, $"unknown resync reason '{reasonStr}'", matchId, seq);
            }
            return DecodeResult.Success(new ResyncMsg(lastVersion, reason), matchId, seq);
        }

        private static bool TryReadSnapshotBody(JVal body, out SnapshotMsg msg, out string detail)
        {
            msg = null!;
            detail = "";
            if (body.Kind != JKind.Object) { detail = "snapshot body is not an object"; return false; }
            if (!body.TryGet("stateVersion", out var svVal) || !svVal.TryU64(out ulong stateVersion))
            { detail = "missing/bad 'stateVersion'"; return false; }
            if (!body.TryGet("hash", out var hVal) || !hVal.TryStr(out string hash))
            { detail = "missing/bad 'hash'"; return false; }
            if (!body.TryGet("state", out var stVal) || !SnapshotJsonCodec.TryRead(stVal, out var state, out detail))
                return false;
            msg = new SnapshotMsg(stateVersion, hash, state);
            return true;
        }

        private static DecodeResult ReadSnapshot(JVal body, MatchId matchId, ulong seq)
            => TryReadSnapshotBody(body, out var msg, out string detail)
                ? DecodeResult.Success(msg, matchId, seq)
                : DecodeResult.Fail(ProtocolError.BadField, detail, matchId, seq);

        private static DecodeResult ReadEvents(JVal body, MatchId matchId, ulong seq)
        {
            if (!ReadU64(body, "stateVersion", matchId, seq, out ulong stateVersion))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'stateVersion'", matchId, seq);
            if (!body.TryGet("events", out var evVal) || evVal.Kind != JKind.Array)
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'events'", matchId, seq);

            var events = new List<GameEvent>(evVal.Items!.Count);
            foreach (var item in evVal.Items)
            {
                if (!TryReadEvent(item, out var e, out ProtocolError error, out string detail))
                    return DecodeResult.Fail(error, detail, matchId, seq);
                events.Add(e);
            }
            return DecodeResult.Success(new EventsMsg(stateVersion, events), matchId, seq);
        }

        private static bool TryReadEvent(JVal obj, out GameEvent e, out ProtocolError error, out string detail)
        {
            e = default;
            error = ProtocolError.BadField;
            detail = "";
            if (obj.Kind != JKind.Object) { detail = "event is not an object"; return false; }
            if (!obj.TryGet("seq", out var sVal) || !sVal.TryU64(out ulong evSeq))
            { detail = "missing/bad event 'seq'"; return false; }
            if (!obj.TryGet("kind", out var kVal) || !kVal.TryStr(out string kindStr))
            { detail = "missing/bad event 'kind'"; return false; }
            if (!WirePrimitives.TryEnum(kindStr, out GameEventKind kind))
            {
                error = ProtocolError.UnknownEventKind;
                detail = $"unknown event kind '{kindStr}'";
                return false;
            }

            InstanceId? src = null, tgt = null;
            int? amt = null;
            EventFlags flags = EventFlags.None;
            PresentationCue? cue = null;

            if (obj.TryGet("cue", out var cueVal))
            {
                if (!cueVal.TryStr(out string cueStr))
                { detail = "bad event 'cue'"; return false; }
                if (WirePrimitives.TryEnum(cueStr, out PresentationCue parsedCue))
                    cue = parsedCue;
            }

            if (obj.TryGet("src", out var srcVal) && !srcVal.IsNull)
            {
                if (!srcVal.TryStr(out string ss) || !WirePrimitives.TryInstance(ss, out var sid))
                { detail = "bad event 'src'"; return false; }
                src = sid;
            }
            if (obj.TryGet("tgt", out var tgtVal) && !tgtVal.IsNull)
            {
                if (!tgtVal.TryStr(out string ts) || !WirePrimitives.TryInstance(ts, out var tid))
                { detail = "bad event 'tgt'"; return false; }
                tgt = tid;
            }
            if (obj.TryGet("amt", out var amtVal) && !amtVal.IsNull)
            {
                if (!amtVal.TryI32(out int a)) { detail = "bad event 'amt'"; return false; }
                amt = a;
            }
            if (obj.TryGet("flags", out var fVal) && !fVal.IsNull)
            {
                if (!fVal.TryStr(out string fs) || !WirePrimitives.TryEnum(fs, out flags))
                { detail = "bad event 'flags'"; return false; }
            }

            try
            {
                GameEventPolicy.Validate(kind, amt, flags, cue);
            }
            catch (System.ArgumentException ex)
            {
                detail = ex.Message;
                return false;
            }

            e = new GameEvent(kind, evSeq, src, tgt, amt, flags, cue);
            return true;
        }

        private static DecodeResult ReadReject(JVal body, MatchId matchId, ulong seq)
        {
            if (!body.TryGet("reason", out var rVal) || !rVal.TryStr(out string rs)
                || !WirePrimitives.TryEnum(rs, out RejectReason reason))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'reason'", matchId, seq);
            if (!ReadU64(body, "seq", matchId, seq, out ulong intentSeq))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad reject 'seq'", matchId, seq);
            return DecodeResult.Success(new RejectMsg(reason, intentSeq), matchId, seq);
        }

        private static DecodeResult ReadTurnTimer(JVal body, MatchId matchId, ulong seq)
        {
            if (!body.TryGet("activePlayer", out var pVal) || !pVal.TryStr(out string player))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'activePlayer'", matchId, seq);
            if (!ReadU64(body, "deadlineTick", matchId, seq, out ulong deadline))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'deadlineTick'", matchId, seq);
            return DecodeResult.Success(new TurnTimerMsg(new PlayerId(player), deadline), matchId, seq);
        }

        private static DecodeResult ReadMatchStart(JVal body, MatchId matchId, ulong seq)
        {
            if (!body.TryGet("playerId", out var pVal) || !pVal.TryStr(out string player))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'playerId'", matchId, seq);
            if (!body.TryGet("seat", out var seatVal) || !seatVal.TryI32(out int seat) || (seat != 0 && seat != 1))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'seat'", matchId, seq);
            if (!body.TryGet("wireContractVersion", out var wcvVal) || !wcvVal.TryStr(out string wcv))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'wireContractVersion'", matchId, seq);
            if (!body.TryGet("opponent", out var oppVal) || oppVal.Kind != JKind.Object
                || !oppVal.TryGet("playerId", out var opVal) || !opVal.TryStr(out string oppId)
                || !oppVal.TryGet("displayName", out var dnVal) || !dnVal.TryStr(out string oppName))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'opponent'", matchId, seq);
            if (!body.TryGet("snapshot", out var snapVal))
                return DecodeResult.Fail(ProtocolError.BadField, "missing 'snapshot'", matchId, seq);
            if (!TryReadSnapshotBody(snapVal, out var snapshot, out string detail))
                return DecodeResult.Fail(ProtocolError.BadField, $"snapshot: {detail}", matchId, seq);

            // B1 additive field (design §6-bis): absent (pre-B1 encoder) → empty, never a decode failure.
            string token = "";
            if (body.TryGet("reconnectToken", out var tokVal) && !tokVal.TryStr(out token))
                return DecodeResult.Fail(ProtocolError.BadField, "bad 'reconnectToken'", matchId, seq);

            var msg = new MatchStartMsg(new PlayerId(player), seat, wcv,
                new OpponentInfo(new PlayerId(oppId), oppName), snapshot, token);
            return DecodeResult.Success(msg, matchId, seq);
        }

        private static DecodeResult ReadMatchEnd(JVal body, MatchId matchId, ulong seq)
        {
            if (!body.TryGet("outcome", out var oVal) || !oVal.TryStr(out string os)
                || !WirePrimitives.TryEnum(os, out MatchOutcome outcome))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'outcome'", matchId, seq);
            if (!body.TryGet("reason", out var rVal) || !rVal.TryStr(out string rs))
                return DecodeResult.Fail(ProtocolError.BadField, "missing/bad 'reason'", matchId, seq);
            MatchEndReason reason;
            switch (rs)
            {
                case "win": reason = MatchEndReason.Win; break;
                case "concede": reason = MatchEndReason.Concede; break;
                case "timeout": reason = MatchEndReason.Timeout; break;
                case "disconnect": reason = MatchEndReason.Disconnect; break;
                default: return DecodeResult.Fail(ProtocolError.BadField, $"unknown end reason '{rs}'", matchId, seq);
            }
            return DecodeResult.Success(new MatchEndMsg(outcome, reason), matchId, seq);
        }
    }
}
