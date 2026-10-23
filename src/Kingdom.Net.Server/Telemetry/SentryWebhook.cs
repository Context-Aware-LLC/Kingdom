using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kingdom.Net.Server.Telemetry
{
    /// <summary>
    /// <c>POST /telemetry/sentry</c> — Sentry's integration-platform webhook, turned into the funnel's
    /// <c>crash</c> row (<c>docs/dev-docs/specs/first-session-funnel.md</c>, "Server rows").
    ///
    /// <para><b>An external webhook — security.md's review trigger.</b> The only thing that makes a
    /// request Sentry's is <c>Sentry-Hook-Signature</c>: lowercase hex HMAC-SHA256 of the request body
    /// keyed with the integration's Client Secret (Sentry's "Webhooks" page, integration platform).
    /// It is checked against the RAW body bytes, in constant time, before the body is parsed. With no
    /// secret configured the route does not exist (404), so an unconfigured box accepts nothing
    /// rather than everything.</para>
    ///
    /// <para><b>What is kept is four identifiers, each re-validated:</b> the event's
    /// <c>session_id</c> and <c>device</c> tags (the shapes the client mints), its event id and its
    /// release. Sentry's payload carries the whole event — stack, breadcrumbs, user — and none of it
    /// is stored: the row is a join key, the event stays in Sentry.</para>
    ///
    /// <para><b>Only error-bearing resources write.</b> <c>error</c> (the event under
    /// <c>data.error</c>) and <c>event_alert</c> (under <c>data.event</c>); every other resource —
    /// <c>installation</c>, <c>issue</c>, <c>metric_alert</c>, <c>comment</c> … — is answered 200
    /// with no row, so Sentry does not retry or disable the hook over a resource we never asked for.</para>
    ///
    /// <para><b>Replay.</b> Sentry signs no timestamp into the HMAC, so a captured request replays.
    /// A replay writes a byte-identical row except for <c>at</c>, so the view must count a crash
    /// per <c>sentry_event_id</c>; counted that way a replay is worth nothing.</para>
    /// </summary>
    public static class SentryWebhook
    {
        public const string SecretEnvVar = "KINGDOM_SENTRY_WEBHOOK_SECRET";
        public const string SignatureHeader = "Sentry-Hook-Signature";
        public const string ResourceHeader = "Sentry-Hook-Resource";
        public const string RowType = "crash";

        /// <summary>A full event with a native stack runs to a few hundred KB; past this, refuse.</summary>
        public const int MaxBodyBytes = 1024 * 1024;

        public readonly struct Outcome
        {
            public Outcome(int httpStatus, string status, FunnelRow? row = null)
            {
                HttpStatus = httpStatus;
                Status = status;
                Row = row;
            }

            public int HttpStatus { get; }
            public string Status { get; }
            public FunnelRow? Row { get; }
        }

        /// <param name="body">The raw body bytes, exactly as received.</param>
        /// <param name="oversized">The body exceeded <see cref="MaxBodyBytes"/> and was not read whole.</param>
        public static Outcome Handle(string secret, ReadOnlySpan<byte> body, bool oversized,
                                     string? signature, string? resource, long nowMs)
        {
            if (oversized) return new Outcome(413, "too_large");
            if (!SignatureMatches(secret, body, signature)) return new Outcome(401, "bad_signature");

            string? path = resource switch
            {
                "error" => "error",
                "event_alert" => "event",
                _ => null,
            };
            if (path is null) return new Outcome(200, "ignored");

            try
            {
                using var doc = JsonDocument.Parse(body.ToArray());
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty(path, out var ev) || ev.ValueKind != JsonValueKind.Object)
                    return new Outcome(200, "ignored");

                string? eventId = Lower(StringProp(ev, "event_id"));
                if (!FunnelFormat.IsLowerHex(eventId, 32)) return new Outcome(200, "ignored");

                string? release = StringProp(ev, "release");
                if (!FunnelFormat.IsToken(release, 200, ".+_-@")) release = null;

                string? session = Tag(ev, "session_id");
                if (!FunnelFormat.IsLowerHex(session, 32)) session = null;
                // NOT the `device` tag alone: Sentry reserves `device` for its own model tag ("Pixel
                // 5a"), which displaced ours on the first real crash (2026-10-23). The client tags
                // `device_hash`; builds before that still carry the hash as the event's user id.
                string? device = FirstLowerHex16(
                    Tag(ev, "device_hash"),
                    Tag(ev, "device"),
                    ev.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                        ? StringProp(user, "id") : null);

                var row = new FunnelRow(RowType)
                    .Str("session_id", session)
                    .Str("device", device)
                    .Str("sentry_event_id", eventId)
                    .Str("release", release)
                    .Str("at", FunnelFormat.Timestamp(nowMs));
                return new Outcome(200, "ok", row);
            }
            catch (JsonException)
            {
                // Signed by Sentry and still unreadable: nothing a retry would fix.
                return new Outcome(200, "ignored");
            }
        }

        /// <summary>Lowercase hex HMAC-SHA256 of the raw body, compared in constant time.</summary>
        public static bool SignatureMatches(string secret, ReadOnlySpan<byte> body, string? signature)
        {
            if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(signature)) return false;
            string expected = Sign(secret, body);
            byte[] a = Encoding.ASCII.GetBytes(expected);
            byte[] b = Encoding.ASCII.GetBytes(signature!.Trim().ToLowerInvariant());
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        public static string Sign(string secret, ReadOnlySpan<byte> body)
        {
            Span<byte> mac = stackalloc byte[32];
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, mac);
            return Convert.ToHexStringLower(mac);
        }

        private static string? StringProp(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string? Lower(string? s) => s?.ToLowerInvariant();

        private static string? FirstLowerHex16(params string?[] candidates)
        {
            foreach (string? c in candidates)
                if (FunnelFormat.IsLowerHex(c, 16)) return c;
            return null;
        }

        /// <summary>A tag off the event. Sentry sends <c>[["key","value"], …]</c>; the
        /// <c>{"key":…,"value":…}</c> form its API also uses is read too.</summary>
        private static string? Tag(JsonElement ev, string key)
        {
            if (!ev.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return null;
            foreach (var t in tags.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() == 2 &&
                    t[0].ValueKind == JsonValueKind.String && t[0].GetString() == key &&
                    t[1].ValueKind == JsonValueKind.String)
                    return t[1].GetString();
                if (t.ValueKind == JsonValueKind.Object && StringProp(t, "key") == key)
                    return StringProp(t, "value");
            }
            return null;
        }
    }
}
