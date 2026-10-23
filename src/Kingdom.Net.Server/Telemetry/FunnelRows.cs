using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kingdom.Net.Server.Telemetry
{
    /// <summary>
    /// One first-session funnel row (pre-publication item 2; the row contract is
    /// <c>docs/dev-docs/specs/first-session-funnel.md</c>). A <c>type</c> and an ordered list of
    /// scalar fields, nothing nested: every row lands in <c>raw_event</c> only, and the view reads it
    /// with <c>line-&gt;&gt;'field'</c>.
    ///
    /// <para><b>Field values are already validated when they get here.</b> The row is a carrier, not
    /// a checker: <see cref="ClientMilestoneRows"/> and <see cref="SentryWebhook"/> decide what a
    /// client- or Sentry-authored value may be, and the server's own rows hold server values.</para>
    /// </summary>
    public sealed class FunnelRow
    {
        private readonly List<(string Name, object Value)> _fields = new List<(string, object)>();

        public FunnelRow(string type) => Type = type;

        public string Type { get; }
        public IReadOnlyList<(string Name, object Value)> Fields => _fields;

        /// <summary>Adds a string field; a null value adds nothing (absent, never JSON null).</summary>
        public FunnelRow Str(string name, string? value)
        {
            if (value is not null) _fields.Add((name, value));
            return this;
        }

        public FunnelRow Int(string name, int value)
        {
            _fields.Add((name, value));
            return this;
        }

        /// <summary>The value of a string field, or null. For tests and the in-memory sink.</summary>
        public string? Get(string name)
        {
            foreach (var f in _fields)
                if (f.Name == name) return f.Value as string;
            return null;
        }

        public bool Has(string name)
        {
            foreach (var f in _fields)
                if (f.Name == name) return true;
            return false;
        }
    }

    /// <summary>
    /// Where funnel rows go. A seam of its own rather than a fourth method on
    /// <see cref="ITelemetrySink"/>: the match-event decorators (quests, achievements) wrap that
    /// interface and have no business seeing these rows, and every one of them would otherwise have
    /// to forward a method it does not care about.
    ///
    /// <para><b>Must not throw</b>, for the reason <see cref="ITelemetrySink"/> gives: three of the
    /// four callers are on a match socket's path. The return value is for the one caller that can use
    /// it, <c>POST /telemetry/client</c>, which answers 503 on false so the client keeps its rows
    /// queued instead of believing they landed.</para>
    /// </summary>
    public interface IFunnelSink
    {
        /// <returns>False when the rows could not be written. All or nothing.</returns>
        bool Write(IReadOnlyList<FunnelRow> rows);
    }

    public static class FunnelSinkExtensions
    {
        public static bool Write(this IFunnelSink sink, FunnelRow row) => sink.Write(new[] { row });
    }

    /// <summary>The default: nothing recorded, every caller still runs.</summary>
    public sealed class NullFunnelSink : IFunnelSink
    {
        public static NullFunnelSink Instance { get; } = new NullFunnelSink();
        private NullFunnelSink() { }
        public bool Write(IReadOnlyList<FunnelRow> rows) => true;
    }

    /// <summary>Test double: keeps every row in order.</summary>
    public sealed class InMemoryFunnelSink : IFunnelSink
    {
        private readonly object _lock = new object();
        private readonly List<FunnelRow> _rows = new List<FunnelRow>();

        /// <summary>When true, <see cref="Write"/> refuses, as a full disk would.</summary>
        public bool Fail { get; set; }

        public IReadOnlyList<FunnelRow> Rows { get { lock (_lock) return _rows.ToArray(); } }

        public List<FunnelRow> OfType(string type)
        {
            var hits = new List<FunnelRow>();
            lock (_lock)
                foreach (var r in _rows)
                    if (r.Type == type) hits.Add(r);
            return hits;
        }

        public bool Write(IReadOnlyList<FunnelRow> rows)
        {
            if (Fail) return false;
            lock (_lock) _rows.AddRange(rows);
            return true;
        }
    }

    /// <summary>
    /// The server-observed funnel rows, one builder each, so every spelling of a type and a field
    /// lives here and the call sites only say WHEN. Shapes are the contract's "Server rows" table.
    /// </summary>
    public static class ServerFunnelRows
    {
        public const string HeaderSession = "X-Kingdom-Session";
        public const string HeaderDevice = "X-Kingdom-Device";
        /// <summary>Not in the contract's header list: <c>gate_served</c> names <c>build</c> and
        /// <c>platform</c>, and the gate request carried neither, so the server reads them here.</summary>
        public const string HeaderPlatform = "X-Kingdom-Platform";
        public const string HeaderBuild = "X-Kingdom-Build";
        /// <summary>The client's wire contract, so the row can say <c>stale</c> for a contract bump
        /// the way the client's own verdict does. Optional like the two above.</summary>
        public const string HeaderContract = "X-Kingdom-Contract";

        /// <summary>
        /// <c>gate_served</c>. <c>GET /update-gate</c> answers every caller the same JSON and the
        /// CLIENT decides pass or stale (<c>HttpUpdateGate.CheckAsync</c>), so the outcome "the server
        /// served" is the same decision re-run here over what the request says about itself. With no
        /// platform header there is nothing to decide on and <c>outcome</c> is absent — never a
        /// guessed <c>pass</c>.
        ///
        /// <para>Every header is client-asserted and validated like a client row's field: a value that
        /// fails is dropped from the row, and the route still answers.</para>
        /// </summary>
        public static FunnelRow GateServed(
            string? session, string? deviceToken, string? platform, string? build, string? clientContract,
            string serverContract, int minBuildAndroid, int minBuildIos, long nowMs)
        {
            if (!FunnelFormat.IsLowerHex(session, 32)) session = null;
            string? device = !string.IsNullOrWhiteSpace(deviceToken) && deviceToken!.Length <= 256
                ? FunnelFormat.DeviceHash(deviceToken) : null;
            if (platform is not ("android" or "ios" or "editor" or "desktop")) platform = null;
            if (!FunnelFormat.IsToken(build, 64, ".+_-")) build = null;
            if (!FunnelFormat.IsToken(clientContract, 64, "._-")) clientContract = null;

            string? outcome = null;
            if (platform is not null)
            {
                int min = platform == "android" ? minBuildAndroid : platform == "ios" ? minBuildIos : 0;
                bool staleByBuild = StoreBuild(build) is int storeBuild && storeBuild < min;
                bool staleByContract = Generation(serverContract) is int s && Generation(clientContract) is int c && s > c;
                outcome = staleByBuild || staleByContract ? "stale" : "pass";
            }

            return new FunnelRow("gate_served")
                .Str("session_id", session)
                .Str("device", device)
                .Str("outcome", outcome)
                .Str("build", build)
                .Str("platform", platform)
                .Str("at", FunnelFormat.Timestamp(nowMs));
        }

        /// <summary><c>hello</c>: a match socket's hello resolved an account.</summary>
        public static FunnelRow Hello(string accountId, string deviceToken, long nowMs) =>
            new FunnelRow("hello")
                .Str("account_id", accountId)
                .Str("device", FunnelFormat.DeviceHash(deviceToken))
                .Str("at", FunnelFormat.Timestamp(nowMs));

        /// <summary><c>queue</c>: a pairing entered the matchmaking queue.</summary>
        public static FunnelRow Queue(string accountId, long nowMs) =>
            new FunnelRow("queue")
                .Str("account_id", accountId)
                .Str("at", FunnelFormat.Timestamp(nowMs));

        /// <summary><c>queue_refused</c>: a pairing was turned away at <c>MaxSessions</c>.</summary>
        public static FunnelRow QueueRefusedCapacity(string accountId, long nowMs) =>
            new FunnelRow("queue_refused")
                .Str("account_id", accountId)
                .Str("reason", "capacity")
                .Str("at", FunnelFormat.Timestamp(nowMs));

        /// <summary><c>first_action</c>: a seat's first accepted intent in a match.</summary>
        public static FunnelRow FirstAction(string matchId, string accountId, long nowMs) =>
            new FunnelRow("first_action")
                .Str("match_id", matchId)
                .Str("account_id", accountId)
                .Str("at", FunnelFormat.Timestamp(nowMs));

        /// <summary>The store build number out of <c>BuildStamp.ReleaseId</c> (<c>0.0.14+31</c>), or
        /// null for one with none (<c>+editor</c>), which is never stale by number — the client's rule.</summary>
        internal static int? StoreBuild(string? releaseId)
        {
            if (releaseId is null) return null;
            int plus = releaseId.LastIndexOf('+');
            string tail = plus >= 0 ? releaseId.Substring(plus + 1) : releaseId;
            return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
        }

        /// <summary>The N of <c>interfaces.vN</c>; the client's <c>HttpUpdateGate.TryGeneration</c>.</summary>
        internal static int? Generation(string? contract)
        {
            if (string.IsNullOrEmpty(contract)) return null;
            int start = contract!.Length;
            while (start > 0 && contract[start - 1] >= '0' && contract[start - 1] <= '9') start--;
            return start < contract.Length &&
                   int.TryParse(contract.Substring(start), NumberStyles.None, CultureInfo.InvariantCulture, out int g)
                ? g : null;
        }
    }

    /// <summary>The funnel's identity and time spellings, in one place so no two writers drift.</summary>
    public static class FunnelFormat
    {
        /// <summary>
        /// The <c>device</c> key: lowercase hex SHA-256 of the UTF-8 device token, first 16
        /// characters. <b>Must equal the client's <c>CrashReportingScope.DeviceHash</c></b>
        /// (<c>unity/Assets/Scripts/Presentation/</c>) byte for byte — a Sentry event, a client row
        /// and a server row join on it, and a drift would split every device into two that never meet.
        /// The raw token is never stored (VULN-003 §4.5).
        /// </summary>
        public static string DeviceHash(string deviceToken)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(deviceToken ?? ""), digest);
            return Convert.ToHexStringLower(digest.Slice(0, 8));
        }

        /// <summary>ISO 8601 UTC with milliseconds — the <c>at</c> and <c>client_ts</c> spelling.</summary>
        public static string Timestamp(DateTimeOffset t) =>
            t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        public static string Timestamp(long unixMs) => Timestamp(DateTimeOffset.FromUnixTimeMilliseconds(unixMs));

        /// <summary>Exactly <paramref name="length"/> lowercase hex characters.</summary>
        public static bool IsLowerHex(string? s, int length)
        {
            if (s is null || s.Length != length) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>Non-empty, at most <paramref name="max"/> characters, each ASCII alphanumeric or
        /// one of <paramref name="extra"/>. Everything a funnel row stores that is not server-minted
        /// passes through here or a fixed whitelist, so no row can carry text a reader renders.</summary>
        public static bool IsToken(string? s, int max, string extra, bool lowerOnly = false)
        {
            if (string.IsNullOrEmpty(s) || s!.Length > max) return false;
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') ||
                          (!lowerOnly && c >= 'A' && c <= 'Z') || extra.IndexOf(c) >= 0;
                if (!ok) return false;
            }
            return true;
        }
    }
}
