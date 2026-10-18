using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Client.Auth
{
    public enum PlayGamesRestoreStatus
    {
        /// <summary>The profile is linked to an account; <see cref="PlayGamesRestore.TokenHash"/>
        /// signs in to it.</summary>
        Restored,

        /// <summary>No account holds this Play Games profile: a new player, or one who has never
        /// played signed in to Play Games.</summary>
        NotLinked,

        /// <summary>Google refused the auth code.</summary>
        Refused,
        RateLimited,
        Unavailable,
        Offline,
        Malformed,
    }

    public sealed class PlayGamesRestore
    {
        public PlayGamesRestoreStatus Status { get; }

        /// <summary>The single-use token Supabase redeems for the session; empty unless restored.</summary>
        public string TokenHash { get; }

        /// <summary>The kind of link the token was minted as, which Supabase's <c>verify</c> needs.</summary>
        public string TokenType { get; }

        private PlayGamesRestore(PlayGamesRestoreStatus status, string tokenHash, string tokenType)
        {
            Status = status;
            TokenHash = tokenHash;
            TokenType = tokenType;
        }

        public static PlayGamesRestore Restored(string tokenHash, string tokenType)
            => new PlayGamesRestore(PlayGamesRestoreStatus.Restored, tokenHash, tokenType);

        public static PlayGamesRestore Failed(PlayGamesRestoreStatus status)
            => new PlayGamesRestore(status, "", "");
    }

    /// <summary>
    /// <c>POST /play-games/restore</c> (#425): a device with no session hands the game server a Play
    /// Games auth code, and gets back a token for the account that profile is linked to. Asked before
    /// sign-in, so it carries no bearer. Every failure comes back as data, never as an exception, and
    /// every one of them means the same to the caller: show the sign-in screen.
    /// </summary>
    public sealed class PlayGamesRestoreGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public PlayGamesRestoreGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public async Task<PlayGamesRestore> RestoreAsync(string authCode, CancellationToken cancellationToken = default)
        {
            if (_baseUrl.Length == 0) return PlayGamesRestore.Failed(PlayGamesRestoreStatus.Unavailable);
            if (string.IsNullOrEmpty(authCode)) return PlayGamesRestore.Failed(PlayGamesRestoreStatus.Refused);

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            AuthHttpResponse response = await _http
                .PostJsonAsync(_baseUrl + "/play-games/restore",
                    AuthJson.Object(AuthJson.Field("authCode", authCode)), headers, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsTransportFailure) return PlayGamesRestore.Failed(PlayGamesRestoreStatus.Offline);

            // The status field decides, on every code: a 404 "notlinked" and a 503 "unavailable"
            // both carry one.
            if (!AuthJson.TryReadTopLevel(response.Body, out var fields) ||
                !AuthJson.TryString(fields, "status", out string status))
                return PlayGamesRestore.Failed(response.IsSuccess
                    ? PlayGamesRestoreStatus.Malformed
                    : PlayGamesRestoreStatus.Unavailable);

            switch (status.ToLowerInvariant())
            {
                case "restored":
                    return AuthJson.TryString(fields, "tokenHash", out string hash) &&
                           AuthJson.TryString(fields, "tokenType", out string type)
                        ? PlayGamesRestore.Restored(hash, type)
                        : PlayGamesRestore.Failed(PlayGamesRestoreStatus.Malformed);
                case "notlinked": return PlayGamesRestore.Failed(PlayGamesRestoreStatus.NotLinked);
                case "refused": return PlayGamesRestore.Failed(PlayGamesRestoreStatus.Refused);
                case "ratelimited": return PlayGamesRestore.Failed(PlayGamesRestoreStatus.RateLimited);
                case "unavailable": return PlayGamesRestore.Failed(PlayGamesRestoreStatus.Unavailable);
                default: return PlayGamesRestore.Failed(PlayGamesRestoreStatus.Malformed);
            }
        }
    }
}
