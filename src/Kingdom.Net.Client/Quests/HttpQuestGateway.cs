using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Quests
{
    public enum QuestFetchStatus
    {
        Known,
        Offline,
        Unauthenticated,
        Unavailable,
        Malformed,
    }

    public sealed class QuestEntry
    {
        public string Id { get; }
        public string Title { get; }
        public int Goal { get; }
        public int Progress { get; }
        public long RewardGold { get; }
        public string Day { get; }
        public bool Complete { get; }

        public QuestEntry(
            string id,
            string title,
            int goal,
            int progress,
            long rewardGold,
            string day,
            bool complete)
        {
            Id = id;
            Title = title;
            Goal = goal;
            Progress = progress;
            RewardGold = rewardGold;
            Day = day;
            Complete = complete;
        }
    }

    public sealed class QuestSnapshot
    {
        private static readonly IReadOnlyList<QuestEntry> NoQuests = Array.Empty<QuestEntry>();

        public QuestFetchStatus Status { get; }
        public IReadOnlyList<QuestEntry> Quests { get; }
        public string Detail { get; }

        private QuestSnapshot(
            QuestFetchStatus status,
            IReadOnlyList<QuestEntry>? quests,
            string detail)
        {
            Status = status;
            Quests = quests ?? NoQuests;
            Detail = detail ?? "";
        }

        public static QuestSnapshot Known(IReadOnlyList<QuestEntry> quests)
            => new QuestSnapshot(QuestFetchStatus.Known, quests, "");

        public static QuestSnapshot Failed(QuestFetchStatus status, string detail)
            => new QuestSnapshot(status, null, detail);
    }

    /// <summary>
    /// Read-only client for the server-authoritative <c>GET /quests</c> projection. It uses the same
    /// bearer transport as collection and roster reads, never mutates progress, and reports every
    /// failure as data so the main menu can distinguish an empty quest list from an unavailable one.
    /// </summary>
    public sealed class HttpQuestGateway
    {
        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public HttpQuestGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public bool IsConfigured => _baseUrl.Length > 0;

        public async Task<QuestSnapshot> FetchAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured)
                return QuestSnapshot.Failed(
                    QuestFetchStatus.Unavailable, "No quest endpoint is configured.");
            if (string.IsNullOrEmpty(accessToken))
                return QuestSnapshot.Failed(QuestFetchStatus.Unauthenticated, "Not signed in.");

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken),
                new KeyValuePair<string, string>("Accept", "application/json"),
            };

            AuthHttpResponse response = await _http
                .GetAsync(_baseUrl + "/quests", headers, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsTransportFailure)
                return QuestSnapshot.Failed(QuestFetchStatus.Offline, response.Body);
            if (response.StatusCode == 401)
                return QuestSnapshot.Failed(QuestFetchStatus.Unauthenticated, response.Body);
            if (response.StatusCode == 503)
                return QuestSnapshot.Failed(QuestFetchStatus.Unavailable, response.Body);
            if (!response.IsSuccess)
                return QuestSnapshot.Failed(
                    QuestFetchStatus.Offline,
                    "Server returned " + response.StatusCode.ToString(CultureInfo.InvariantCulture));

            return Parse(response.Body);
        }

        private static QuestSnapshot Parse(string body)
        {
            if (!AuthJson.TryReadTopLevel(body, out var fields) ||
                !AuthJson.TryString(fields, "status", out string status))
                return QuestSnapshot.Failed(QuestFetchStatus.Malformed, "No quest status.");

            if (status.Equals("unauthenticated", StringComparison.OrdinalIgnoreCase))
                return QuestSnapshot.Failed(QuestFetchStatus.Unauthenticated, "Not signed in.");
            if (status.Equals("unavailable", StringComparison.OrdinalIgnoreCase))
                return QuestSnapshot.Failed(QuestFetchStatus.Unavailable, "Quest service unavailable.");
            if (!status.Equals("known", StringComparison.OrdinalIgnoreCase))
                return QuestSnapshot.Failed(QuestFetchStatus.Malformed, "Unknown quest status.");
            if (!AuthJson.TryReadObjectArray(body, "quests", out var rows))
                return QuestSnapshot.Failed(QuestFetchStatus.Malformed, "No quest list.");

            var quests = new List<QuestEntry>(rows.Count);
            foreach (IReadOnlyDictionary<string, string> row in rows)
            {
                if (!TryRead(row, out QuestEntry? quest))
                    return QuestSnapshot.Failed(QuestFetchStatus.Malformed, "Unreadable quest row.");
                quests.Add(quest!);
            }
            return QuestSnapshot.Known(quests);
        }

        private static bool TryRead(
            IReadOnlyDictionary<string, string> row,
            out QuestEntry? quest)
        {
            quest = null;
            if (!AuthJson.TryString(row, "id", out string id) ||
                !AuthJson.TryString(row, "title", out string title) ||
                !AuthJson.TryString(row, "day", out string day) ||
                !AuthJson.TryString(row, "state", out string state) ||
                !AuthJson.TryInt64(row, "goal", out long goalRaw) ||
                !AuthJson.TryInt64(row, "progress", out long progressRaw) ||
                !AuthJson.TryInt64(row, "rewardGold", out long rewardGold))
                return false;

            if (goalRaw <= 0 || goalRaw > int.MaxValue || progressRaw < 0 || progressRaw > goalRaw)
                return false;
            if (rewardGold < 0) return false;
            if (!DateTime.TryParseExact(
                    day,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
                return false;

            bool complete;
            if (state.Equals("complete", StringComparison.OrdinalIgnoreCase)) complete = true;
            else if (state.Equals("active", StringComparison.OrdinalIgnoreCase)) complete = false;
            else return false;
            if (complete != (progressRaw >= goalRaw)) return false;

            quest = new QuestEntry(
                id,
                title,
                (int)goalRaw,
                (int)progressRaw,
                rewardGold,
                day,
                complete);
            return true;
        }
    }
}
