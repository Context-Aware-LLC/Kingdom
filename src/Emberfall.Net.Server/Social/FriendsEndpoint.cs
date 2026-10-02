using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Social
{
    /// <summary>What a <c>/friends</c> route answers with. The lower-cased name is the wire
    /// <c>status</c> string.</summary>
    public enum FriendsStatus
    {
        /// <summary><c>GET /friends</c> read the graph. Empty lists are still Known.</summary>
        Known,

        /// <summary><c>POST /friends/request</c> was taken. <b>This is the answer for a request
        /// that was sent AND for one that was not</b> — unknown code, own code, blocked either
        /// way, already friends, already pending. See <see cref="FriendsEndpoint.RequestAsync"/>.</summary>
        Sent,
        Accepted,
        Declined,
        Removed,
        Blocked,
        Unblocked,
        Named,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store. 503 — never readable as "no friends".</summary>
        Unavailable,

        /// <summary>The code is not well-formed (wrong length, alphabet or check symbol). 400.
        /// Safe to say: the client can compute this itself, so it discloses nothing.</summary>
        InvalidCode,

        /// <summary><c>PlayerName.Check</c> refused the name. 400.</summary>
        InvalidName,

        /// <summary><c>POST /friends/accept</c>: no request from that code is waiting. 404.</summary>
        NotFound,

        /// <summary>One of the CALLER's own caps is full. 409.</summary>
        AtCapacity,

        /// <summary>Too many code-taking writes from this account. 429.</summary>
        RateLimited,
    }

    /// <summary>A <c>/friends</c> answer, before it becomes JSON.</summary>
    public sealed class FriendsResult
    {
        public FriendsStatus Status { get; }

        /// <summary>The caller's graph AFTER the call, on every 200. Null on every refusal — an
        /// absent list cannot be misread as an empty one.</summary>
        public FriendList? List { get; }

        public FriendsResult(FriendsStatus status, FriendList? list = null)
        {
            Status = status;
            List = list;
        }

        public int HttpStatus => Status switch
        {
            FriendsStatus.Unauthenticated => 401,
            FriendsStatus.Unavailable => 503,
            FriendsStatus.InvalidCode => 400,
            FriendsStatus.InvalidName => 400,
            FriendsStatus.NotFound => 404,
            FriendsStatus.AtCapacity => 409,
            FriendsStatus.RateLimited => 429,
            _ => 200,
        };
    }

    /// <summary>
    /// A fixed-window cap on how many code-taking writes one account may make. In memory and per
    /// process: with more than one machine the real ceiling is this times the machine count, which
    /// is still a ceiling.
    /// </summary>
    public sealed class FriendRequestLimiter
    {
        private readonly ITimeSource _time;
        private readonly int _max;
        private readonly long _windowMs;
        private readonly object _lock = new object();
        private readonly Dictionary<string, (long WindowStart, int Count)> _windows =
            new Dictionary<string, (long, int)>(System.StringComparer.Ordinal);

        public FriendRequestLimiter(ITimeSource time, int max = 10, long windowMs = 60_000)
        {
            _time = time;
            _max = max;
            _windowMs = windowMs;
        }

        public bool TryTake(string playerId)
        {
            long now = _time.NowMs;
            lock (_lock)
            {
                // Expired windows are dropped when the table has grown, so an account that asked
                // once and never again does not hold an entry for the life of the process.
                if (_windows.Count >= 4096)
                    foreach (string stale in _windows.Where(w => now - w.Value.WindowStart >= _windowMs)
                                 .Select(w => w.Key).ToList())
                        _windows.Remove(stale);

                if (!_windows.TryGetValue(playerId, out var window) || now - window.WindowStart >= _windowMs)
                    window = (now, 0);
                if (window.Count >= _max) return false;

                _windows[playerId] = (window.WindowStart, window.Count + 1);
                return true;
            }
        }
    }

    /// <summary>
    /// The <c>/friends</c> routes (#409): a player's friend code, their friends, the requests in
    /// flight and their blocks. Design: <c>docs/dev-docs/design/friends-and-challenges-plan.md</c>.
    ///
    /// <para>Resolves the account exactly as <see cref="SessionEndpoint"/> and
    /// <see cref="RosterEndpoint"/> do — verifier, then <c>CredentialHash.Of(identity.Subject)</c>,
    /// then <c>ResolveOrCreateAsync</c>. A guest device token never reaches here: there is no
    /// bearer to verify, so the friends graph only ever holds signed-in accounts.</para>
    ///
    /// <para><b>Every target is named by friend code and no <c>player_id</c> is ever answered.</b>
    /// A code is something its holder chose to hand out; a player id keys the ledger.</para>
    ///
    /// <para><b>What a caller can learn about a code they were not given</b> — the property the
    /// status collapse in <see cref="RequestAsync"/> exists for. The response is identical whether
    /// or not the code is held, and a caller's own caps are checked BEFORE the code is resolved so
    /// "at capacity" cannot depend on it either. What remains is the caller's own outgoing and
    /// blocked lists, which necessarily show a code that was real; that oracle is bounded by
    /// <see cref="FriendRequestLimiter"/> against a space of 32^9 codes, and those two lists carry
    /// the code only — never the holder's name.</para>
    /// </summary>
    public sealed class FriendsEndpoint
    {
        private readonly IIdentityTokenVerifier? _verifier;
        private readonly IAccountStore? _accounts;
        private readonly IFriendStore _friends;
        private readonly FriendLimits _limits;
        private readonly FriendRequestLimiter _limiter;
        private readonly SocialHub? _hub;

        /// <param name="hub">Told whenever a write changes somebody ELSE's view of the graph, so
        /// a connected player hears about a request or a removal without polling. Null on a box
        /// with no social socket; the routes are complete without it.</param>
        public FriendsEndpoint(
            IIdentityTokenVerifier? verifier,
            IAccountStore? accounts,
            IFriendStore friends,
            FriendLimits limits,
            FriendRequestLimiter limiter,
            SocialHub? hub = null)
        {
            _verifier = verifier;
            _accounts = accounts;
            _friends = friends;
            _limits = limits;
            _limiter = limiter;
            _hub = hub;
        }

        public FriendLimits Limits => _limits;

        public async Task<FriendsResult> ReadAsync(string? bearerToken, CancellationToken ct = default)
        {
            var (refusal, playerId) = await ResolveAsync(bearerToken, ct).ConfigureAwait(false);
            return refusal ?? await DoneAsync(FriendsStatus.Known, playerId, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// <c>POST /friends/request {code}</c>.
        ///
        /// <para><b>ORDER MATTERS, and every step before the lookup must be independent of
        /// whether the code is held.</b> Rate limit, then well-formedness, then the caller's own
        /// caps, and only then the code. Move the cap check below the lookup and "at capacity"
        /// becomes an answer only real codes can produce.</para>
        /// </summary>
        public async Task<FriendsResult> RequestAsync(string? bearerToken, string? code, CancellationToken ct = default)
        {
            var (refusal, playerId) = await ResolveAsync(bearerToken, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;

            if (!_limiter.TryTake(playerId)) return new FriendsResult(FriendsStatus.RateLimited);
            if (!FriendCode.TryNormalize(code, out string canonical))
                return new FriendsResult(FriendsStatus.InvalidCode);

            FriendList mine = await _friends.ReadAsync(playerId, ct).ConfigureAwait(false);
            if (mine.Friends.Count >= _limits.MaxFriends || mine.Outgoing.Count >= _limits.MaxPendingOutgoing)
                return new FriendsResult(FriendsStatus.AtCapacity);

            string? target = await _friends.FindPlayerIdByCodeAsync(canonical, ct).ConfigureAwait(false);
            if (target != null)
            {
                FriendRequestOutcome outcome = await _friends
                    .RequestAsync(playerId, target, _limits, ct).ConfigureAwait(false);

                // Reachable only when a concurrent request filled the cap after the check above.
                if (outcome == FriendRequestOutcome.AtCapacity)
                    return new FriendsResult(FriendsStatus.AtCapacity);

                if (outcome == FriendRequestOutcome.Sent)
                    await ChangedAsync(ct, target).ConfigureAwait(false);
                else if (outcome == FriendRequestOutcome.Accepted)
                    await ChangedAsync(ct, playerId, target).ConfigureAwait(false);
            }

            return await DoneAsync(FriendsStatus.Sent, playerId, ct).ConfigureAwait(false);
        }

        /// <summary><c>POST /friends/accept {code}</c> — answer a request that is waiting on the
        /// caller. NotFound covers an unknown code and a code with no request behind it alike.</summary>
        public async Task<FriendsResult> AcceptAsync(string? bearerToken, string? code, CancellationToken ct = default)
        {
            var (refusal, playerId, target) = await ResolveTargetAsync(bearerToken, code, limited: false, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;
            if (target is null) return new FriendsResult(FriendsStatus.NotFound);

            switch (await _friends.AcceptAsync(playerId, target, _limits, ct).ConfigureAwait(false))
            {
                case FriendAcceptOutcome.Accepted:
                    await ChangedAsync(ct, playerId, target).ConfigureAwait(false);
                    return await DoneAsync(FriendsStatus.Accepted, playerId, ct).ConfigureAwait(false);
                case FriendAcceptOutcome.AtCapacity:
                    return new FriendsResult(FriendsStatus.AtCapacity);
                default:
                    return new FriendsResult(FriendsStatus.NotFound);
            }
        }

        /// <summary><c>POST /friends/decline {code}</c> — refuse a request, or withdraw one the
        /// caller sent. Idempotent: the answer is the same whether or not one stood.</summary>
        public async Task<FriendsResult> DeclineAsync(string? bearerToken, string? code, CancellationToken ct = default)
        {
            var (refusal, playerId, target) = await ResolveTargetAsync(bearerToken, code, limited: false, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;
            if (target != null && await _friends.DeclineAsync(playerId, target, ct).ConfigureAwait(false))
                await ChangedAsync(ct, target).ConfigureAwait(false);
            return await DoneAsync(FriendsStatus.Declined, playerId, ct).ConfigureAwait(false);
        }

        /// <summary><c>POST /friends/remove {code}</c> — end a friendship. Idempotent.</summary>
        public async Task<FriendsResult> RemoveAsync(string? bearerToken, string? code, CancellationToken ct = default)
        {
            var (refusal, playerId, target) = await ResolveTargetAsync(bearerToken, code, limited: false, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;
            if (target != null && await _friends.RemoveAsync(playerId, target, ct).ConfigureAwait(false))
                await ChangedAsync(ct, playerId, target).ConfigureAwait(false);
            return await DoneAsync(FriendsStatus.Removed, playerId, ct).ConfigureAwait(false);
        }

        /// <summary><c>POST /friends/block {code}</c>. Takes any code, so it is rate-limited and
        /// answers the same for a code nobody holds — the same reasoning as
        /// <see cref="RequestAsync"/>.</summary>
        public async Task<FriendsResult> BlockAsync(string? bearerToken, string? code, CancellationToken ct = default)
        {
            var (refusal, playerId, target) = await ResolveTargetAsync(bearerToken, code, limited: true, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;

            if (target != null)
            {
                // Read BEFORE the block, because the block is what erases the answer. The target
                // is told only when something of theirs actually changed: nudging a stranger on
                // being blocked would tell them somebody blocked them.
                FriendList before = await _friends.ReadAsync(playerId, ct).ConfigureAwait(false);
                bool related = before.Friends.Concat(before.Incoming).Concat(before.Outgoing)
                    .Any(entry => entry.PlayerId == target);

                if (await _friends.BlockAsync(playerId, target, _limits, ct).ConfigureAwait(false) == FriendBlockOutcome.AtCapacity)
                    return new FriendsResult(FriendsStatus.AtCapacity);

                if (related) await ChangedAsync(ct, playerId, target).ConfigureAwait(false);
            }

            return await DoneAsync(FriendsStatus.Blocked, playerId, ct).ConfigureAwait(false);
        }

        /// <summary><c>POST /friends/unblock {code}</c>. Idempotent.</summary>
        public async Task<FriendsResult> UnblockAsync(string? bearerToken, string? code, CancellationToken ct = default)
        {
            var (refusal, playerId, target) = await ResolveTargetAsync(bearerToken, code, limited: false, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;
            if (target != null) await _friends.UnblockAsync(playerId, target, ct).ConfigureAwait(false);
            return await DoneAsync(FriendsStatus.Unblocked, playerId, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// <c>PUT /friends/name {displayName}</c> — the name this player's friends see.
        ///
        /// <para>Client-authored text bound for another player's screen, so it passes the SAME
        /// <see cref="PlayerName.Check"/> the match nameplate's does (VULN-002) and the normalized
        /// form is what is stored. Unlike <c>MatchGate</c>, a refused name is an error rather than a
        /// substitution: there is a text field on the other end of this call that can say why.</para>
        /// </summary>
        public async Task<FriendsResult> SetNameAsync(string? bearerToken, string? displayName, CancellationToken ct = default)
        {
            var (refusal, playerId) = await ResolveAsync(bearerToken, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;

            NameCheck check = PlayerName.Check(displayName!);
            if (!check.IsValid) return new FriendsResult(FriendsStatus.InvalidName);

            await _friends.SetDisplayNameAsync(playerId, check.Normalized, ct).ConfigureAwait(false);
            return await DoneAsync(FriendsStatus.Named, playerId, ct).ConfigureAwait(false);
        }

        private Task ChangedAsync(CancellationToken ct, params string[] playerIds)
            => _hub is null ? Task.CompletedTask : _hub.GraphChangedAsync(playerIds, ct);

        private async Task<FriendsResult> DoneAsync(FriendsStatus status, string playerId, CancellationToken ct)
            => new FriendsResult(status, await _friends.ReadAsync(playerId, ct).ConfigureAwait(false));

        private async Task<(FriendsResult? Refusal, string PlayerId)> ResolveAsync(
            string? bearerToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || _verifier is null)
                return (new FriendsResult(FriendsStatus.Unauthenticated), "");

            var verified = await _verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return (new FriendsResult(FriendsStatus.Unauthenticated), "");

            if (_accounts is null)
                return (new FriendsResult(FriendsStatus.Unavailable), "");

            var identity = verified.Identity!;
            Account account = await _accounts.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);
            return (null, account.PlayerId);
        }

        /// <summary>The caller, and the holder of <paramref name="code"/> — null when nobody
        /// holds it, which each verb answers exactly as it answers "nothing to do".</summary>
        private async Task<(FriendsResult? Refusal, string PlayerId, string? Target)> ResolveTargetAsync(
            string? bearerToken, string? code, bool limited, CancellationToken ct)
        {
            var (refusal, playerId) = await ResolveAsync(bearerToken, ct).ConfigureAwait(false);
            if (refusal != null) return (refusal, "", null);

            if (limited && !_limiter.TryTake(playerId))
                return (new FriendsResult(FriendsStatus.RateLimited), "", null);
            if (!FriendCode.TryNormalize(code, out string canonical))
                return (new FriendsResult(FriendsStatus.InvalidCode), "", null);

            string? target = await _friends.FindPlayerIdByCodeAsync(canonical, ct).ConfigureAwait(false);
            return (null, playerId, target);
        }
    }
}
