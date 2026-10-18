using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Social
{
    /// <summary>What a <c>/friends</c> call came to. The caller branches on this and never on a
    /// list being empty: "no friends" and "could not find out" are different answers.</summary>
    public enum FriendsCallStatus
    {
        /// <summary>The call landed and <see cref="FriendsSnapshot"/>'s lists are the player's
        /// graph as it now stands. For a request this means the server TOOK it — it answers the
        /// same whether or not the code belongs to anyone, by design.</summary>
        Ok,

        /// <summary>Nothing came back. Transient; the lists are not an answer.</summary>
        Offline,
        Unauthenticated,
        Unavailable,

        /// <summary>The code is not well-formed. <c>FriendCode.TryNormalize</c> catches this
        /// locally, so reaching the server with one is a client bug rather than a typo.</summary>
        InvalidCode,

        /// <summary>Accept only: no request from that code is waiting.</summary>
        NotFound,

        /// <summary>One of this player's own caps is full.</summary>
        AtCapacity,
        RateLimited,

        /// <summary>A 200 this build cannot read. Never treated as an empty list.</summary>
        Malformed,
    }

    /// <summary>Another player as the friends list shows them. <see cref="DisplayName"/> is their
    /// sign-up name — there is no separate friends name — and is empty when the server holds none, and always empty on outgoing requests and blocks, which the
    /// server sends by code alone.
    ///
    /// <para><b><see cref="DisplayName"/> is text another player typed.</b> Whatever draws it owes
    /// what the match nameplate owes: rich text off, and a clip in pixels.</para></summary>
    public sealed class FriendEntry
    {
        public string Code { get; }
        public string DisplayName { get; }

        public FriendEntry(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName ?? "";
        }
    }

    /// <summary>The player's friends graph, or why it could not be read.</summary>
    public sealed class FriendsSnapshot
    {
        private static readonly IReadOnlyList<FriendEntry> None = Array.Empty<FriendEntry>();

        public FriendsCallStatus Status { get; }

        /// <summary>This player's own friend code, canonical form. Empty unless
        /// <see cref="Status"/> is <see cref="FriendsCallStatus.Ok"/>.</summary>
        public string Code { get; }
        public string DisplayName { get; }
        public IReadOnlyList<FriendEntry> Friends { get; }
        public IReadOnlyList<FriendEntry> Incoming { get; }
        public IReadOnlyList<FriendEntry> Outgoing { get; }
        public IReadOnlyList<FriendEntry> Blocked { get; }

        private FriendsSnapshot(FriendsCallStatus status, string code, string displayName,
            IReadOnlyList<FriendEntry>? friends, IReadOnlyList<FriendEntry>? incoming,
            IReadOnlyList<FriendEntry>? outgoing, IReadOnlyList<FriendEntry>? blocked)
        {
            Status = status;
            Code = code;
            DisplayName = displayName;
            Friends = friends ?? None;
            Incoming = incoming ?? None;
            Outgoing = outgoing ?? None;
            Blocked = blocked ?? None;
        }

        public static FriendsSnapshot Known(string code, string displayName,
            IReadOnlyList<FriendEntry> friends, IReadOnlyList<FriendEntry> incoming,
            IReadOnlyList<FriendEntry> outgoing, IReadOnlyList<FriendEntry> blocked)
            => new FriendsSnapshot(FriendsCallStatus.Ok, code, displayName, friends, incoming, outgoing, blocked);

        public static FriendsSnapshot Failed(FriendsCallStatus status)
            => new FriendsSnapshot(status, "", "", null, null, null, null);
    }

    /// <summary>
    /// Client for the server's <c>/friends</c> routes (#409). Every call returns the player's whole
    /// graph as the server now holds it, so the screen redraws from the answer rather than patching
    /// a local copy — there is no local copy, and no offline one (the game requires a connection).
    ///
    /// <para>Never throws: every failure is a <see cref="FriendsCallStatus"/>, because the caller
    /// is a screen that has to say something.</para>
    /// </summary>
    public sealed class HttpFriendsGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public HttpFriendsGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public bool IsConfigured => _baseUrl.Length > 0;

        public Task<FriendsSnapshot> FetchAsync(string accessToken, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends", null, null, ct);

        public Task<FriendsSnapshot> RequestAsync(string accessToken, string code, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends/request", "code", code, ct);

        public Task<FriendsSnapshot> AcceptAsync(string accessToken, string code, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends/accept", "code", code, ct);

        /// <summary>Refuse a request, or withdraw one this player sent.</summary>
        public Task<FriendsSnapshot> DeclineAsync(string accessToken, string code, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends/decline", "code", code, ct);

        public Task<FriendsSnapshot> RemoveAsync(string accessToken, string code, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends/remove", "code", code, ct);

        public Task<FriendsSnapshot> BlockAsync(string accessToken, string code, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends/block", "code", code, ct);

        public Task<FriendsSnapshot> UnblockAsync(string accessToken, string code, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends/unblock", "code", code, ct);

        private async Task<FriendsSnapshot> CallAsync(
            string accessToken, string path, string? field, string? value, CancellationToken ct)
        {
            if (!IsConfigured) return FriendsSnapshot.Failed(FriendsCallStatus.Unavailable);
            if (string.IsNullOrEmpty(accessToken)) return FriendsSnapshot.Failed(FriendsCallStatus.Unauthenticated);

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };

            AuthHttpResponse response;
            if (field is null)
                response = await _http.GetAsync(_baseUrl + path, headers, ct).ConfigureAwait(false);
            else
            {
                string body = AuthJson.Object(AuthJson.Field(field, value ?? ""));
                response = await _http.PostJsonAsync(_baseUrl + path, body, headers, ct).ConfigureAwait(false);
            }

            if (response.IsTransportFailure) return FriendsSnapshot.Failed(FriendsCallStatus.Offline);
            if (!response.IsSuccess) return FriendsSnapshot.Failed(Refusal(response));
            return Parse(response.Body);
        }

        /// <summary>The body's <c>status</c> string wins where it is readable; the status code
        /// is the fallback for a refusal that never reached the route.</summary>
        private static FriendsCallStatus Refusal(AuthHttpResponse response)
        {
            if (AuthJson.TryReadTopLevel(response.Body, out var fields) &&
                AuthJson.TryString(fields, "status", out string status))
            {
                switch (status.ToLowerInvariant())
                {
                    case "unauthenticated": return FriendsCallStatus.Unauthenticated;
                    case "unavailable": return FriendsCallStatus.Unavailable;
                    case "invalidcode": return FriendsCallStatus.InvalidCode;
                    case "notfound": return FriendsCallStatus.NotFound;
                    case "atcapacity": return FriendsCallStatus.AtCapacity;
                    case "ratelimited": return FriendsCallStatus.RateLimited;
                }
            }

            switch (response.StatusCode)
            {
                case 401: return FriendsCallStatus.Unauthenticated;
                case 404: return FriendsCallStatus.NotFound;
                case 409: return FriendsCallStatus.AtCapacity;
                case 429: return FriendsCallStatus.RateLimited;
                case 503: return FriendsCallStatus.Unavailable;
                default: return FriendsCallStatus.Offline;
            }
        }

        private static FriendsSnapshot Parse(string body)
        {
            // All four lists must be present and readable. A 200 missing one is a server this
            // build does not understand, and showing the other three would present a partial
            // graph as the whole one.
            if (!AuthJson.TryReadTopLevel(body, out var fields) ||
                !AuthJson.TryString(fields, "code", out string code) ||
                !TryList(body, "friends", out var friends) ||
                !TryList(body, "incoming", out var incoming) ||
                !TryList(body, "outgoing", out var outgoing) ||
                !TryList(body, "blocked", out var blocked))
                return FriendsSnapshot.Failed(FriendsCallStatus.Malformed);

            AuthJson.TryString(fields, "displayName", out string displayName);
            return FriendsSnapshot.Known(code, displayName, friends, incoming, outgoing, blocked);
        }

        private static bool TryList(string body, string name, out IReadOnlyList<FriendEntry> entries)
        {
            var list = new List<FriendEntry>();
            entries = list;
            if (!AuthJson.TryReadObjectArray(body, name, out var rows)) return false;

            foreach (IReadOnlyDictionary<string, string> row in rows)
            {
                if (!AuthJson.TryString(row, "code", out string code)) return false;
                AuthJson.TryString(row, "displayName", out string displayName);
                list.Add(new FriendEntry(code, displayName));
            }
            return true;
        }
    }
}

namespace Kingdom.Net.Client.Social
{
    /// <summary>What a <c>/play-games</c> call came to (#423).</summary>
    public enum PlayGamesFriendsStatus
    {
        Ok,
        Offline,
        Unauthenticated,

        /// <summary>The server has no Game server credential, so Play Games is off there.</summary>
        Unavailable,

        /// <summary>Google refused the auth code. Asking the SDK for a fresh one may work.</summary>
        InvalidCode,

        /// <summary>The player has not shared their Play Games friends with this game.</summary>
        FriendsHidden,

        /// <summary>The server could not reach Google.</summary>
        Upstream,
        RateLimited,
        Malformed,
    }

    /// <summary>A Play Games friend who plays Bad Ideas. <see cref="DisplayName"/> is text another
    /// player typed: rich text off and a clip in pixels wherever it is drawn.</summary>
    public sealed class PlayGamesSuggestion
    {
        public string Code { get; }
        public string DisplayName { get; }

        /// <summary>Their Play Games player id, for opening their Play Games profile.</summary>
        public string PlayGamesId { get; }

        /// <summary>This player has already asked them.</summary>
        public bool Requested { get; }

        /// <summary>They have asked this player, so Add accepts.</summary>
        public bool AskedYou { get; }

        public PlayGamesSuggestion(string code, string displayName, string playGamesId, bool requested, bool askedYou)
        {
            Code = code;
            DisplayName = displayName ?? "";
            PlayGamesId = playGamesId ?? "";
            Requested = requested;
            AskedYou = askedYou;
        }
    }

    public sealed class PlayGamesFriendsResult
    {
        public PlayGamesFriendsStatus Status { get; }

        /// <summary>Empty unless <see cref="Status"/> is Ok.</summary>
        public IReadOnlyList<PlayGamesSuggestion> Suggestions { get; }

        public PlayGamesFriendsResult(PlayGamesFriendsStatus status, IReadOnlyList<PlayGamesSuggestion>? suggestions = null)
        {
            Status = status;
            Suggestions = suggestions ?? Array.Empty<PlayGamesSuggestion>();
        }
    }

    /// <summary>
    /// The server's Play Games routes (#423). Each takes a single-use server auth code from the
    /// Play Games SDK; the server reads ids and friends from Google itself. Never throws.
    /// </summary>
    public sealed class HttpPlayGamesFriendsGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public HttpPlayGamesFriendsGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary><c>POST /play-games/link</c>: make this player findable by their Play Games friends.</summary>
        public async Task<PlayGamesFriendsStatus> LinkAsync(string accessToken, string authCode, CancellationToken ct = default)
            => (await CallAsync(accessToken, "/play-games/link", authCode, ct).ConfigureAwait(false)).Status;

        /// <summary><c>POST /friends/play-games</c>: this player's Play Games friends who play.</summary>
        public Task<PlayGamesFriendsResult> FindAsync(string accessToken, string authCode, CancellationToken ct = default)
            => CallAsync(accessToken, "/friends/play-games", authCode, ct);

        private async Task<PlayGamesFriendsResult> CallAsync(
            string accessToken, string path, string authCode, CancellationToken ct)
        {
            if (_baseUrl.Length == 0) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Unavailable);
            if (string.IsNullOrEmpty(accessToken)) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Unauthenticated);
            if (string.IsNullOrEmpty(authCode)) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.InvalidCode);

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            string body = AuthJson.Object(AuthJson.Field("authCode", authCode));
            AuthHttpResponse response = await _http.PostJsonAsync(_baseUrl + path, body, headers, ct).ConfigureAwait(false);

            if (response.IsTransportFailure) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Offline);
            if (!AuthJson.TryReadTopLevel(response.Body, out var fields) ||
                !AuthJson.TryString(fields, "status", out string status))
                return new PlayGamesFriendsResult(response.IsSuccess ? PlayGamesFriendsStatus.Malformed
                    : response.StatusCode == 401 ? PlayGamesFriendsStatus.Unauthenticated
                    : PlayGamesFriendsStatus.Offline);

            switch (status.ToLowerInvariant())
            {
                case "linked": return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Ok);
                case "known": break;
                case "unauthenticated": return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Unauthenticated);
                case "unavailable": return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Unavailable);
                case "invalidcode": return new PlayGamesFriendsResult(PlayGamesFriendsStatus.InvalidCode);
                case "friendshidden": return new PlayGamesFriendsResult(PlayGamesFriendsStatus.FriendsHidden);
                case "upstream": return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Upstream);
                case "ratelimited": return new PlayGamesFriendsResult(PlayGamesFriendsStatus.RateLimited);
                default: return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Malformed);
            }

            if (!AuthJson.TryReadObjectArray(response.Body, "suggestions", out var rows))
                return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Malformed);

            var suggestions = new List<PlayGamesSuggestion>();
            foreach (IReadOnlyDictionary<string, string> row in rows)
            {
                if (!AuthJson.TryString(row, "code", out string code)) return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Malformed);
                AuthJson.TryString(row, "displayName", out string displayName);
                AuthJson.TryString(row, "playGamesId", out string playGamesId);
                AuthJson.TryString(row, "relation", out string relation);
                suggestions.Add(new PlayGamesSuggestion(code, displayName, playGamesId,
                    relation == "outgoing", relation == "incoming"));
            }
            return new PlayGamesFriendsResult(PlayGamesFriendsStatus.Ok, suggestions);
        }
    }
}
