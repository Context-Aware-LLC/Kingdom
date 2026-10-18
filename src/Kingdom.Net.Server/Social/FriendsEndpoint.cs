using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Social
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

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store. 503 — never readable as "no friends".</summary>
        Unavailable,

        /// <summary>The code is not well-formed (wrong length, alphabet or check symbol). 400.
        /// Safe to say: the client can compute this itself, so it discloses nothing.</summary>
        InvalidCode,

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
            FriendsStatus.NotFound => 404,
            FriendsStatus.AtCapacity => 409,
            FriendsStatus.RateLimited => 429,
            _ => 200,
        };
    }

    /// <summary>What a <c>/play-games</c> route answers with. The lower-cased name is the wire
    /// <c>status</c> string.</summary>
    public enum PlayGamesFriendsStatus
    {
        /// <summary><c>POST /friends/play-games</c> listed the suggestions. Empty is still Known.</summary>
        Known,

        /// <summary><c>POST /play-games/link</c> stored the caller's Play Games id.</summary>
        Linked,

        Unauthenticated,

        /// <summary>No Game server credential or no account store on this box. 503.</summary>
        Unavailable,

        /// <summary>No auth code, or Google refused it. 400.</summary>
        InvalidCode,

        /// <summary>The player has not shared their Play Games friends with this game. 403.</summary>
        FriendsHidden,

        /// <summary>Google did not answer. 502.</summary>
        Upstream,

        RateLimited,
    }

    /// <summary>A Play Games friend who also plays, as the client is shown them.</summary>
    /// <param name="Relation"><c>none</c>, <c>outgoing</c> (the caller has asked them) or
    /// <c>incoming</c> (they have asked the caller).</param>
    public sealed record PlayGamesSuggestion(string Code, string DisplayName, string PlayGamesId, string Relation);

    public sealed class PlayGamesFriendsResult
    {
        public PlayGamesFriendsStatus Status { get; }

        /// <summary>Non-null only on Known.</summary>
        public IReadOnlyList<PlayGamesSuggestion>? Suggestions { get; }

        public PlayGamesFriendsResult(PlayGamesFriendsStatus status, IReadOnlyList<PlayGamesSuggestion>? suggestions = null)
        {
            Status = status;
            Suggestions = suggestions;
        }

        public int HttpStatus => Status switch
        {
            PlayGamesFriendsStatus.Unauthenticated => 401,
            PlayGamesFriendsStatus.Unavailable => 503,
            PlayGamesFriendsStatus.InvalidCode => 400,
            PlayGamesFriendsStatus.FriendsHidden => 403,
            PlayGamesFriendsStatus.Upstream => 502,
            PlayGamesFriendsStatus.RateLimited => 429,
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
            new Dictionary<string, (long, int)>(StringComparer.Ordinal);

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
        private readonly IPlayGamesApi? _playGames;
        private readonly IPlayGamesLinkStore? _playGamesLinks;
        private readonly FriendRequestLimiter? _playGamesLimiter;

        /// <param name="hub">Told whenever a write changes somebody ELSE's view of the graph, so
        /// a connected player hears about a request or a removal without polling. Null on a box
        /// with no social socket; the routes are complete without it.</param>
        /// <param name="playGames">Google's side of Play Games (#423). Null turns the
        /// <c>/play-games</c> routes off: they answer Unavailable.</param>
        /// <param name="playGamesLimiter">Caps the Play Games routes per account: each one costs
        /// several calls to Google.</param>
        public FriendsEndpoint(
            IIdentityTokenVerifier? verifier,
            IAccountStore? accounts,
            IFriendStore friends,
            FriendLimits limits,
            FriendRequestLimiter limiter,
            SocialHub? hub = null,
            IPlayGamesApi? playGames = null,
            IPlayGamesLinkStore? playGamesLinks = null,
            FriendRequestLimiter? playGamesLimiter = null)
        {
            _verifier = verifier;
            _accounts = accounts;
            _friends = friends;
            _limits = limits;
            _limiter = limiter;
            _hub = hub;
            _playGames = playGames;
            _playGamesLinks = playGamesLinks;
            _playGamesLimiter = playGamesLimiter;
        }

        /// <summary>
        /// <c>POST /play-games/link {authCode}</c>: store the caller's Play Games id, so their Play
        /// Games friends can find them. The client calls it once a launch, after Play Games signs in.
        /// The id is read from Google with the server's own credential; the client sends only a
        /// single-use auth code, so nobody can link an id they do not hold.
        /// </summary>
        public async Task<PlayGamesFriendsResult> LinkPlayGamesAsync(
            string? bearerToken, string? authCode, CancellationToken ct = default)
        {
            var (refusal, _, _) = await OpenPlayGamesAsync(bearerToken, authCode, withFriends: false, ct)
                .ConfigureAwait(false);
            return refusal ?? new PlayGamesFriendsResult(PlayGamesFriendsStatus.Linked);
        }

        /// <summary>
        /// <c>POST /friends/play-games {authCode}</c>: the caller's Play Games friends who play Bad
        /// Ideas, each to be added with an ordinary friend request (#423).
        ///
        /// <para><b>The friends list is never stored.</b> Google's terms keep friends data, derived
        /// data included, 30 days at most, so it is fetched on every call and dropped with the
        /// response. Only the caller's own id is kept.</para>
        ///
        /// <para>Left out: the caller, existing friends, and anyone blocked by either side, so a
        /// block stays invisible to the blocked player as it is everywhere else.</para>
        /// </summary>
        public async Task<PlayGamesFriendsResult> FindPlayGamesFriendsAsync(
            string? bearerToken, string? authCode, CancellationToken ct = default)
        {
            var (refusal, playerId, session) = await OpenPlayGamesAsync(bearerToken, authCode, withFriends: true, ct)
                .ConfigureAwait(false);
            if (refusal != null) return refusal;

            IReadOnlyDictionary<string, string> found = await _playGamesLinks!
                .FindAsync(session!.FriendIds!, ct).ConfigureAwait(false);
            FriendList mine = await _friends.ReadAsync(playerId, ct).ConfigureAwait(false);
            static bool Has(IReadOnlyList<FriendIdentity> list, string id) => list.Any(e => e.PlayerId == id);

            var suggestions = new List<PlayGamesSuggestion>();
            foreach (var (playGamesId, target) in found)
            {
                if (target == playerId || Has(mine.Friends, target) || Has(mine.Blocked, target)) continue;
                FriendList theirs = await _friends.ReadAsync(target, ct).ConfigureAwait(false);
                if (Has(theirs.Blocked, playerId)) continue;

                string relation = Has(mine.Outgoing, target) ? "outgoing"
                    : Has(mine.Incoming, target) ? "incoming" : "none";
                suggestions.Add(new PlayGamesSuggestion(
                    theirs.Self.Code, theirs.Self.DisplayName ?? "", playGamesId, relation));
            }

            suggestions.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Known, suggestions);
        }

        /// <summary>Authenticate, rate-limit, open the auth code with Google and link the id it
        /// names. A refusal, or the caller and their Google session.</summary>
        private async Task<(PlayGamesFriendsResult? Refusal, string PlayerId, PlayGamesSession? Session)> OpenPlayGamesAsync(
            string? bearerToken, string? authCode, bool withFriends, CancellationToken ct)
        {
            var (refusal, playerId) = await ResolveAsync(bearerToken, ct).ConfigureAwait(false);
            if (refusal != null)
                return (new PlayGamesFriendsResult(refusal.Status == FriendsStatus.Unauthenticated
                    ? PlayGamesFriendsStatus.Unauthenticated : PlayGamesFriendsStatus.Unavailable), "", null);
            if (_playGames is null || _playGamesLinks is null)
                return (new PlayGamesFriendsResult(PlayGamesFriendsStatus.Unavailable), "", null);
            if (_playGamesLimiter != null && !_playGamesLimiter.TryTake(playerId))
                return (new PlayGamesFriendsResult(PlayGamesFriendsStatus.RateLimited), "", null);
            if (string.IsNullOrWhiteSpace(authCode))
                return (new PlayGamesFriendsResult(PlayGamesFriendsStatus.InvalidCode), "", null);

            PlayGamesSession session = await _playGames.OpenAsync(authCode!, withFriends, ct).ConfigureAwait(false);
            if (session.PlayerId is string playGamesId)
                await _playGamesLinks.LinkAsync(playerId, playGamesId, ct).ConfigureAwait(false);

            PlayGamesFriendsStatus? failed = session.Status switch
            {
                PlayGamesCallStatus.Ok => null,
                PlayGamesCallStatus.CodeRefused => PlayGamesFriendsStatus.InvalidCode,
                PlayGamesCallStatus.FriendsHidden => PlayGamesFriendsStatus.FriendsHidden,
                _ => PlayGamesFriendsStatus.Upstream,
            };
            return failed is { } status
                ? (new PlayGamesFriendsResult(status), "", null)
                : (null, playerId, session);
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
            await SyncNameAsync(account.PlayerId, identity.DisplayName, ct).ConfigureAwait(false);
            return (null, account.PlayerId);
        }

        /// <summary>
        /// The name a player's friends see is the one they gave themselves at sign-up, and nothing
        /// else (Sam, 2026-10-02) — so there is no route that sets it. It arrives in the token on
        /// every call and is copied into the store here whenever it differs.
        ///
        /// <para>It is still text another player typed, bound for somebody else's screen, so it
        /// passes the SAME <see cref="PlayerName.Check"/> the match nameplate's does (VULN-002)
        /// and the normalized form is what is stored. A name that fails is skipped, not an error:
        /// the stored one stands, and a player with none is shown by their code.</para>
        /// </summary>
        private async Task SyncNameAsync(string playerId, string tokenName, CancellationToken ct)
        {
            NameCheck check = PlayerName.Check(tokenName);
            if (!check.IsValid) return;

            FriendIdentity stored = await _friends.EnsureIdentityAsync(playerId, ct).ConfigureAwait(false);
            if (!string.Equals(stored.DisplayName, check.Normalized, StringComparison.Ordinal))
                await _friends.SetDisplayNameAsync(playerId, check.Normalized, ct).ConfigureAwait(false);
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
