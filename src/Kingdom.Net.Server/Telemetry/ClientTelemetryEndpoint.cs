using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Telemetry
{
    /// <summary>What <c>POST /telemetry/client</c> answers, before it becomes JSON.</summary>
    public sealed class ClientTelemetryResult
    {
        public ClientTelemetryResult(int httpStatus, string status, int accepted = 0, int dropped = 0)
        {
            HttpStatus = httpStatus;
            Status = status;
            Accepted = accepted;
            Dropped = dropped;
        }

        /// <summary>200 written · 400 not a <c>{"rows":[…]}</c> body · 401 no credential, or a
        /// bearer that did not verify · 413 too many rows or too many bytes · 429 rate limited ·
        /// 503 could not be written. <b>Only a 200 means the rows are on the server</b>; the client
        /// keeps its queue on anything else, and drops the batch on 400/413, which no retry fixes.</summary>
        public int HttpStatus { get; }
        public string Status { get; }
        public int Accepted { get; }
        /// <summary>Rows that failed validation and were not stored. Counted in a 200: they are
        /// gone for good, so the client must not resend them.</summary>
        public int Dropped { get; }
    }

    /// <summary>
    /// <c>POST /telemetry/client</c> — the client half of the first-session funnel
    /// (<c>docs/dev-docs/specs/first-session-funnel.md</c>, "Client rows").
    ///
    /// <para><b>Client-authored input under a device token — security.md's review trigger, and
    /// VULN-003's posture.</b> Every row is a CLAIM, used for counting and nothing else: no account
    /// state, entitlement or rating is read from or attached to one. The device token is an
    /// identifier, never an authenticator, so a row posted under it is attributed to its HASH and
    /// selects no account; <c>account_id</c> is stamped only from a bearer this route has verified,
    /// never from a client field. The raw token is hashed on arrival and never written.</para>
    ///
    /// <para><b>Strict, and dropping rather than repairing.</b> A row with an unknown or
    /// server-owned field (a client cannot claim an <c>account_id</c>), a milestone off the list, an
    /// out-of-range value or a malformed identifier is dropped whole. Every stored string is either
    /// from a fixed set or a restricted ASCII charset, so no row can carry text a reader renders.</para>
    ///
    /// <para><b>Bounded three ways before anything is parsed or verified:</b> body bytes, rows per
    /// request, and a per-address row bucket. Then a per-device (or, bearer only, per-subject) row
    /// bucket. A bearer that fails verification is a 401, not a downgrade to the device token —
    /// the gate's rule for the same situation.</para>
    /// </summary>
    public sealed class ClientTelemetryEndpoint
    {
        /// <summary>One session's rows, give or take; also the per-device burst, so one request can
        /// always fit an empty bucket.</summary>
        public const int MaxRowsPerRequest = 40;

        /// <summary>40 rows of the widest legal row is well under this.</summary>
        public const int MaxBodyBytes = 32 * 1024;

        /// <summary>A device token is a GUID; anything this long is not one.</summary>
        public const int MaxDeviceTokenLength = 256;

        /// <summary>Oldest <c>client_ts</c> accepted. The client's queue survives relaunches, so a row
        /// can be days late; one from a month ago is a clock or a forgery.</summary>
        public static readonly TimeSpan MaxRowAge = TimeSpan.FromDays(30);

        /// <summary>Clock skew tolerated ahead of the server.</summary>
        public static readonly TimeSpan MaxRowLead = TimeSpan.FromDays(1);

        public const string RowType = "client_milestone";

        private static readonly HashSet<string> Milestones = new HashSet<string>(StringComparer.Ordinal)
            { "launch", "gate", "sign_in", "board_playable", "queue", "pause", "resume", "exit" };
        private static readonly HashSet<string> Platforms = new HashSet<string>(StringComparer.Ordinal)
            { "android", "ios", "editor", "desktop" };
        private static readonly HashSet<string> GateOutcomes = new HashSet<string>(StringComparer.Ordinal)
            { "pass", "stale", "unreachable", "error" };
        private static readonly HashSet<string> SignInOutcomes = new HashSet<string>(StringComparer.Ordinal)
            { "signed_in", "failed", "skipped" };
        private static readonly HashSet<string> QueueSources = new HashSet<string>(StringComparer.Ordinal)
            { "menu", "result_view" };
        private static readonly HashSet<string> ClientFields = new HashSet<string>(StringComparer.Ordinal)
            { "milestone", "session_id", "client_ts", "build", "platform", "outcome", "attempts", "cause", "match_id", "source" };

        private readonly IFunnelSink _sink;
        private readonly IIdentityTokenVerifier? _verifier;
        private readonly IAccountStore? _accounts;
        private readonly ITimeSource _time;
        private readonly FunnelRateLimiter _perDevice;
        private readonly FunnelRateLimiter _perAddress;

        public ClientTelemetryEndpoint(
            IFunnelSink sink, IIdentityTokenVerifier? verifier, IAccountStore? accounts, ITimeSource time,
            FunnelRateLimiter? perDevice = null, FunnelRateLimiter? perAddress = null)
        {
            _sink = sink;
            _verifier = verifier;
            _accounts = accounts;
            _time = time;
            // ~40 rows a session, then one row per 10 s: a session's pause/resume churn fits,
            // a loop does not.
            _perDevice = perDevice ?? new FunnelRateLimiter(time, burst: MaxRowsPerRequest, refillMsPerRow: 10_000);
            // A household or a carrier NAT shares an address, so this is ten devices' worth. It is
            // what stops a caller who rotates device tokens, which the per-device bucket cannot.
            _perAddress = perAddress ?? new FunnelRateLimiter(time, burst: 10 * MaxRowsPerRequest, refillMsPerRow: 1_000);
        }

        /// <param name="body">The request body, or null when it was over <see cref="MaxBodyBytes"/>.</param>
        /// <param name="bearer">The <c>Authorization: Bearer</c> token, or null.</param>
        /// <param name="deviceToken">The <c>X-Kingdom-Device</c> header, or null.</param>
        /// <param name="callerAddress">For the per-address bucket.</param>
        public async Task<ClientTelemetryResult> PostAsync(
            ReadOnlyMemory<byte>? body, string? bearer, string? deviceToken, string callerAddress,
            CancellationToken ct = default)
        {
            bool hasBearer = !string.IsNullOrWhiteSpace(bearer);
            bool hasDevice = !string.IsNullOrWhiteSpace(deviceToken);
            if (!hasBearer && !hasDevice) return new ClientTelemetryResult(401, "unauthenticated");
            if (hasDevice && deviceToken!.Length > MaxDeviceTokenLength) return new ClientTelemetryResult(400, "malformed");
            if (body is null) return new ClientTelemetryResult(413, "too_large");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body.Value, new JsonDocumentOptions { MaxDepth = 4 }); }
            catch (JsonException) { return new ClientTelemetryResult(400, "malformed"); }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("rows", out var rows) ||
                    rows.ValueKind != JsonValueKind.Array)
                    return new ClientTelemetryResult(400, "malformed");

                int count = rows.GetArrayLength();
                if (count > MaxRowsPerRequest) return new ClientTelemetryResult(413, "too_many_rows");
                if (count == 0) return new ClientTelemetryResult(200, "ok");

                if (!_perAddress.TryTake("a:" + callerAddress, count))
                    return new ClientTelemetryResult(429, "rate_limited");

                // ── Who. A bearer must verify; the device token is hashed and nothing more. ──
                string? device = hasDevice ? FunnelFormat.DeviceHash(deviceToken!) : null;
                VerifiedIdentity? identity = null;
                string? subHash = null;
                if (hasBearer)
                {
                    if (_verifier is null) return new ClientTelemetryResult(401, "unauthenticated");
                    var verified = await _verifier.VerifyAsync(bearer!, ct).ConfigureAwait(false);
                    if (!verified.IsSuccess) return new ClientTelemetryResult(401, "unauthenticated");
                    identity = verified.Identity!;
                    subHash = CredentialHash.Of(identity.Subject);
                }

                string limitKey = device is not null ? "d:" + device : "s:" + subHash;
                if (!_perDevice.TryTake(limitKey, count))
                    return new ClientTelemetryResult(429, "rate_limited");

                // The same account id MatchGate seats this player under, so a row joins match_seat:
                // the store's player_id, or the subject hash on a box without a store.
                string? accountId = null;
                if (identity is not null)
                {
                    if (_accounts is null) accountId = subHash;
                    else
                    {
                        try
                        {
                            var account = await _accounts.ResolveOrCreateAsync(
                                "supabase", subHash!, identity.Email, ct).ConfigureAwait(false);
                            accountId = account.PlayerId;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.Error.WriteLine("[funnel] account resolve failed: {0}", ex.Message);
                            return new ClientTelemetryResult(503, "unavailable");
                        }
                    }
                }

                long now = _time.NowMs;
                string at = FunnelFormat.Timestamp(now);
                var built = new List<FunnelRow>(count);
                foreach (var row in rows.EnumerateArray())
                {
                    var r = TryBuildRow(row, device, accountId, at, now);
                    if (r is not null) built.Add(r);
                }

                int dropped = count - built.Count;
                if (built.Count > 0 && !_sink.Write(built))
                    return new ClientTelemetryResult(503, "unavailable");
                return new ClientTelemetryResult(200, "ok", built.Count, dropped);
            }
        }

        /// <summary>
        /// One client row, validated against the contract and stamped, or null to drop it. Public so
        /// the validation table is tested directly rather than through HTTP.
        /// </summary>
        public static FunnelRow? TryBuildRow(JsonElement row, string? device, string? accountId, string at, long nowMs)
        {
            if (row.ValueKind != JsonValueKind.Object) return null;

            var seen = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in row.EnumerateObject())
            {
                // Unknown and server-owned names alike: a row naming its own account_id or device
                // is claiming what only the server may say.
                if (!ClientFields.Contains(p.Name) || !seen.TryAdd(p.Name, p.Value)) return null;
            }

            string? milestone = OneOf(seen, "milestone", Milestones);
            if (milestone is null) return null;

            if (!TryString(seen, "session_id", out string? session) || !FunnelFormat.IsLowerHex(session, 32))
                return null;

            if (!TryString(seen, "client_ts", out string? rawTs) || !TryParseClientTs(rawTs!, out var ts))
                return null;
            var now = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
            if (ts < now - MaxRowAge || ts > now + MaxRowLead) return null;

            string? build = null;
            if (seen.ContainsKey("build") &&
                (!TryString(seen, "build", out build) || !FunnelFormat.IsToken(build, 64, ".+_-")))
                return null;

            string? platform = null;
            if (seen.ContainsKey("platform") && (platform = OneOf(seen, "platform", Platforms)) is null)
                return null;

            // outcome: required where the contract defines one, absent everywhere else.
            string? outcome = null;
            var outcomes = milestone == "gate" ? GateOutcomes : milestone == "sign_in" ? SignInOutcomes : null;
            if (outcomes is null) { if (seen.ContainsKey("outcome")) return null; }
            else if ((outcome = OneOf(seen, "outcome", outcomes)) is null) return null;

            int? attempts = null;
            if (seen.TryGetValue("attempts", out var a))
            {
                if (milestone != "gate" || a.ValueKind != JsonValueKind.Number ||
                    !a.TryGetInt32(out int n) || n < 0 || n > 100)
                    return null;
                attempts = n;
            }

            string? cause = null;
            if (seen.ContainsKey("cause") &&
                (!TryString(seen, "cause", out cause) || !FunnelFormat.IsToken(cause, 64, "_:.-", lowerOnly: true)))
                return null;

            string? matchId = null;
            if (seen.ContainsKey("match_id") &&
                (milestone != "board_playable" || !TryString(seen, "match_id", out matchId) ||
                 !FunnelFormat.IsLowerHex(matchId, 32)))
                return null;

            string? source = null;
            if (seen.ContainsKey("source") &&
                (milestone != "queue" || (source = OneOf(seen, "source", QueueSources)) is null))
                return null;

            var r = new FunnelRow(RowType)
                .Str("milestone", milestone)
                .Str("session_id", session)
                .Str("client_ts", FunnelFormat.Timestamp(ts))
                .Str("build", build)
                .Str("platform", platform)
                .Str("outcome", outcome);
            if (attempts is int at2) r.Int("attempts", at2);
            return r.Str("cause", cause)
                .Str("match_id", matchId)
                .Str("source", source)
                .Str("device", device)
                .Str("account_id", accountId)
                .Str("at", at);
        }

        /// <summary>ISO 8601 in UTC (<c>Z</c>), seconds required, 0–7 fractional digits.</summary>
        private static readonly string[] ClientTsFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.f'Z'", "yyyy-MM-dd'T'HH:mm:ss.ff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'", "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
        };

        /// <summary>
        /// <c>client_ts</c> by EXACT format, not a lenient parse: the view casts the stored string to
        /// <c>timestamptz</c>, and one value Postgres refuses fails every query over the view. An
        /// impossible date (<c>2026-02-30</c>) fails here, an offset other than <c>Z</c> fails here,
        /// and the year must be sane. The stored form is always re-emitted as
        /// <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (<see cref="FunnelFormat.Timestamp(DateTimeOffset)"/>).
        /// </summary>
        internal static bool TryParseClientTs(string raw, out DateTimeOffset ts)
        {
            ts = default;
            if (raw.Length > 32 ||
                !DateTimeOffset.TryParseExact(raw, ClientTsFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out ts))
                return false;
            return ts.Year >= 2025 && ts.Year <= 2100;
        }

        private static bool TryString(Dictionary<string, JsonElement> seen, string name, out string? value)
        {
            value = null;
            if (!seen.TryGetValue(name, out var e) || e.ValueKind != JsonValueKind.String) return false;
            value = e.GetString();
            return value is not null;
        }

        private static string? OneOf(Dictionary<string, JsonElement> seen, string name, HashSet<string> allowed)
            => TryString(seen, name, out string? v) && allowed.Contains(v!) ? v : null;
    }
}
