using System;
using System.Globalization;
using System.Text;
using Kingdom.Net.Client.Gate;

namespace Kingdom.Net.Client.Telemetry
{
    /// <summary>The <c>milestone</c> values a <c>client_milestone</c> row may carry
    /// (<c>docs/dev-docs/specs/first-session-funnel.md</c>). The server drops any other.</summary>
    public static class FunnelMilestone
    {
        public const string Launch = "launch";
        public const string Gate = "gate";
        public const string SignIn = "sign_in";
        public const string BoardPlayable = "board_playable";
        public const string Queue = "queue";
        public const string Pause = "pause";
        public const string Resume = "resume";
        public const string Exit = "exit";
    }

    /// <summary>The <c>outcome</c> values: four for <c>gate</c>, three for <c>sign_in</c>.</summary>
    public static class FunnelOutcome
    {
        public const string Pass = "pass";
        public const string Stale = "stale";
        public const string Unreachable = "unreachable";
        /// <summary>The check itself threw. <see cref="UpdateGateVerdict"/> has no error member —
        /// a server that answered 5xx or garbage is <see cref="Unreachable"/> there — so this is the
        /// only way a gate row reads <c>error</c>.</summary>
        public const string Error = "error";

        public const string SignedIn = "signed_in";
        public const string Failed = "failed";
        public const string Skipped = "skipped";

        public static string ForGate(UpdateGateVerdict verdict)
        {
            switch (verdict)
            {
                case UpdateGateVerdict.Pass: return Pass;
                case UpdateGateVerdict.UpdateRequired: return Stale;
                case UpdateGateVerdict.Unreachable: return Unreachable;
                default: return Error;
            }
        }
    }

    /// <summary>The <c>source</c> of a <c>queue</c> row.</summary>
    public static class FunnelQueueSource
    {
        public const string Menu = "menu";
        public const string ResultView = "result_view";
    }

    /// <summary>The <c>platform</c> values.</summary>
    public static class FunnelPlatform
    {
        public const string Android = "android";
        public const string Ios = "ios";
        public const string Editor = "editor";
        public const string Desktop = "desktop";
    }

    /// <summary>The request headers that carry the funnel's identity to the server.</summary>
    public static class FunnelHeaders
    {
        /// <summary><c>FunnelSession.Id</c>, on the update-gate request.</summary>
        public const string Session = "X-Kingdom-Session";

        /// <summary>The RAW device token. The server hashes it and never stores it (contract,
        /// Identity); the client never logs it.</summary>
        public const string Device = "X-Kingdom-Device";

        /// <summary>A <see cref="FunnelPlatform"/> value, on the update-gate request.</summary>
        public const string Platform = "X-Kingdom-Platform";

        /// <summary><c>BuildStamp.ReleaseId</c>, on the update-gate request.</summary>
        public const string Build = "X-Kingdom-Build";

        /// <summary><c>ProtocolContract.Version</c>, on the update-gate request.</summary>
        public const string Contract = "X-Kingdom-Contract";
    }

    /// <summary>
    /// One <c>client_milestone</c> row, as the single JSON line the queue stores and the server
    /// reads. Only the fields marked <i>client</i> in the contract: <c>type</c>, <c>device</c>,
    /// <c>account_id</c> and <c>at</c> are the server's to stamp.
    ///
    /// <para><b>Built through the factories, never field by field,</b> so a row can only carry the
    /// optional fields its milestone allows (<c>attempts</c> on <c>gate</c> only, and so on). The
    /// server drops a malformed row rather than storing it, which would lose it silently.</para>
    /// </summary>
    public sealed class FunnelRow
    {
        /// <summary>The contract's cap on <c>cause</c>.</summary>
        public const int MaxCauseLength = 64;

