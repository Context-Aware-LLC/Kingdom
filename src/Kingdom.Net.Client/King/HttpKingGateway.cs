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
        /// Favorite struck or deleted. Empty for a Favorite with no name to show, who gets the
        /// client's placeholder. PEER-AUTHORED: render with rich text off and a pixel clip.</summary>
        public string? Favorite { get; }

        public RollEntry(int reign, int epithet, int fate, string? favorite)
        {
            Reign = reign;
            Epithet = epithet;
            Fate = fate;
            Favorite = favorite;
        }
    }

    /// <summary>One line of the King's Favor board.</summary>
    public sealed class FavorBoardRow
    {
        public int Place { get; }

        /// <summary>PEER-AUTHORED: render with rich text off and a pixel clip. Empty for a player with
        /// no name to show, who gets the client's placeholder.</summary>
        public string Name { get; }

        public int Score { get; }

        public FavorBoardRow(int place, string name, int score)
        {
            Place = place;
            Name = name ?? "";
            Score = score;
        }
    }

    /// <summary>This reign's King's Favor board, as <c>GET /king/favor</c> answers it.</summary>
    public sealed class FavorBoard
    {
        public int Reign { get; }

        /// <summary>A <c>CommissionIds</c> id.</summary>
        public string Commission { get; }

        public bool LowerIsBetter { get; }

        /// <summary>Best first, at most a hundred.</summary>
        public IReadOnlyList<FavorBoardRow> Rows { get; }

        public FavorBoard(int reign, string commission, bool lowerIsBetter, IReadOnlyList<FavorBoardRow> rows)
        {
            Reign = reign;
            Commission = commission;
            LowerIsBetter = lowerIsBetter;
            Rows = rows;
        }
    }

    /// <summary>The signed-in player's own standing, as <c>GET /king/favor/me</c> answers it.</summary>
    public sealed class FavorStanding
    {
        /// <summary>Null when the player has no score this reign.</summary>
        public int? Place { get; }
        public int? Score { get; }

        /// <summary>Commission ids whose seal the player holds: one per commission they ever placed
        /// in. A commission id is its achievement's id, so each names a Ledger page.</summary>
        public IReadOnlyList<string> Seals { get; }

        public FavorStanding(int? place, int? score, IReadOnlyList<string> seals)
        {
            Place = place;
            Score = score;
            Seals = seals;
        }
    }

    /// <summary>
    /// Read-only client for <c>GET /king</c>, <c>GET /king/roll</c> and <c>GET /king/favor</c>, which
    /// are public and the same for everyone, so no token is sent; and <c>GET /king/favor/me</c>, which
    /// takes the bearer. Every failure comes back as data, never as an exception.
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

                // Read raw, not through TryString, which refuses "": an unnamed Favorite is a
                // Favorite, and only a JSON null is none.
                string? favorite = null;
                if (!nulls.Contains("favorite") && fields.TryGetValue("favorite", out string? name))
                    favorite = name;
                roll.Add(new RollEntry(reign, epithet, fate, favorite));
            }
            return roll;
        }

        /// <summary>This reign's board, or null when there is no King or the answer could not be
        /// read. An unreadable row nulls the whole board rather than leaving a gap in it.</summary>
        public async Task<FavorBoard?> FetchFavorAsync(CancellationToken cancellationToken = default)
        {
            string? body = await GetAsync("/king/favor", null, cancellationToken).ConfigureAwait(false);
            if (body == null || !ReadStatus(body, out IReadOnlyDictionary<string, string> fields)) return null;
            if (!TryCount(fields, "reign", 1, out int reign) ||
                !AuthJson.TryString(fields, "commission", out string commission) ||
                !TryBool(fields, "lowerIsBetter", out bool lowerIsBetter) ||
                !AuthJson.TryReadRawObjectArray(body, "rows", out IReadOnlyList<string> raw))
                return null;

            var rows = new List<FavorBoardRow>(raw.Count);
            foreach (string row in raw)
            {
                if (!AuthJson.TryReadTopLevelWithNulls(row, out IReadOnlyDictionary<string, string> r,
                        out HashSet<string> nulls) ||
                    !TryCount(r, "place", 1, out int place) ||
                    !AuthJson.TryInt64(r, "score", out long score) || score < int.MinValue || score > int.MaxValue ||
                    !r.TryGetValue("name", out string? name) || name == null)
                    return null;
                // Raw, like the Roll's Favorite: "" is the placeholder's cue, and "null" in quotes
                // is a player of that name.
                rows.Add(new FavorBoardRow(place, nulls.Contains("name") ? "" : name, (int)score));
            }
            return new FavorBoard(reign, commission, lowerIsBetter, rows);
        }

        /// <summary>The signed-in player's place, best and seals, or null when there is no King, no
        /// token, or the answer could not be read. Callers that only want seals read null as none.</summary>
        public async Task<FavorStanding?> FetchStandingAsync(string accessToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(accessToken)) return null;
            string? body = await GetAsync("/king/favor/me", accessToken, cancellationToken).ConfigureAwait(false);
            if (body == null || !AuthJson.TryReadTopLevelWithNulls(body, out IReadOnlyDictionary<string, string> fields,
                    out HashSet<string> nulls) ||
                !AuthJson.TryString(fields, "status", out string status) ||
                !status.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                !AuthJson.TryReadStringArray(body, "seals", out IReadOnlyList<string> seals))
                return null;

            int? place = null;
            int? score = null;
            if (!nulls.Contains("place"))
            {
                if (!TryCount(fields, "place", 1, out int p) ||
                    !AuthJson.TryInt64(fields, "score", out long s) || s < int.MinValue || s > int.MaxValue)
                    return null;
                place = p;
                score = (int)s;
            }
            return new FavorStanding(place, score, seals);
        }

        private Task<string?> GetAsync(string path, CancellationToken cancellationToken)
            => GetAsync(path, null, cancellationToken);

        private async Task<string?> GetAsync(string path, string? accessToken, CancellationToken cancellationToken)
        {
            if (!IsConfigured) return null;

            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accept", "application/json"),
            };
            if (accessToken != null)
                headers.Insert(0, new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken));
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

        private static bool TryBool(IReadOnlyDictionary<string, string> fields, string name, out bool value)
        {
            value = false;
            if (!fields.TryGetValue(name, out string? raw)) return false;
            if (raw == "true") { value = true; return true; }
            return raw == "false";
        }

        private static bool TryCount(IReadOnlyDictionary<string, string> fields, string name, int min, out int value)
        {
            value = 0;
            if (!AuthJson.TryInt64(fields, name, out long raw) || raw < min || raw > int.MaxValue) return false;
            value = (int)raw;
            return true;
        }
    }
}
