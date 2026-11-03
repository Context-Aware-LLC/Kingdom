using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.King
{
    /// <summary>
    /// This week's King, as <c>GET /king</c> answers it. <see cref="Epithet"/> and <see cref="Fate"/>
    /// are 0-based indices into the pools the client words; the server never sends the words.
    /// </summary>
    public sealed class KingNow
    {
        public int Reign { get; }
        public int Epithet { get; }
        public int Fate { get; }

        /// <summary>A <c>CommissionIds</c> id.</summary>
        public string Commission { get; }

        /// <summary>When this reign ends and the next King takes the throne, in Unix ms.</summary>
        public long EndsAtMs { get; }

        public KingNow(int reign, int epithet, int fate, string commission, long endsAtMs)
        {
            Reign = reign;
            Epithet = epithet;
            Fate = fate;
            Commission = commission;
            EndsAtMs = endsAtMs;
        }
    }

    /// <summary>One line of the Succession Roll.</summary>
    public sealed class RollEntry
    {
        public int Reign { get; }
        public int Epithet { get; }
        public int Fate { get; }

        /// <summary>The reign's top place. Null for the current reign, a reign nobody won, and a
        /// Favorite struck or deleted. PEER-AUTHORED: render with rich text off and a pixel clip.</summary>
        public string? Favorite { get; }

        public RollEntry(int reign, int epithet, int fate, string? favorite)
        {
            Reign = reign;
            Epithet = epithet;
            Fate = fate;
            Favorite = favorite;
        }
    }

    /// <summary>
    /// Read-only client for <c>GET /king</c> and <c>GET /king/roll</c>. Both are public and the same
    /// for everyone, so no token is sent. Every failure comes back as data, never as an exception.
    ///
    /// <para><b>No King and an unread King are both null</b>, and both mean the same thing to the
    /// menu: no crown. Nothing a player can do depends on the King, so the difference has nowhere to
    /// go. The Roll is the same: an answered Roll always holds at least the current reign, so empty
    /// can only mean it was not read.</para>
    /// </summary>
    public sealed class HttpKingGateway
    {
        private static readonly IReadOnlyList<RollEntry> NoRoll = Array.Empty<RollEntry>();

        private readonly string _baseUrl;
        private readonly IAuthHttp _http;

        public HttpKingGateway(string baseUrl, IAuthHttp http)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public bool IsConfigured => _baseUrl.Length > 0;

        /// <summary>This week's King, or null when there is none or the answer could not be read.</summary>
        public async Task<KingNow?> FetchKingAsync(CancellationToken cancellationToken = default)
        {
            string? body = await GetAsync("/king", cancellationToken).ConfigureAwait(false);
            if (body == null || !ReadStatus(body, out IReadOnlyDictionary<string, string> fields)) return null;

            if (!TryCount(fields, "reign", 1, out int reign) ||
                !TryCount(fields, "epithet", 0, out int epithet) ||
                !TryCount(fields, "fate", 0, out int fate) ||
                !AuthJson.TryString(fields, "commission", out string commission) ||
                !AuthJson.TryInt64(fields, "endsAtMs", out long endsAtMs))
                return null;
            return new KingNow(reign, epithet, fate, commission, endsAtMs);
        }

        /// <summary>Every reign, newest first, or empty when there is no King or the answer could not
        /// be read. An unreadable row empties the whole Roll rather than leaving a gap in it.</summary>
        public async Task<IReadOnlyList<RollEntry>> FetchRollAsync(CancellationToken cancellationToken = default)
        {
            string? body = await GetAsync("/king/roll", cancellationToken).ConfigureAwait(false);
            if (body == null || !ReadStatus(body, out _)) return NoRoll;
            if (!AuthJson.TryReadRawObjectArray(body, "reigns", out IReadOnlyList<string> rows)) return NoRoll;

            var roll = new List<RollEntry>(rows.Count);
            foreach (string row in rows)
            {
                if (!AuthJson.TryReadTopLevelWithNulls(row, out IReadOnlyDictionary<string, string> fields,
                        out HashSet<string> nulls) ||
                    !TryCount(fields, "reign", 1, out int reign) ||
                    !TryCount(fields, "epithet", 0, out int epithet) ||
                    !TryCount(fields, "fate", 0, out int fate))
                    return NoRoll;

                string? favorite = null;
                if (!nulls.Contains("favorite") && AuthJson.TryString(fields, "favorite", out string name))
                    favorite = name;
                roll.Add(new RollEntry(reign, epithet, fate, favorite));
            }
            return roll;
        }

        private async Task<string?> GetAsync(string path, CancellationToken cancellationToken)
        {
            if (!IsConfigured) return null;

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            AuthHttpResponse response = await _http
                .GetAsync(_baseUrl + path, headers, cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccess ? response.Body : null;
        }

        /// <summary>True for an object whose status is <c>ok</c>. <c>none</c> (no King set, or before
        /// Reign 1) and anything unreadable are both false.</summary>
        private static bool ReadStatus(string body, out IReadOnlyDictionary<string, string> fields)
            => AuthJson.TryReadTopLevel(body, out fields) &&
               AuthJson.TryString(fields, "status", out string status) &&
               status.Equals("ok", StringComparison.OrdinalIgnoreCase);

        private static bool TryCount(IReadOnlyDictionary<string, string> fields, string name, int min, out int value)
        {
            value = 0;
            if (!AuthJson.TryInt64(fields, name, out long raw) || raw < min || raw > int.MaxValue) return false;
            value = (int)raw;
            return true;
        }
    }
}