        public string Milestone { get; }
        public string SessionId { get; }
        public long ClientTsMs { get; }
        public string Build { get; }
        public string Platform { get; }
        public string? Outcome { get; private set; }
        public int? Attempts { get; private set; }
        public string? Cause { get; private set; }
        public string? MatchId { get; private set; }
        public string? Source { get; private set; }

        private FunnelRow(string milestone, FunnelStamp stamp, long clientTsMs)
        {
            Milestone = milestone;
            SessionId = stamp.SessionId;
            Build = stamp.Build;
            Platform = stamp.Platform;
            ClientTsMs = clientTsMs;
        }

        public static FunnelRow Plain(string milestone, FunnelStamp stamp, long nowMs)
            => new FunnelRow(milestone, stamp, nowMs);

        public static FunnelRow Gate(FunnelStamp stamp, long nowMs, string outcome, int attempts)
            => new FunnelRow(FunnelMilestone.Gate, stamp, nowMs)
            {
                Outcome = outcome,
                Attempts = attempts < 0 ? 0 : attempts,
            };

        public static FunnelRow SignIn(FunnelStamp stamp, long nowMs, string outcome, string? cause)
            => new FunnelRow(FunnelMilestone.SignIn, stamp, nowMs)
            {
                Outcome = outcome,
                Cause = SanitizeCause(cause),
            };

        public static FunnelRow BoardPlayable(FunnelStamp stamp, long nowMs, string matchId)
            => new FunnelRow(FunnelMilestone.BoardPlayable, stamp, nowMs) { MatchId = matchId ?? "" };

        public static FunnelRow Queue(FunnelStamp stamp, long nowMs, string source)
            => new FunnelRow(FunnelMilestone.Queue, stamp, nowMs) { Source = source };

        /// <summary>The line written to the queue and posted inside <c>{"rows":[...]}</c>.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(192);
            sb.Append('{');
            AppendField(sb, "milestone", Milestone, first: true);
            AppendField(sb, "session_id", SessionId);
            AppendField(sb, "client_ts", FormatTimestamp(ClientTsMs));
            AppendField(sb, "build", Build);
            AppendField(sb, "platform", Platform);
            if (Outcome != null) AppendField(sb, "outcome", Outcome);
            if (Attempts.HasValue)
                sb.Append(",\"attempts\":").Append(Attempts.Value.ToString(CultureInfo.InvariantCulture));
            if (Cause != null) AppendField(sb, "cause", Cause);
            if (MatchId != null) AppendField(sb, "match_id", MatchId);
            if (Source != null) AppendField(sb, "source", Source);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>ISO 8601 UTC with milliseconds, e.g. <c>2026-10-23T14:05:09.123Z</c>.</summary>
        public static string FormatTimestamp(long unixMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        /// <summary>
        /// Fit a cause to the contract: lowercase, <c>[a-z0-9_:.-]</c> only (anything else becomes
        /// <c>_</c>), at most <see cref="MaxCauseLength"/>. Null or empty is no cause at all.
        /// Rewritten rather than refused, because the server drops a row with a malformed value and
        /// a slightly mangled cause is worth more than a lost <c>sign_in</c> row.
        /// </summary>
        public static string? SanitizeCause(string? cause)
        {
            if (string.IsNullOrEmpty(cause)) return null;
            int length = Math.Min(cause!.Length, MaxCauseLength);
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                char c = char.ToLowerInvariant(cause[i]);
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                          c == '_' || c == ':' || c == '.' || c == '-';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string name, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            AppendString(sb, name);
            sb.Append(':');
            AppendString(sb, value ?? "");
        }

        private static void AppendString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>What every row carries regardless of milestone, fixed for the life of a process.</summary>
    public readonly struct FunnelStamp
    {
        public string SessionId { get; }
        public string Build { get; }
        public string Platform { get; }

        public FunnelStamp(string sessionId, string build, string platform)
        {
            SessionId = sessionId ?? "";
            Build = build ?? "";
            Platform = platform ?? "";
        }
    }
}
