using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Kingdom.Net.Server.Social
{
    /// <summary>One friend's challenge to another, from offer to match.</summary>
    public sealed class Challenge
    {
        public string Id { get; }
        public string FromPlayerId { get; }
        public string ToPlayerId { get; }

        /// <summary>Server clock ms after which this challenge is dead, in whichever state it is.
        /// Reset when it is accepted: the offer's clock and the ticket's clock are different waits.</summary>
        public long ExpiresAtMs { get; internal set; }

        /// <summary>Null until accepted. Index 0 is the sender's ticket, 1 the target's.</summary>
        public string[]? Tickets { get; internal set; }

        internal bool[] Redeemed { get; } = new bool[2];

        public bool Accepted => Tickets != null;

        internal Challenge(string id, string from, string to, long expiresAtMs)
        {
            Id = id;
            FromPlayerId = from;
            ToPlayerId = to;
            ExpiresAtMs = expiresAtMs;
        }

        public bool Involves(string playerId) => FromPlayerId == playerId || ToPlayerId == playerId;
        public string Other(string playerId) => FromPlayerId == playerId ? ToPlayerId : FromPlayerId;
    }

    /// <summary>What a redeemed ticket entitles its holder to: a seat in one private match.</summary>
    public sealed record ChallengeSeat(string ChallengeId, string OpponentPlayerId);

    /// <summary>
    /// The half of the challenge handshake <c>MatchGate</c> sees. A separate interface so the gate
    /// depends on "is this ticket good for this player" and on nothing else about the social surface.
    /// </summary>
    public interface IChallengeTickets
    {
        /// <summary>
        /// Spend a ticket. Null unless it is live, unspent, AND was issued to
        /// <paramref name="playerId"/> — the ticket alone is not enough, which is what makes a
        /// leaked or guessed one useless to anyone but its owner.
        /// </summary>
        ChallengeSeat? Redeem(string ticket, string playerId);

        /// <summary>The match started, or will never start; the challenge is over either way.</summary>
        void Finish(string challengeId);
    }

    /// <summary>
    /// Every live challenge (#409). In memory and per process, like presence: a challenge is a
    /// conversation between two connected sockets and has nothing worth surviving a restart.
    ///
    /// <para><b>One live challenge per player, in either role.</b> That single rule is the whole
    /// state machine for "already challenging someone" and "being challenged twice", and it is
    /// what lets a client show one prompt instead of a queue of them.</para>
    ///
    /// <para>Pure state behind one lock — no sockets, no store. <see cref="SocialHub"/> decides who
    /// may open one and who is told what; this decides only what is true.</para>
    /// </summary>
    public sealed class ChallengeBook : IChallengeTickets
    {
        private readonly ITimeSource _time;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Challenge> _byId = new Dictionary<string, Challenge>(StringComparer.Ordinal);
        private readonly Dictionary<string, Challenge> _byPlayer = new Dictionary<string, Challenge>(StringComparer.Ordinal);
        private readonly Dictionary<string, Challenge> _byTicket = new Dictionary<string, Challenge>(StringComparer.Ordinal);

        /// <summary>How long an offer waits for an answer.</summary>
        public long OfferTtlMs { get; }

        /// <summary>How long an accepted challenge waits for both players to reach <c>/ws</c>.</summary>
        public long TicketTtlMs { get; }

        public ChallengeBook(ITimeSource time, long offerTtlMs = 60_000, long ticketTtlMs = 30_000)
        {
            _time = time;
            OfferTtlMs = offerTtlMs;
            TicketTtlMs = ticketTtlMs;
        }

        /// <summary>A new offer, or null if either player already has a live challenge.</summary>
        public Challenge? Open(string fromPlayerId, string toPlayerId)
        {
            lock (_lock)
            {
                if (fromPlayerId == toPlayerId) return null;
                if (Live(fromPlayerId) != null || Live(toPlayerId) != null) return null;

                var challenge = new Challenge(NewToken(), fromPlayerId, toPlayerId, _time.NowMs + OfferTtlMs);
                _byId[challenge.Id] = challenge;
                _byPlayer[fromPlayerId] = challenge;
                _byPlayer[toPlayerId] = challenge;
                return challenge;
            }
        }

        /// <summary>The target accepts. Mints the two tickets and restarts the clock. Null unless
        /// <paramref name="byPlayerId"/> is the target of a live, unanswered offer.</summary>
        public Challenge? Accept(string challengeId, string byPlayerId)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(challengeId, out var challenge) || Expired(challenge)) return null;
                if (challenge.Accepted || challenge.ToPlayerId != byPlayerId) return null;

                challenge.Tickets = new[] { NewToken(), NewToken() };
                challenge.ExpiresAtMs = _time.NowMs + TicketTtlMs;
                _byTicket[challenge.Tickets[0]] = challenge;
                _byTicket[challenge.Tickets[1]] = challenge;
                return challenge;
            }
        }

        /// <summary>Either party ends it. Null if <paramref name="byPlayerId"/> is not one of them
        /// or the challenge is not live.</summary>
        public Challenge? Close(string challengeId, string byPlayerId)
        {
            lock (_lock)
            {
                // An expired one is left for Sweep, which is what tells BOTH players it expired.
                if (!_byId.TryGetValue(challengeId, out var challenge) || Expired(challenge) ||
                    !challenge.Involves(byPlayerId))
                    return null;
                Remove(challenge);
                return challenge;
            }
        }

        /// <summary>The live challenge this player is part of, if any — removed. Used when their
        /// social socket goes away, so the other side is not left holding an offer nobody can
        /// answer. An ACCEPTED challenge is left alone: both players are on their way to
        /// <c>/ws</c> and the social socket is no longer what carries it.</summary>
        public Challenge? DropOfferFor(string playerId)
        {
            lock (_lock)
            {
                Challenge? challenge = Live(playerId);
                if (challenge is null || challenge.Accepted) return null;
                Remove(challenge);
                return challenge;
            }
        }

        /// <summary>Removes and returns everything past its deadline.</summary>
        public IReadOnlyList<Challenge> Sweep()
        {
            lock (_lock)
            {
                List<Challenge>? expired = null;
                foreach (Challenge challenge in _byId.Values)
                    if (Expired(challenge)) (expired ??= new List<Challenge>()).Add(challenge);
                if (expired is null) return Array.Empty<Challenge>();
                foreach (Challenge challenge in expired) Remove(challenge);
                return expired;
            }
        }

        public bool HasLive(string playerId)
        {
            lock (_lock) return Live(playerId) != null;
        }

        public ChallengeSeat? Redeem(string ticket, string playerId)
        {
            lock (_lock)
            {
                if (!_byTicket.TryGetValue(ticket, out var challenge) || Expired(challenge)) return null;

                int seat = challenge.Tickets![0] == ticket ? 0 : 1;
                string owner = seat == 0 ? challenge.FromPlayerId : challenge.ToPlayerId;
                if (owner != playerId || challenge.Redeemed[seat]) return null;

                challenge.Redeemed[seat] = true;
                return new ChallengeSeat(challenge.Id, challenge.Other(playerId));
            }
        }

        public void Finish(string challengeId)
        {
            lock (_lock)
                if (_byId.TryGetValue(challengeId, out var challenge)) Remove(challenge);
        }

        private Challenge? Live(string playerId)
            => _byPlayer.TryGetValue(playerId, out var challenge) && !Expired(challenge) ? challenge : null;

        private bool Expired(Challenge challenge) => _time.NowMs >= challenge.ExpiresAtMs;

        private void Remove(Challenge challenge)
        {
            _byId.Remove(challenge.Id);
            foreach (string player in new[] { challenge.FromPlayerId, challenge.ToPlayerId })
                if (_byPlayer.TryGetValue(player, out var held) && ReferenceEquals(held, challenge))
                    _byPlayer.Remove(player);
            if (challenge.Tickets != null)
                foreach (string ticket in challenge.Tickets) _byTicket.Remove(ticket);
        }

        /// <summary>128 random bits as hex. Used for both the challenge id and the tickets; the
        /// ticket is the one that has to be unguessable.</summary>
        private static string NewToken()
        {
            Span<byte> bytes = stackalloc byte[16];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
