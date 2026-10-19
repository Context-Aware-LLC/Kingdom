using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Achievements
{
    public enum AchievementFetchStatus
    {
        Known,
        Offline,
        Unauthenticated,
        Unavailable,
        Malformed,
    }

    /// <summary>One achievement the server says the player has earned.</summary>
    public sealed class EarnedAchievementEntry
    {
        public string Id { get; }

        /// <summary>The id to unlock in Play Games. Empty while the achievement has no Play Console
        /// entry; such an entry is earned and simply has nothing to unlock yet.</summary>
        public string PlayGamesId { get; }

        /// <summary>When it was first earned, for the Ledger's date. Null when the row carried no
        /// readable date, which leaves the page earned and simply undated.</summary>
        public DateTimeOffset? EarnedAt { get; }

        public EarnedAchievementEntry(string id, string playGamesId, DateTimeOffset? earnedAt = null)
        {
            Id = id;
            PlayGamesId = playGamesId;
            EarnedAt = earnedAt;
        }
    }

    public sealed class AchievementSnapshot
    {
        private static readonly IReadOnlyList<EarnedAchievementEntry> NoAchievements =
            Array.Empty<EarnedAchievementEntry>();

        public AchievementFetchStatus Status { get; }
        public IReadOnlyList<EarnedAchievementEntry> Achievements { get; }
        public string Detail { get; }

        private AchievementSnapshot(
            AchievementFetchStatus status,
            IReadOnlyList<EarnedAchievementEntry>? achievements,
            string detail)
        {
            Status = status;
            Achievements = achievements ?? NoAchievements;
            Detail = detail ?? "";
        }

        public static AchievementSnapshot Known(IReadOnlyList<EarnedAchievementEntry> achievements)
            => new AchievementSnapshot(AchievementFetchStatus.Known, achievements, "");

        public static AchievementSnapshot Failed(AchievementFetchStatus status, string detail)
            => new AchievementSnapshot(status, null, detail);

        /// <summary>
        /// The Play Games ids still to unlock: every earned achievement that has one and is not in
        /// <paramref name="alreadyUnlocked"/>, each once. Empty unless the read succeeded, so a failed
        /// read can never be mistaken for "nothing earned".
        /// </summary>
        public IReadOnlyList<string> PlayGamesIdsToUnlock(ICollection<string> alreadyUnlocked)
        {
            if (alreadyUnlocked is null) throw new ArgumentNullException(nameof(alreadyUnlocked));
            var pending = new List<string>();
            if (Status != AchievementFetchStatus.Known) return pending;
            foreach (EarnedAchievementEntry entry in Achievements)
            {
                if (entry.PlayGamesId.Length == 0) continue;
                if (alreadyUnlocked.Contains(entry.PlayGamesId) || pending.Contains(entry.PlayGamesId)) continue;
                pending.Add(entry.PlayGamesId);
            }
            return pending;
        }
    }

    /// <summary>
    /// Read-only client for <c>GET /achievements</c>. The server decides what was earned; the client
    /// only copies that to Play Games. Every failure comes back as data, never as an exception.
    /// </summary>
    public sealed class HttpAchievementGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public HttpAchievementGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public bool IsConfigured => _baseUrl.Length > 0;

        public async Task<AchievementSnapshot> FetchAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return AchievementSnapshot.Failed(
                    AchievementFetchStatus.Unavailable, "No achievement endpoint is configured.");
            if (string.IsNullOrEmpty(accessToken))
                return AchievementSnapshot.Failed(AchievementFetchStatus.Unauthenticated, "Not signed in.");

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };

            AuthHttpResponse response = await _http
                .GetAsync(_baseUrl + "/achievements", headers, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsTransportFailure)
                return AchievementSnapshot.Failed(AchievementFetchStatus.Offline, response.Body);
            if (response.StatusCode == 401)
                return AchievementSnapshot.Failed(AchievementFetchStatus.Unauthenticated, response.Body);
            if (response.StatusCode == 503)
                return AchievementSnapshot.Failed(AchievementFetchStatus.Unavailable, response.Body);
            if (!response.IsSuccess)
                return AchievementSnapshot.Failed(
                    AchievementFetchStatus.Offline,
                    "Server returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture));

            return Parse(response.Body);
        }

        private static AchievementSnapshot Parse(string body)
        {
            if (!AuthJson.TryReadTopLevel(body, out var fields) ||
                !AuthJson.TryString(fields, "status", out string status))
                return AchievementSnapshot.Failed(AchievementFetchStatus.Malformed, "No achievement status.");

            if (status.Equals("unauthenticated", StringComparison.OrdinalIgnoreCase))
                return AchievementSnapshot.Failed(AchievementFetchStatus.Unauthenticated, "Not signed in.");
            if (status.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
                return AchievementSnapshot.Failed(AchievementFetchStatus.Unavailable, "Achievements unavailable.");
            if (!status.Equals("known", StringComparison.OrdinalIgnoreCase))
                return AchievementSnapshot.Failed(AchievementFetchStatus.Malformed, "Unknown achievement status.");
            if (!AuthJson.TryReadObjectArray(body, "achievements", out var rows))
                return AchievementSnapshot.Failed(AchievementFetchStatus.Malformed, "No achievement list.");

            var achievements = new List<EarnedAchievementEntry>(rows.Count);
            foreach (IReadOnlyDictionary<string, string> row in rows)
            {
                // playGamesId is read raw: TryString refuses an empty value, and empty is a real
                // answer here (no console entry yet). Missing altogether is still malformed.
                if (!AuthJson.TryString(row, "id", out string id) ||
                    !row.TryGetValue("playGamesId", out string? playGamesId) || playGamesId is null)
                    return AchievementSnapshot.Failed(AchievementFetchStatus.Malformed, "Unreadable achievement row.");
                DateTimeOffset? earnedAt = null;
                if (row.TryGetValue("earnedAt", out string? stamp) &&
                    DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                    earnedAt = parsed;
                achievements.Add(new EarnedAchievementEntry(id, playGamesId, earnedAt));
            }
            return AchievementSnapshot.Known(achievements);
        }
    }
}
