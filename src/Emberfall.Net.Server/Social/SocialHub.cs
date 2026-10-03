using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Server.Social
{
    /// <summary>
    /// Who is connected to <c>/social</c>, what their friends are told about it, and the challenge
    /// handshake between two of them (#409). In memory and per process: presence is DERIVED from
    /// live sockets and never stored, so there is nothing here to go stale across a restart.
    ///
    /// <para><b>Presence goes to accepted friends and to nobody else.</b> Every fan-out reads the
    /// subject's friend list from <see cref="IFriendStore"/> at the moment of sending rather than
    /// holding a subscription, so a removed or blocked friend stops hearing at the next change
    /// with no unsubscribe step to forget. A player with no social socket is Offline to their
    /// friends even mid-match — the socket IS the presence.</para>
    ///
    /// <para><b>A challenge is only ever between two accepted friends who are both Online</b>,
    /// and a refusal never says which of those failed (<see cref="ChallengeRefusedMsg"/>).</para>
    ///
    /// <para>Takes <see cref="IServerConnection"/>, not a socket, so it runs under test with no
    /// transport. One connection per player: a second login replaces the first.</para>
    /// </summary>
    public sealed class SocialHub
    {
        private readonly IFriendStore _friends;
        private readonly ChallengeBook _challenges;
        private readonly Func<string, bool> _isInMatch;
        private readonly FriendRequestLimiter _challengeLimiter;
        private readonly FriendRequestLimiter _chatLimiter;

        private readonly object _lock = new object();
        private readonly Dictionary<string, IServerConnection> _online =
            new Dictionary<string, IServerConnection>(StringComparer.Ordinal);

        /// <param name="isInMatch">Is this player id seated in a live match? Asked of the gate.</param>
        /// <param name="challengeLimiter">Caps <see cref="ChallengeSendMsg"/> per account — an
        /// offer is a prompt on somebody else's screen.</param>
        /// <param name="chatLimiter">Caps <see cref="ChatSendMsg"/> per account, for the same reason.</param>
        public SocialHub(IFriendStore friends, ChallengeBook challenges, Func<string, bool> isInMatch,
            FriendRequestLimiter challengeLimiter, FriendRequestLimiter chatLimiter)
        {
            _chatLimiter = chatLimiter;
            _friends = friends;
            _challenges = challenges;
            _isInMatch = isInMatch;
            _challengeLimiter = challengeLimiter;
        }

        public int OnlineCount
        {
            get { lock (_lock) return _online.Count; }
        }

        public async Task ConnectAsync(string playerId, IServerConnection conn, CancellationToken ct = default)
        {
            IServerConnection? replaced;
            lock (_lock)
            {
                _online.TryGetValue(playerId, out replaced);
                _online[playerId] = conn;
            }
            replaced?.Close(1008, "replaced by a newer connection");

            await SendSnapshotAsync(playerId, ct).ConfigureAwait(false);
            await AnnounceAsync(playerId, ct).ConfigureAwait(false);
        }

        /// <summary>A no-op unless <paramref name="conn"/> is still this player's CURRENT
        /// connection — a replaced socket closing must not mark its replacement offline.</summary>
        public async Task DisconnectAsync(string playerId, IServerConnection conn, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_online.TryGetValue(playerId, out var current) || !ReferenceEquals(current, conn)) return;
                _online.Remove(playerId);
            }

            Challenge? dropped = _challenges.DropOfferFor(playerId);
            if (dropped != null)
                Send(dropped.Other(playerId), new ChallengeClosedMsg(dropped.Id, ChallengeCloseReason.Left));

            await AnnounceAsync(playerId, ct).ConfigureAwait(false);
        }

        /// <summary>One inbound frame from an authenticated socket. Every string in
        /// <paramref name="msg"/> is the peer's and unchecked.</summary>
        public async Task HandleAsync(string playerId, SocialMsg msg, CancellationToken ct = default)
        {
            switch (msg)
            {
                case SocialPingMsg _:
                    Send(playerId, new SocialPongMsg());
                    break;
                case ChallengeSendMsg send:
                    await SendChallengeAsync(playerId, send.Code, ct).ConfigureAwait(false);
                    break;
                case ChallengeAcceptMsg accept:
                    AcceptChallenge(playerId, accept.ChallengeId);
                    break;
                case ChallengeCloseMsg close:
                    CloseChallenge(playerId, close.ChallengeId);
                    break;
                case ChatSendMsg chat:
                    await SendChatAsync(playerId, chat.Code, chat.Text, ct).ConfigureAwait(false);
                    break;
                // Anything else is a server-to-client type a client has no business sending.
            }
        }

        /// <summary>The friends graph around these players changed (a request, an accept, a
        /// removal, a block). Each one who is connected is told to re-read <c>GET /friends</c>
        /// and handed a fresh full presence list, since who they may see just changed.</summary>
        public async Task GraphChangedAsync(IEnumerable<string> playerIds, CancellationToken ct = default)
        {
            foreach (string playerId in playerIds.Distinct(StringComparer.Ordinal))
            {
                if (!IsOnline(playerId)) continue;
                Send(playerId, new FriendsChangedMsg());
                await SendSnapshotAsync(playerId, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A match these two are seated in started or ended.
        ///
        /// <para>Only a CONNECTED seat is announced, and the check is not an optimisation: this is
        /// raised for every match on the server, and reading the friend list of a seat that has
        /// never opened the social surface — a guest, a bot — would mint that seat a friend code.</para>
        /// </summary>
        public async Task MatchRosterChangedAsync(string p0, string p1, CancellationToken ct = default)
        {
            if (IsOnline(p0)) await AnnounceAsync(p0, ct).ConfigureAwait(false);
            if (IsOnline(p1)) await AnnounceAsync(p1, ct).ConfigureAwait(false);
        }

        /// <summary>Close every challenge past its deadline and tell both players. Driven by a
        /// timer — an offer nobody answers has no other event to end it.</summary>
        public void SweepChallenges()
        {
            foreach (Challenge expired in _challenges.Sweep())
            {
                var closed = new ChallengeClosedMsg(expired.Id, ChallengeCloseReason.Expired);
                Send(expired.FromPlayerId, closed);
                Send(expired.ToPlayerId, closed);
            }
        }

        private async Task SendChallengeAsync(string playerId, string rawCode, CancellationToken ct)
        {
            // The echo is the canonical form or nothing: a refused frame never reflects the
            // peer's raw string back at them.
            string code = FriendCode.TryNormalize(rawCode, out string canonical) ? canonical : "";

            Challenge? challenge = null;
            FriendList? mine = null;
            if (code.Length > 0 && _challengeLimiter.TryTake(playerId) && StateOf(playerId) == PresenceState.Online)
            {
                mine = await _friends.ReadAsync(playerId, ct).ConfigureAwait(false);
                FriendIdentity? target = mine.Friends.FirstOrDefault(f => f.Code == code);
                if (target != null && StateOf(target.PlayerId) == PresenceState.Online)
                    challenge = _challenges.Open(playerId, target.PlayerId);
            }

            if (challenge is null)
            {
                Send(playerId, new ChallengeRefusedMsg(code));
                return;
            }

            int ttl = (int)_challenges.OfferTtlMs;
            Send(challenge.ToPlayerId, new ChallengeIncomingMsg(challenge.Id, mine!.Self.Code, ttl));
            Send(playerId, new ChallengeSentMsg(challenge.Id, code, ttl));
        }

        /// <summary>
        /// A direct message (VULN-004). Delivered only to an accepted friend whose social socket is
        /// open, re-cleaned here whatever the sender's client did, and never stored: a friend who
        /// is not connected does not get it, and neither does anyone later. A block ends the
        /// friendship, so it mutes both ways with no separate check.
        ///
        /// <para>The rate limit is taken before anything is looked up, so a refusal costs the same
        /// whether or not the code is a friend's.</para>
        /// </summary>
        private async Task SendChatAsync(string playerId, string rawCode, string rawText, CancellationToken ct)
        {
            string code = FriendCode.TryNormalize(rawCode, out string canonical) ? canonical : "";
            string? text = ChatText.Clean(rawText);

            FriendList? mine = null;
            FriendIdentity? target = null;
            if (code.Length > 0 && text != null && _chatLimiter.TryTake(playerId))
            {
                mine = await _friends.ReadAsync(playerId, ct).ConfigureAwait(false);
                target = mine.Friends.FirstOrDefault(f => f.Code == code);
            }

            if (target is null || !IsOnline(target.PlayerId))
            {
                Send(playerId, new ChatRefusedMsg(code));
                return;
            }

            Send(target.PlayerId, new ChatMsg(mine!.Self.Code, text!, Mine: false));
            Send(playerId, new ChatMsg(code, text!, Mine: true));
        }

        private void AcceptChallenge(string playerId, string challengeId)
        {
            Challenge? challenge = _challenges.Accept(challengeId, playerId);
            if (challenge is null)
            {
                Send(playerId, new ChallengeClosedMsg(Echo(challengeId), ChallengeCloseReason.Gone));
                return;
            }

            // Either of them may have been seated in a queued match while the offer stood.
            if (_isInMatch(challenge.FromPlayerId) || _isInMatch(challenge.ToPlayerId))
            {
                _challenges.Finish(challenge.Id);
                var gone = new ChallengeClosedMsg(challenge.Id, ChallengeCloseReason.Gone);
                Send(challenge.FromPlayerId, gone);
                Send(challenge.ToPlayerId, gone);
                return;
            }

            int ttl = (int)_challenges.TicketTtlMs;
            Send(challenge.FromPlayerId, new ChallengeReadyMsg(challenge.Id, challenge.Tickets![0], ttl));
            Send(challenge.ToPlayerId, new ChallengeReadyMsg(challenge.Id, challenge.Tickets[1], ttl));
        }

        private void CloseChallenge(string playerId, string challengeId)
        {
            Challenge? challenge = _challenges.Close(challengeId, playerId);
            if (challenge is null)
            {
                Send(playerId, new ChallengeClosedMsg(Echo(challengeId), ChallengeCloseReason.Gone));
                return;
            }

            var reason = playerId == challenge.ToPlayerId && !challenge.Accepted
                ? ChallengeCloseReason.Declined
                : ChallengeCloseReason.Cancelled;
            var closed = new ChallengeClosedMsg(challenge.Id, reason);
            Send(challenge.FromPlayerId, closed);
            Send(challenge.ToPlayerId, closed);
        }

        /// <summary>Tell this player's connected friends where they are now.</summary>
        private async Task AnnounceAsync(string playerId, CancellationToken ct)
        {
            if (OnlineCount == 0) return;

            FriendList list = await _friends.ReadAsync(playerId, ct).ConfigureAwait(false);
            if (list.Friends.Count == 0) return;

            var update = new PresenceMsg(false, new[] { new FriendPresence(list.Self.Code, StateOf(playerId)) });
            foreach (FriendIdentity friend in list.Friends) Send(friend.PlayerId, update);
        }

        /// <summary>Tell this player where every friend is. A friend not listed is offline.</summary>
        private async Task SendSnapshotAsync(string playerId, CancellationToken ct)
        {
            FriendList list = await _friends.ReadAsync(playerId, ct).ConfigureAwait(false);
            var present = new List<FriendPresence>();
            foreach (FriendIdentity friend in list.Friends)
            {
                PresenceState state = StateOf(friend.PlayerId);
                if (state != PresenceState.Offline) present.Add(new FriendPresence(friend.Code, state));
            }
            Send(playerId, new PresenceMsg(true, present));
        }

        private PresenceState StateOf(string playerId)
            => !IsOnline(playerId) ? PresenceState.Offline
                : _isInMatch(playerId) ? PresenceState.InMatch
                : PresenceState.Online;

        private bool IsOnline(string playerId)
        {
            lock (_lock) return _online.ContainsKey(playerId);
        }

        private void Send(string playerId, SocialMsg msg)
        {
            IServerConnection? conn;
            lock (_lock) _online.TryGetValue(playerId, out conn);
            if (conn is null) return;

            // A full or dead writer is a client that is not reading. Closing it runs the ordinary
            // disconnect path; holding frames for it would not.
            if (!conn.TryEnqueue(SocialCodec.Encode(msg))) conn.Close(1008, "not reading");
        }

        /// <summary>A challenge id the server did not recognise, cut to the length of a real one
        /// before it is echoed to the client that sent it.</summary>
        private static string Echo(string challengeId)
            => challengeId.Length <= 32 ? challengeId : challengeId.Substring(0, 32);
    }
}
