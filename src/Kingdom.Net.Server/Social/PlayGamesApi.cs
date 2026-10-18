using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Server.Social
{
    /// <summary>How a call to Google ended.</summary>
    public enum PlayGamesCallStatus
    {
        Ok,

        /// <summary>Google refused the auth code: spent, expired, or minted for another client.</summary>
        CodeRefused,

        /// <summary>The player has not let this game see their Play Games friends.</summary>
        FriendsHidden,

        /// <summary>Google did not answer, or answered something unreadable.</summary>
        Unreachable,
    }

    /// <summary>What one auth code opened: the player's own id and their friends who play.</summary>
    /// <param name="FriendIds">Null unless asked for and <see cref="Status"/> is Ok.</param>
    public sealed record PlayGamesSession(
        PlayGamesCallStatus Status, string? PlayerId, IReadOnlyList<string>? FriendIds);

    /// <summary>
    /// Google's side of Play Games (#423). One call per auth code, because a code is single-use:
    /// it is exchanged, the player's id read, and their friends listed if asked, all on the one
    /// access token, which is then dropped.
    /// </summary>
    public interface IPlayGamesApi
    {
        Task<PlayGamesSession> OpenAsync(string authCode, bool withFriends, CancellationToken ct = default);
    }

    /// <summary>
    /// The REST calls behind <see cref="IPlayGamesApi"/>, made with the Play Console's "Game server"
    /// OAuth client. Google's own guidance is the reason this runs here and not on the phone: a
    /// server uses the REST API "rather than passing the result of the Android API", so neither the
    /// player's id nor their friends ever come from the client.
    /// </summary>
    public sealed class GooglePlayGamesApi : IPlayGamesApi
    {
        /// <summary>The environment variables that hold the Game server credential.</summary>
        public const string ClientIdEnvVar = "KINGDOM_PLAY_GAMES_CLIENT_ID";
        public const string ClientSecretEnvVar = "KINGDOM_PLAY_GAMES_CLIENT_SECRET";

        /// <summary>Pages of 50; four of them is more Play Games friends than the friends cap.</summary>
        private const int FriendPageSize = 50;
        private const int MaxFriendPages = 4;

        private readonly HttpClient _http;
        private readonly string _clientId;
        private readonly string _clientSecret;

        public GooglePlayGamesApi(HttpClient http, string clientId, string clientSecret)
        {
            _http = http;
            _clientId = clientId;
            _clientSecret = clientSecret;
        }

        /// <summary>Null when the credential is not configured, which turns the routes off.</summary>
        public static GooglePlayGamesApi? FromEnvironment()
        {
            string? id = KingdomEnv.Get(ClientIdEnvVar);
            string? secret = KingdomEnv.Get(ClientSecretEnvVar);
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return null;
            return new GooglePlayGamesApi(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, id!, secret!);
        }

        public async Task<PlayGamesSession> OpenAsync(string authCode, bool withFriends, CancellationToken ct = default)
        {
            try
            {
                string? token = await ExchangeAsync(authCode, ct).ConfigureAwait(false);
                if (token is null) return new PlayGamesSession(PlayGamesCallStatus.CodeRefused, null, null);

                using JsonDocument? me = await GetAsync("https://games.googleapis.com/games/v1/players/me", token, ct).ConfigureAwait(false);
                if (me is null || !TryString(me.RootElement, "playerId", out string playerId))
                    return new PlayGamesSession(PlayGamesCallStatus.Unreachable, null, null);
                if (!withFriends) return new PlayGamesSession(PlayGamesCallStatus.Ok, playerId, null);

                var friends = new List<string>();
                string? pageToken = null;
                for (int page = 0; page < MaxFriendPages; page++)
                {
                    string url = "https://games.googleapis.com/games/v1/players/me/players/FRIENDS_ALL?maxResults="
                                 + FriendPageSize
                                 + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                    // Google answers 403 when the player has not shared their friends list.
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                        return new PlayGamesSession(PlayGamesCallStatus.FriendsHidden, playerId, null);
                    if (!response.IsSuccessStatusCode)
                        return new PlayGamesSession(PlayGamesCallStatus.Unreachable, playerId, null);

                    using JsonDocument list = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (list.RootElement.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement item in items.EnumerateArray())
                            if (TryString(item, "playerId", out string friend)) friends.Add(friend);

                    if (!TryString(list.RootElement, "nextPageToken", out string next)) break;
                    pageToken = next;
                }

                return new PlayGamesSession(PlayGamesCallStatus.Ok, playerId, friends);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine("[play-games] Google call failed: {0}", ex.GetType().Name);
                return new PlayGamesSession(PlayGamesCallStatus.Unreachable, null, null);
            }
        }

        /// <summary>The access token for <paramref name="authCode"/>, or null when Google refused it.
        /// The redirect URI is empty: a code minted by the Play Games SDK carries none.</summary>
        private async Task<string?> ExchangeAsync(string authCode, CancellationToken ct)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = authCode,
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["redirect_uri"] = "",
            });
            using HttpResponseMessage response = await _http.PostAsync("https://oauth2.googleapis.com/token", form, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.BadRequest) return null;
            response.EnsureSuccessStatusCode();
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return TryString(body.RootElement, "access_token", out string token) ? token : null;
        }

        private async Task<JsonDocument?> GetAsync(string url, string token, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        private static bool TryString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement e)
                || e.ValueKind != JsonValueKind.String) return false;
            value = e.GetString() ?? "";
            return value.Length > 0;
        }
    }
}
