using System;
using System.Collections.Generic;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// A frame on the <c>/social</c> socket (#409) — presence, and the challenge handshake that
    /// ends in a private match. One flat JSON object per frame, discriminated by <c>"t"</c>.
    ///
    /// <para><b>Nothing here ever touches a match.</b> These types are not <see cref="NetMessage"/>s
    /// and do not go through <see cref="ProtocolCodec"/>: they carry no match id, no sequence and no
    /// state hash, and nothing in them reaches <c>GameState</c>. A challenge becomes a match only
    /// when each player takes its <see cref="ChallengeReadyMsg.Ticket"/> to the ordinary <c>/ws</c>
    /// route.</para>
    ///
    /// <para><b>Another player is named by friend code and nothing else</b> — see
    /// <see cref="FriendCode"/>.</para>
    /// </summary>
    public abstract record SocialMsg;

    // ── client → server ─────────────────────────────────────────────────────────────────

    /// <summary>The mandatory first frame. There is no guest path: a socket with no verifiable
    /// token has no friends to hear about and is closed.</summary>
    public sealed record SocialHelloMsg(string AuthToken, string ClientContractVersion) : SocialMsg;

    /// <summary>Keeps an idle socket from being reaped. Answered with <see cref="SocialPongMsg"/>.</summary>
    public sealed record SocialPingMsg : SocialMsg;

    /// <summary>Challenge the friend holding <paramref name="Code"/>.</summary>
    public sealed record ChallengeSendMsg(string Code) : SocialMsg;

    public sealed record ChallengeAcceptMsg(string ChallengeId) : SocialMsg;

    /// <summary>Decline (the target) or withdraw (the sender) — one verb, because the server
    /// already knows which the caller is.</summary>
    public sealed record ChallengeCloseMsg(string ChallengeId) : SocialMsg;

    /// <summary>A direct message to the friend holding <paramref name="Code"/>. The server
    /// re-cleans <paramref name="Text"/> with <see cref="ChatText"/>; the client's copy is advice.</summary>
    public sealed record ChatSendMsg(string Code, string Text) : SocialMsg;

    // ── server → client ─────────────────────────────────────────────────────────────────

    public sealed record SocialPongMsg : SocialMsg;

    public enum PresenceState { Offline, Online, InMatch }

    public readonly struct FriendPresence
    {
        public readonly string Code;
        public readonly PresenceState State;

        public FriendPresence(string code, PresenceState state)
        {
            Code = code;
            State = state;
        }
    }

    /// <summary>
    /// Where the listed friends are. <see cref="Full"/> means "this is everyone, and a friend not
    /// listed is offline" — sent on connect and whenever the friends graph changes. Otherwise it
    /// is a change to the friends listed and says nothing about the rest.
    /// </summary>
    public sealed record PresenceMsg(bool Full, IReadOnlyList<FriendPresence> Friends) : SocialMsg;

    /// <summary>The friends graph around this player changed; re-read <c>GET /friends</c>. Carries
    /// nothing, deliberately — the route is the one place that decides what a player may see.</summary>
    public sealed record FriendsChangedMsg : SocialMsg;

    /// <summary>A friend is challenging this player. <paramref name="TtlMs"/> is how long the
    /// offer stands, counted from when the server sent this.</summary>
    public sealed record ChallengeIncomingMsg(string ChallengeId, string Code, int TtlMs) : SocialMsg;

    /// <summary>The sender's own confirmation that the offer went out.</summary>
    public sealed record ChallengeSentMsg(string ChallengeId, string Code, int TtlMs) : SocialMsg;

    /// <summary>
    /// The challenge to <paramref name="Code"/> was not made. One answer for every cause — not a
    /// friend, offline, in a match, already in another challenge — because the first of those is
    /// not the sender's to learn.
    /// </summary>
    public sealed record ChallengeRefusedMsg(string Code) : SocialMsg;

    /// <summary>
    /// Both sides agreed. Connect to <c>/ws?challenge=<paramref name="Ticket"/></c> exactly as for
    /// a queued match. The ticket is single-use and works only for the account it was issued to.
    /// </summary>
    public sealed record ChallengeReadyMsg(string ChallengeId, string Ticket, int TtlMs) : SocialMsg;

    public enum ChallengeCloseReason
    {
        /// <summary>The target said no.</summary>
        Declined,
        /// <summary>The sender withdrew it.</summary>
        Cancelled,
        /// <summary>Nobody answered in time, or one side never arrived at the match.</summary>
        Expired,
        /// <summary>The other player's social socket went away.</summary>
        Left,
        /// <summary>It no longer exists — the answer to acting on an id the server does not hold.</summary>
        Gone,
    }

    public sealed record ChallengeClosedMsg(string ChallengeId, ChallengeCloseReason Reason) : SocialMsg;

    /// <summary>
    /// A direct message, as the server cleaned it. <paramref name="Code"/> is always the OTHER
    /// player: the sender when <paramref name="Mine"/> is false, the recipient when it is the
    /// sender's own copy coming back — which is how the sender learns it was delivered, and sees
    /// the text exactly as the friend will.
    ///
    /// <para><b><paramref name="Text"/> is text another player typed</b> (VULN-004). Nothing stores
    /// it: a message to a friend who is not connected is not delivered and is gone.</para>
    /// </summary>
    public sealed record ChatMsg(string Code, string Text, bool Mine) : SocialMsg;

    /// <summary>The message to <paramref name="Code"/> was not delivered. One answer for every
    /// cause — not a friend, offline, too fast, nothing left after cleaning — for the same reason
    /// as <see cref="ChallengeRefusedMsg"/>.</summary>
    public sealed record ChatRefusedMsg(string Code) : SocialMsg;

    /// <summary>
    /// Encode and decode for <see cref="SocialMsg"/>. Hand-rolled over the same
    /// <see cref="JsonWriter"/>/<see cref="JsonParser"/> as the match codec, for the same reason:
    /// this assembly compiles inside Unity, where <c>System.Text.Json</c> does not exist.
    ///
    /// <para><b>Decode never throws on wire input</b> and does not validate meaning — a
    /// <see cref="ChallengeSendMsg"/> comes back holding whatever string the peer sent. Every
    /// string in a decoded message is untrusted until the receiver checks it.</para>
    /// </summary>
    public static class SocialCodec
    {
        /// <summary>Social frames are a few short strings. Anything larger is not one.</summary>
        public const int MaxMessageBytes = 4 * 1024;

        public static byte[] Encode(SocialMsg msg)
        {
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("t");
            switch (msg)
            {
                case SocialHelloMsg m:
                    w.Str("hello");
                    w.Name("authToken"); w.Str(m.AuthToken);
                    w.Name("clientContractVersion"); w.Str(m.ClientContractVersion);
                    break;
                case SocialPingMsg _: w.Str("ping"); break;
                case SocialPongMsg _: w.Str("pong"); break;
                case ChallengeSendMsg m:
                    w.Str("challenge_send");
                    w.Name("code"); w.Str(m.Code);
                    break;
                case ChallengeAcceptMsg m:
                    w.Str("challenge_accept");
                    w.Name("id"); w.Str(m.ChallengeId);
                    break;
                case ChallengeCloseMsg m:
                    w.Str("challenge_close");
                    w.Name("id"); w.Str(m.ChallengeId);
                    break;
                case PresenceMsg m:
                    w.Str("presence");
                    w.Name("full"); w.Bool(m.Full);
                    w.Name("friends");
                    w.BeginArray();
                    foreach (FriendPresence friend in m.Friends)
                    {
                        w.BeginObject();
                        w.Name("code"); w.Str(friend.Code);
                        w.Name("state"); w.Str(StateName(friend.State));
                        w.EndObject();
                    }
                    w.EndArray();
                    break;
                case FriendsChangedMsg _: w.Str("friends_changed"); break;
                case ChallengeIncomingMsg m:
                    w.Str("challenge_incoming");
                    w.Name("id"); w.Str(m.ChallengeId);
                    w.Name("code"); w.Str(m.Code);
                    w.Name("ttlMs"); w.I32(m.TtlMs);
                    break;
                case ChallengeSentMsg m:
                    w.Str("challenge_sent");
                    w.Name("id"); w.Str(m.ChallengeId);
                    w.Name("code"); w.Str(m.Code);
                    w.Name("ttlMs"); w.I32(m.TtlMs);
                    break;
                case ChallengeRefusedMsg m:
                    w.Str("challenge_refused");
                    w.Name("code"); w.Str(m.Code);
                    break;
                case ChallengeReadyMsg m:
                    w.Str("challenge_ready");
                    w.Name("id"); w.Str(m.ChallengeId);
                    w.Name("ticket"); w.Str(m.Ticket);
                    w.Name("ttlMs"); w.I32(m.TtlMs);
                    break;
                case ChallengeClosedMsg m:
                    w.Str("challenge_closed");
                    w.Name("id"); w.Str(m.ChallengeId);
                    w.Name("reason"); w.Str(ReasonName(m.Reason));
                    break;
                case ChatSendMsg m:
                    w.Str("chat_send");
                    w.Name("code"); w.Str(m.Code);
                    w.Name("text"); w.Str(m.Text);
                    break;
                case ChatMsg m:
                    w.Str("chat");
                    w.Name("code"); w.Str(m.Code);
                    w.Name("text"); w.Str(m.Text);
                    w.Name("mine"); w.Bool(m.Mine);
                    break;
                case ChatRefusedMsg m:
                    w.Str("chat_refused");
                    w.Name("code"); w.Str(m.Code);
                    break;
                default:
                    throw new ArgumentException("Unknown social message type " + msg.GetType().Name, nameof(msg));
            }
            w.EndObject();
            return w.ToUtf8();
        }

        /// <summary>False for anything that is not a well-formed social frame; <paramref name="msg"/>
        /// is then null.</summary>
        public static bool TryDecode(ReadOnlySpan<byte> utf8, out SocialMsg? msg)
        {
            msg = null;
            if (utf8.Length > MaxMessageBytes) return false;
            if (!JsonParser.TryParse(utf8, out JVal root, out _) || root.Kind != JKind.Object) return false;
            if (!Str(root, "t", out string type)) return false;

            switch (type)
            {
                case "hello":
                    if (!Str(root, "authToken", out string token) ||
                        !Str(root, "clientContractVersion", out string version)) return false;
                    msg = new SocialHelloMsg(token, version);
                    return true;
                case "ping": msg = new SocialPingMsg(); return true;
                case "pong": msg = new SocialPongMsg(); return true;
                case "friends_changed": msg = new FriendsChangedMsg(); return true;
                case "challenge_send":
                    if (!Str(root, "code", out string sendCode)) return false;
                    msg = new ChallengeSendMsg(sendCode);
                    return true;
                case "challenge_accept":
                    if (!Str(root, "id", out string acceptId)) return false;
                    msg = new ChallengeAcceptMsg(acceptId);
                    return true;
                case "challenge_close":
                    if (!Str(root, "id", out string closeId)) return false;
                    msg = new ChallengeCloseMsg(closeId);
                    return true;
                case "challenge_refused":
                    if (!Str(root, "code", out string refusedCode)) return false;
                    msg = new ChallengeRefusedMsg(refusedCode);
                    return true;
                case "challenge_incoming":
                case "challenge_sent":
                    if (!Str(root, "id", out string offerId) || !Str(root, "code", out string offerCode) ||
                        !Int(root, "ttlMs", out int offerTtl)) return false;
                    msg = type == "challenge_incoming"
                        ? new ChallengeIncomingMsg(offerId, offerCode, offerTtl)
                        : (SocialMsg)new ChallengeSentMsg(offerId, offerCode, offerTtl);
                    return true;
                case "challenge_ready":
                    if (!Str(root, "id", out string readyId) || !Str(root, "ticket", out string ticket) ||
                        !Int(root, "ttlMs", out int readyTtl)) return false;
                    msg = new ChallengeReadyMsg(readyId, ticket, readyTtl);
                    return true;
                case "challenge_closed":
                    if (!Str(root, "id", out string closedId) || !Str(root, "reason", out string reasonName) ||
                        !TryReason(reasonName, out ChallengeCloseReason reason)) return false;
                    msg = new ChallengeClosedMsg(closedId, reason);
                    return true;
                case "chat_send":
                    if (!Str(root, "code", out string toCode) || !Str(root, "text", out string sendText)) return false;
                    msg = new ChatSendMsg(toCode, sendText);
                    return true;
                case "chat":
                    if (!Str(root, "code", out string chatCode) || !Str(root, "text", out string chatText) ||
                        !root.TryGet("mine", out JVal mineVal) || !mineVal.TryBool(out bool mine)) return false;
                    msg = new ChatMsg(chatCode, chatText, mine);
                    return true;
                case "chat_refused":
                    if (!Str(root, "code", out string chatRefusedCode)) return false;
                    msg = new ChatRefusedMsg(chatRefusedCode);
                    return true;
                case "presence":
                    return TryPresence(root, out msg);
                default:
                    return false;
            }
        }

        private static bool TryPresence(JVal root, out SocialMsg? msg)
        {
            msg = null;
            if (!root.TryGet("full", out JVal fullVal) || !fullVal.TryBool(out bool full)) return false;
            if (!root.TryGet("friends", out JVal list) || list.Kind != JKind.Array || list.Items == null) return false;

            var friends = new List<FriendPresence>(list.Items.Count);
            foreach (JVal item in list.Items)
            {
                if (!Str(item, "code", out string code) || !Str(item, "state", out string stateName) ||
                    !TryState(stateName, out PresenceState state)) return false;
                friends.Add(new FriendPresence(code, state));
            }
            msg = new PresenceMsg(full, friends);
            return true;
        }

        private static bool Str(JVal obj, string name, out string value)
        {
            value = "";
            return obj.TryGet(name, out JVal v) && v.TryStr(out value);
        }

        private static bool Int(JVal obj, string name, out int value)
        {
            value = 0;
            return obj.TryGet(name, out JVal v) && v.TryI32(out value);
        }

        private static string StateName(PresenceState state)
        {
            switch (state)
            {
                case PresenceState.Online: return "online";
                case PresenceState.InMatch: return "in_match";
                default: return "offline";
            }
        }

        private static bool TryState(string name, out PresenceState state)
        {
            switch (name)
            {
                case "online": state = PresenceState.Online; return true;
                case "in_match": state = PresenceState.InMatch; return true;
                case "offline": state = PresenceState.Offline; return true;
                default: state = PresenceState.Offline; return false;
            }
        }

        private static string ReasonName(ChallengeCloseReason reason)
        {
            switch (reason)
            {
                case ChallengeCloseReason.Declined: return "declined";
                case ChallengeCloseReason.Cancelled: return "cancelled";
                case ChallengeCloseReason.Expired: return "expired";
                case ChallengeCloseReason.Left: return "left";
                default: return "gone";
            }
        }

        private static bool TryReason(string name, out ChallengeCloseReason reason)
        {
            switch (name)
            {
                case "declined": reason = ChallengeCloseReason.Declined; return true;
                case "cancelled": reason = ChallengeCloseReason.Cancelled; return true;
                case "expired": reason = ChallengeCloseReason.Expired; return true;
                case "left": reason = ChallengeCloseReason.Left; return true;
                case "gone": reason = ChallengeCloseReason.Gone; return true;
                default: reason = ChallengeCloseReason.Gone; return false;
            }
        }
    }
}
