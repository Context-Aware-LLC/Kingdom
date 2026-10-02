using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Server.Social
{
    /// <summary>The caps on one player's social graph (#409). Every list a client can be handed
    /// is bounded by one of these, so <c>GET /friends</c> has a ceiling on its size.</summary>
    public sealed record FriendLimits(
        int MaxFriends = 100,
        /// <summary>Requests this player has sent that nobody has answered.</summary>
        int MaxPendingOutgoing = 20,
        /// <summary>Requests waiting on this player. Past it a new request is dropped silently —
        /// the sender must not learn the target's inbox is full.</summary>
        int MaxPendingIncoming = 50,
        int MaxBlocked = 200);

    /// <summary>A player as the social surface names them: a code, and the name they chose to show.</summary>
    public sealed record FriendIdentity(string PlayerId, string Code, string? DisplayName);

    /// <summary>Everything <c>GET /friends</c> answers with, before it becomes JSON.</summary>
    public sealed record FriendList(
        FriendIdentity Self,
        IReadOnlyList<FriendIdentity> Friends,
        IReadOnlyList<FriendIdentity> Incoming,
        IReadOnlyList<FriendIdentity> Outgoing,
        IReadOnlyList<FriendIdentity> Blocked);

    public enum FriendRequestOutcome
    {
        /// <summary>A pending request now stands from the sender to the target.</summary>
        Sent,
        /// <summary>One already stood. Nothing was written.</summary>
        AlreadyPending,
        AlreadyFriends,
        /// <summary>The target had already asked the sender, so this request answered theirs.</summary>
        Accepted,
        /// <summary>Nothing was written and the sender must not be told why: the target is the
        /// sender, either side has blocked the other, or the target's own caps are full.</summary>
        Refused,
        /// <summary>The SENDER's own cap is full. Safe to say — it is about the caller.</summary>
        AtCapacity,
    }

    public enum FriendAcceptOutcome
    {
        /// <summary>The two are friends — whether this call did it or an earlier one had.</summary>
        Accepted,
        /// <summary>No request from that player is waiting on this one.</summary>
        NotFound,
        AtCapacity,
    }

    public enum FriendBlockOutcome
    {
        Blocked,
        AtCapacity,
    }

    /// <summary>
    /// The friends graph (#409): codes, requests, friendships and blocks. Keyed on
    /// <c>Account.PlayerId</c> like the ledger, and never on an email — some accounts have none.
    ///
    /// <para><b>The store never decides what a client is told.</b> Its outcomes distinguish cases
    /// the wire must not (<see cref="FriendRequestOutcome.Refused"/> from
    /// <see cref="FriendRequestOutcome.Sent"/>); collapsing them is <see cref="FriendsEndpoint"/>'s
    /// job, so tests can still assert what actually happened.</para>
    ///
    /// <para>Two implementations share this contract and one test suite: in-memory for tests and
    /// dev, Postgres for production. Every write is idempotent — the Postgres store retries a
    /// whole unit after a dead pooled socket.</para>
    /// </summary>
    public interface IFriendStore
    {
        /// <summary>This player's code and shown name, minting the code on first sight. A player
        /// who has never reached the social surface has no code and cannot be found by one.</summary>
        Task<FriendIdentity> EnsureIdentityAsync(string playerId, CancellationToken ct = default);

        /// <summary>The holder of a canonical code, or null. Never mints.</summary>
        Task<string?> FindPlayerIdByCodeAsync(string code, CancellationToken ct = default);

        /// <summary>Sets the name this player's friends see — their sign-up name, copied in by
        /// <see cref="FriendsEndpoint"/>. The caller has already run <c>PlayerName.Check</c>; the
        /// store stores what it is given.</summary>
        Task SetDisplayNameAsync(string playerId, string displayName, CancellationToken ct = default);

        /// <summary>The whole graph around one player. Each list is ordered oldest first.</summary>
        Task<FriendList> ReadAsync(string playerId, CancellationToken ct = default);

        Task<FriendRequestOutcome> RequestAsync(string fromPlayerId, string toPlayerId,
            FriendLimits limits, CancellationToken ct = default);

        Task<FriendAcceptOutcome> AcceptAsync(string playerId, string requesterPlayerId,
            FriendLimits limits, CancellationToken ct = default);

        /// <summary>Drops a pending request between the two, whichever of them sent it — so it is
        /// both "decline" and "cancel". False if none stood.</summary>
        Task<bool> DeclineAsync(string playerId, string otherPlayerId, CancellationToken ct = default);

        /// <summary>Ends an accepted friendship. False if the two were not friends.</summary>
        Task<bool> RemoveAsync(string playerId, string otherPlayerId, CancellationToken ct = default);

        /// <summary>Blocks <paramref name="otherPlayerId"/> and drops whatever stood between the
        /// two. A block is one-directional and each side's is its own row: lifting mine never
        /// lifts theirs.</summary>
        Task<FriendBlockOutcome> BlockAsync(string playerId, string otherPlayerId,
            FriendLimits limits, CancellationToken ct = default);

        Task<bool> UnblockAsync(string playerId, string otherPlayerId, CancellationToken ct = default);
    }

    /// <summary>Mints the random half of a friend code. Cryptographic randomness, because a
    /// guessable sequence would turn "share your code" into "anyone can enumerate the next one".</summary>
    internal static class FriendCodeMint
    {
        public static string Next()
        {
            Span<byte> bytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(bytes);
            return FriendCode.FromValue(BitConverter.ToInt64(bytes));
        }
    }

    /// <summary>The in-memory friends store — the default for tests and single-box dev. Same
    /// semantics as <see cref="PostgresFriendStore"/>; thread-safe under a single lock.</summary>
    public sealed class InMemoryFriendStore : IFriendStore
    {
        private sealed class Player
        {
            public string Code = "";
            public string? DisplayName;
        }

        private sealed class Friendship
        {
            public bool Accepted;
            public string RequestedBy = "";
            public long CreatedAtMs;
        }

        private readonly ITimeSource _time;
        private readonly Func<string> _mint;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Player> _players = new Dictionary<string, Player>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _playerByCode = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<(string Lo, string Hi), Friendship> _friendships = new Dictionary<(string, string), Friendship>();
        private readonly Dictionary<(string Blocker, string Blocked), long> _blocks = new Dictionary<(string, string), long>();

        public InMemoryFriendStore(ITimeSource time) : this(time, FriendCodeMint.Next) { }

        /// <param name="mint">The code source — injectable so a test can force a collision.</param>
        internal InMemoryFriendStore(ITimeSource time, Func<string> mint)
        {
            _time = time;
            _mint = mint;
        }

        public Task<FriendIdentity> EnsureIdentityAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock) return Task.FromResult(Identity(playerId, Ensure(playerId)));
        }

        public Task<string?> FindPlayerIdByCodeAsync(string code, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _playerByCode.TryGetValue(code, out string? playerId);
                return Task.FromResult(playerId);
            }
        }

        public Task SetDisplayNameAsync(string playerId, string displayName, CancellationToken ct = default)
        {
            lock (_lock) Ensure(playerId).DisplayName = displayName;
            return Task.CompletedTask;
        }

        public Task<FriendList> ReadAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var self = Identity(playerId, Ensure(playerId));
                var friends = new List<(long At, FriendIdentity Who)>();
                var incoming = new List<(long At, FriendIdentity Who)>();
                var outgoing = new List<(long At, FriendIdentity Who)>();

                foreach (var pair in _friendships)
                {
                    string? other = Other(pair.Key, playerId);
                    if (other is null) continue;
                    var entry = (pair.Value.CreatedAtMs, Identity(other, _players[other]));
                    if (pair.Value.Accepted) friends.Add(entry);
                    else if (pair.Value.RequestedBy == playerId) outgoing.Add(entry);
                    else incoming.Add(entry);
                }

                var blocked = _blocks
                    .Where(b => b.Key.Blocker == playerId)
                    .Select(b => (b.Value, Identity(b.Key.Blocked, _players[b.Key.Blocked])))
                    .ToList();

                return Task.FromResult(new FriendList(
                    self, Ordered(friends), Ordered(incoming), Ordered(outgoing), Ordered(blocked)));
            }
        }

        public Task<FriendRequestOutcome> RequestAsync(string fromPlayerId, string toPlayerId,
            FriendLimits limits, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (fromPlayerId == toPlayerId || !_players.ContainsKey(toPlayerId) ||
                    _blocks.ContainsKey((fromPlayerId, toPlayerId)) ||
                    _blocks.ContainsKey((toPlayerId, fromPlayerId)))
                    return Task.FromResult(FriendRequestOutcome.Refused);

                Ensure(fromPlayerId);
                var key = Key(fromPlayerId, toPlayerId);
                if (_friendships.TryGetValue(key, out var existing))
                {
                    if (existing.Accepted) return Task.FromResult(FriendRequestOutcome.AlreadyFriends);
                    if (existing.RequestedBy == fromPlayerId)
                        return Task.FromResult(FriendRequestOutcome.AlreadyPending);

                    // They asked first, so this is the answer to their request.
                    if (Count(fromPlayerId).Friends >= limits.MaxFriends)
                        return Task.FromResult(FriendRequestOutcome.AtCapacity);
                    if (Count(toPlayerId).Friends >= limits.MaxFriends)
                        return Task.FromResult(FriendRequestOutcome.Refused);
                    existing.Accepted = true;
                    return Task.FromResult(FriendRequestOutcome.Accepted);
                }

                var mine = Count(fromPlayerId);
                if (mine.Friends >= limits.MaxFriends || mine.Outgoing >= limits.MaxPendingOutgoing)
                    return Task.FromResult(FriendRequestOutcome.AtCapacity);
                if (Count(toPlayerId).Incoming >= limits.MaxPendingIncoming)
                    return Task.FromResult(FriendRequestOutcome.Refused);

                _friendships[key] = new Friendship { RequestedBy = fromPlayerId, CreatedAtMs = _time.NowMs };
                return Task.FromResult(FriendRequestOutcome.Sent);
            }
        }

        public Task<FriendAcceptOutcome> AcceptAsync(string playerId, string requesterPlayerId,
            FriendLimits limits, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (!_friendships.TryGetValue(Key(playerId, requesterPlayerId), out var existing))
                    return Task.FromResult(FriendAcceptOutcome.NotFound);
                if (existing.Accepted) return Task.FromResult(FriendAcceptOutcome.Accepted);
                if (existing.RequestedBy != requesterPlayerId)
                    return Task.FromResult(FriendAcceptOutcome.NotFound);

                if (Count(playerId).Friends >= limits.MaxFriends ||
                    Count(requesterPlayerId).Friends >= limits.MaxFriends)
                    return Task.FromResult(FriendAcceptOutcome.AtCapacity);

                existing.Accepted = true;
                return Task.FromResult(FriendAcceptOutcome.Accepted);
            }
        }

        public Task<bool> DeclineAsync(string playerId, string otherPlayerId, CancellationToken ct = default)
            => Task.FromResult(Drop(playerId, otherPlayerId, accepted: false));

        public Task<bool> RemoveAsync(string playerId, string otherPlayerId, CancellationToken ct = default)
            => Task.FromResult(Drop(playerId, otherPlayerId, accepted: true));

        public Task<FriendBlockOutcome> BlockAsync(string playerId, string otherPlayerId,
            FriendLimits limits, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (playerId == otherPlayerId || !_players.ContainsKey(otherPlayerId))
                    return Task.FromResult(FriendBlockOutcome.Blocked);

                Ensure(playerId);
                if (!_blocks.ContainsKey((playerId, otherPlayerId)))
                {
                    if (_blocks.Keys.Count(b => b.Blocker == playerId) >= limits.MaxBlocked)
                        return Task.FromResult(FriendBlockOutcome.AtCapacity);
                    _blocks[(playerId, otherPlayerId)] = _time.NowMs;
                }

                _friendships.Remove(Key(playerId, otherPlayerId));
                return Task.FromResult(FriendBlockOutcome.Blocked);
            }
        }

        public Task<bool> UnblockAsync(string playerId, string otherPlayerId, CancellationToken ct = default)
        {
            lock (_lock) return Task.FromResult(_blocks.Remove((playerId, otherPlayerId)));
        }

        private bool Drop(string playerId, string otherPlayerId, bool accepted)
        {
            lock (_lock)
            {
                var key = Key(playerId, otherPlayerId);
                return _friendships.TryGetValue(key, out var existing)
                    && existing.Accepted == accepted
                    && _friendships.Remove(key);
            }
        }

        private Player Ensure(string playerId)
        {
            if (_players.TryGetValue(playerId, out var player)) return player;

            string code = _mint();
            while (_playerByCode.ContainsKey(code)) code = _mint();

            player = new Player { Code = code };
            _players[playerId] = player;
            _playerByCode[code] = playerId;
            return player;
        }

        private (int Friends, int Outgoing, int Incoming) Count(string playerId)
        {
            int friends = 0, outgoing = 0, incoming = 0;
            foreach (var pair in _friendships)
            {
                if (Other(pair.Key, playerId) is null) continue;
                if (pair.Value.Accepted) friends++;
                else if (pair.Value.RequestedBy == playerId) outgoing++;
                else incoming++;
            }
            return (friends, outgoing, incoming);
        }

        private static FriendIdentity Identity(string playerId, Player player)
            => new FriendIdentity(playerId, player.Code, player.DisplayName);

        private static string? Other((string Lo, string Hi) key, string playerId)
            => key.Lo == playerId ? key.Hi : key.Hi == playerId ? key.Lo : null;

        /// <summary>One row per pair, whoever asked: the lower id always goes first.</summary>
        internal static (string Lo, string Hi) Key(string a, string b)
            => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

        private static IReadOnlyList<FriendIdentity> Ordered(List<(long At, FriendIdentity Who)> entries)
            => entries.OrderBy(e => e.At).ThenBy(e => e.Who.Code, StringComparer.Ordinal)
                .Select(e => e.Who).ToList();
    }
}
