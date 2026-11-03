using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kingdom.Net.Client.Auth
{
    /// <summary>
    /// Just enough JSON for the auth calls: build a flat request object, and read the TOP-LEVEL
    /// scalar fields out of a reply.
    ///
    /// <para><b>Why not <c>Kingdom.Net.Protocol</c>'s parser.</b> That one is deliberately strict
    /// for the game wire — it rejects floats, exponents and duplicate keys, and it is <c>internal</c>.
    /// Supabase's replies are somebody else's schema and may legally contain all three inside the
    /// <c>user</c> object, so borrowing it would turn a valid sign-in into a decode failure. This
    /// reader skips nested objects and arrays whole rather than trying to understand them.</para>
    ///
    /// <para><b>Top-level only, and that is a correctness property, not a shortcut.</b> A scan for
    /// the first <c>"email"</c> in a Supabase reply finds the one inside <c>user</c>, and a scan for
    /// <c>"id"</c> finds whichever nested id happens to come first. Everything this file needs —
    /// <c>access_token</c>, <c>refresh_token</c>, <c>expires_in</c>, and the JWT payload's <c>sub</c>
    /// and <c>email</c> — is at the top level of its own object, so reading only the top level is
    /// unambiguous.</para>
    ///
    /// <para><b>Never throws on input.</b> Every reader reports failure by returning false.</para>
    /// </summary>
    internal static class AuthJson
    {
        /// <summary>Build a flat JSON object. Fields whose value is null are omitted — that is how an
        /// optional parameter is left off a request rather than sent as an empty string.</summary>
        public static string Object(params KeyValuePair<string, string?>[] fields)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var field in fields)
            {
                if (field.Value == null) continue;
                if (!first) sb.Append(',');
                first = false;
                AppendString(sb, field.Key);
                sb.Append(':');
                AppendString(sb, field.Value);
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>A JSON object with one raw (already-encoded) member appended, for the one request
        /// that needs a non-string value.</summary>
        public static string ObjectWithRaw(string rawMember, params KeyValuePair<string, string?>[] fields)
        {
            string body = Object(fields);
            if (string.IsNullOrEmpty(rawMember)) return body;
            // body is "{...}"; splice before the closing brace.
            string inner = body.Substring(1, body.Length - 2);
            return inner.Length == 0 ? "{" + rawMember + "}" : "{" + inner + "," + rawMember + "}";
        }

        public static KeyValuePair<string, string?> Field(string name, string? value)
            => new KeyValuePair<string, string?>(name, value);

        /// <summary>
        /// Read every top-level scalar member of a JSON object into a dictionary, with numbers and
        /// booleans kept as their raw text. Returns false when the input is not an object at all.
        /// Nested objects and arrays are skipped, not descended into.
        /// </summary>
        public static bool TryReadTopLevel(string json, out IReadOnlyDictionary<string, string> fields)
            => TryReadTopLevel(json, keepContainers: false, out fields);

        /// <summary>
        /// <see cref="TryReadTopLevel(string, out IReadOnlyDictionary{string, string})"/>, also naming
        /// every member whose value is the JSON literal <c>null</c>. The plain read cannot: it keeps a
        /// string's text without its quotes, so <c>null</c> and <c>"null"</c> both read as
        /// <c>null</c>. That matters wherever a peer-authored name can be absent — <c>null</c> is a
        /// legal player name.
        /// </summary>
        public static bool TryReadTopLevelWithNulls(string json, out IReadOnlyDictionary<string, string> fields,
                                                    out HashSet<string> nulls)
        {
            nulls = new HashSet<string>(StringComparer.Ordinal);
            return TryReadTopLevel(json, keepContainers: false, out fields, nulls);
        }

        /// <summary>
        /// A nested object member, parsed with the same reader — <c>user_metadata</c> inside a JWT
        /// payload, and nothing else so far.
        ///
        /// <para>Two passes rather than one recursive reader: the outer pass keeps the member's raw
        /// text instead of skipping it, and the inner pass is the ordinary top-level read. Costs a
        /// substring on a payload of a few hundred bytes, once per sign-in, and leaves the reader every
        /// other caller uses completely unchanged.</para>
        /// </summary>
        public static bool TryReadNestedObject(string json, string name,
                                               out IReadOnlyDictionary<string, string> fields)
        {
            fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!TryReadTopLevel(json, keepContainers: true, out var outer)) return false;
            if (!outer.TryGetValue(name, out string? raw) || string.IsNullOrEmpty(raw)) return false;
            return TryReadTopLevel(raw!, out fields);
        }

        /// <summary>
        /// A nested member that is an ARRAY OF OBJECTS, each read with the ordinary top-level reader —
        /// <c>awards</c> in a pack-open receipt, and nothing else so far.
        ///
        /// <para><b>Why an array here when <c>cards</c> and <c>packs</c> are objects.</b> Those two are
        /// maps: a card id to a count, a pack line to a balance, where the key is the identity and the
        /// order carries nothing. A pack's slots are a SEQUENCE — the reveal animates them in the order
        /// the server rolled them, and two slots can legally be the same card — so keying them by
        /// anything would be inventing an identity they do not have.</para>
        ///
        /// <para>Returns false when the member is absent or is not an array. An array containing
        /// anything that is not an object is rejected whole rather than partially read: half a pack is
        /// not a smaller pack, it is a receipt that lies about what the player was given.</para>
        /// </summary>
        public static bool TryReadObjectArray(string json, string name,
                                              out IReadOnlyList<IReadOnlyDictionary<string, string>> items)
        {
            var list = new List<IReadOnlyDictionary<string, string>>();
            items = list;

            if (!TryReadRawObjectArray(json, name, out var rawItems)) return false;
            foreach (string rawItem in rawItems)
            {
                if (!TryReadTopLevel(rawItem, out var element)) return false;
                list.Add(element);
            }
            return true;
        }

        /// <summary>
        /// A nested array of objects, preserving each complete object as JSON. This is for composed
        /// replies such as a multi-pack open, whose elements themselves contain an <c>awards</c>
        /// array. The ordinary object-array reader intentionally flattens only scalar members; the
        /// raw form lets the caller hand each complete child to its existing decoder.
        /// </summary>
        public static bool TryReadRawObjectArray(string json, string name,
                                                 out IReadOnlyList<string> items)
        {
            var list = new List<string>();
            items = list;

            if (!TryReadTopLevel(json, keepContainers: true, out var outer)) return false;
            if (!outer.TryGetValue(name, out string? raw) || string.IsNullOrEmpty(raw)) return false;

            int pos = 0;
            SkipWhitespace(raw!, ref pos);
            if (pos >= raw!.Length || raw[pos] != '[') return false;
            pos++;

            SkipWhitespace(raw, ref pos);
            if (pos < raw.Length && raw[pos] == ']') return true;

            while (pos < raw.Length)
            {
                SkipWhitespace(raw, ref pos);
                if (pos >= raw.Length || raw[pos] != '{') return false;

                int start = pos;
                if (!SkipContainer(raw, ref pos)) return false;
                list.Add(raw.Substring(start, pos - start));

                SkipWhitespace(raw, ref pos);
                if (pos >= raw.Length) return false;
                if (raw[pos] == ',') { pos++; continue; }
                if (raw[pos] == ']') return true;
                return false;
            }

            return false;
        }

        /// <summary>
        /// A nested ARRAY OF STRINGS — <c>seals</c> on <c>GET /king/favor/me</c>, and nothing else so
        /// far. Returns false when the member is absent or is not an array, and rejects the whole array
        /// when any element is not a string, for the reason <see cref="TryReadRawObjectArray"/> gives.
        /// </summary>
        public static bool TryReadStringArray(string json, string name, out IReadOnlyList<string> items)
        {
            var list = new List<string>();
            items = list;

            if (!TryReadTopLevel(json, keepContainers: true, out var outer)) return false;
            if (!outer.TryGetValue(name, out string? raw) || string.IsNullOrEmpty(raw)) return false;

            int pos = 0;
            SkipWhitespace(raw!, ref pos);
            if (pos >= raw!.Length || raw[pos] != '[') return false;
            pos++;

            SkipWhitespace(raw, ref pos);
            if (pos < raw.Length && raw[pos] == ']') return true;

            while (pos < raw.Length)
            {
                SkipWhitespace(raw, ref pos);
                if (!TryReadString(raw, ref pos, out string value)) return false;
                list.Add(value);

                SkipWhitespace(raw, ref pos);
                if (pos >= raw.Length) return false;
                if (raw[pos] == ',') { pos++; continue; }
                if (raw[pos] == ']') return true;
                return false;
            }

            return false;
        }

        /// <summary>
        /// A nested ARRAY OF INTEGERS — <c>backIds</c> in a <c>GET /king/prize</c> choice, and nothing
        /// else so far. Returns false when the member is absent or is not an array, and rejects the
        /// whole array when any element is not an integer, for the reason
        /// <see cref="TryReadRawObjectArray"/> gives.
        /// </summary>
        public static bool TryReadInt64Array(string json, string name, out IReadOnlyList<long> items)
        {
            var list = new List<long>();
            items = list;

            if (!TryReadTopLevel(json, keepContainers: true, out var outer)) return false;
            if (!outer.TryGetValue(name, out string? raw) || string.IsNullOrEmpty(raw)) return false;

            int pos = 0;
            SkipWhitespace(raw!, ref pos);
            if (pos >= raw!.Length || raw[pos] != '[') return false;
            pos++;

            SkipWhitespace(raw, ref pos);
            if (pos < raw.Length && raw[pos] == ']') return true;

            while (pos < raw.Length)
            {
                SkipWhitespace(raw, ref pos);
                int start = pos;
                while (pos < raw.Length && raw[pos] != ',' && raw[pos] != ']' &&
                       raw[pos] != ' ' && raw[pos] != '\t' && raw[pos] != '\n' && raw[pos] != '\r')
                    pos++;
                if (!long.TryParse(raw.Substring(start, pos - start), NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out long value))
                    return false;
                list.Add(value);

                SkipWhitespace(raw, ref pos);
                if (pos >= raw.Length) return false;
                if (raw[pos] == ',') { pos++; continue; }
                if (raw[pos] == ']') return true;
                return false;
            }

            return false;
        }

        private static bool TryReadTopLevel(string json, bool keepContainers,
                                            out IReadOnlyDictionary<string, string> fields,
                                            ISet<string>? nulls = null)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            fields = map;
            if (string.IsNullOrEmpty(json)) return false;

            int pos = 0;
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length || json[pos] != '{') return false;
            pos++;

            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == '}') return true;

            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != '"') return false;
                if (!TryReadString(json, ref pos, out string name)) return false;

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length || json[pos] != ':') return false;
                pos++;
                SkipWhitespace(json, ref pos);
                if (pos >= json.Length) return false;

                char c = json[pos];
                if (c == '{' || c == '[')
                {
                    int containerStart = pos;
                    if (!SkipContainer(json, ref pos)) return false;
                    if (keepContainers) map[name] = json.Substring(containerStart, pos - containerStart);
                }
                else if (c == '"')
                {
                    if (!TryReadString(json, ref pos, out string value)) return false;
                    map[name] = value;
                    nulls?.Remove(name);   // a repeated member: the last one wins, as in the map
                }
                else
                {
                    int start = pos;
                    while (pos < json.Length && json[pos] != ',' && json[pos] != '}' &&
                           json[pos] != ' ' && json[pos] != '\t' && json[pos] != '\n' && json[pos] != '\r')
                        pos++;
                    if (pos == start) return false;
                    map[name] = json.Substring(start, pos - start);
                    if (nulls != null && map[name] == "null") nulls.Add(name);
                }

                SkipWhitespace(json, ref pos);
                if (pos >= json.Length) return false;
                if (json[pos] == ',') { pos++; continue; }
                if (json[pos] == '}') return true;
                return false;
            }

            return false;
        }

        /// <summary>A top-level string member with content, or false.</summary>
        public static bool TryString(IReadOnlyDictionary<string, string> fields, string name, out string value)
        {
            value = "";
            if (!fields.TryGetValue(name, out string? raw) || string.IsNullOrEmpty(raw)) return false;
            value = raw!;
            return true;
        }

        /// <summary>A top-level integer member. Accepts a JSON number and a number in quotes, because
        /// both have been seen from token endpoints in the wild.</summary>
        public static bool TryInt64(IReadOnlyDictionary<string, string> fields, string name, out long value)
        {
            value = 0;
            if (!fields.TryGetValue(name, out string? raw) || string.IsNullOrEmpty(raw)) return false;
            return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// The payload of a JWT, decoded without verifying anything. The signature is the server's
        /// business (<c>SupabaseTokenVerifier</c> against the project JWKS); this exists only so the
        /// client can tell the player which account they are in.
        /// </summary>
        public static bool TryReadJwtPayload(string jwt, out IReadOnlyDictionary<string, string> claims)
        {
            claims = new Dictionary<string, string>(StringComparer.Ordinal);
            return TryReadJwtPayloadRaw(jwt, out string json) && TryReadTopLevel(json, out claims);
        }

        /// <summary>
        /// The decoded payload of a JWT as its raw JSON text, verifying nothing. For the one claim
        /// that is a nested object (<c>user_metadata</c>) and so cannot come back through
        /// <see cref="TryReadJwtPayload"/>; pair it with <see cref="TryReadNestedObject"/>.
        /// </summary>
        public static bool TryReadJwtPayloadRaw(string jwt, out string payloadJson)
        {
            payloadJson = "";
            if (string.IsNullOrEmpty(jwt)) return false;

            int first = jwt.IndexOf('.');
            if (first < 0) return false;
            int second = jwt.IndexOf('.', first + 1);
            if (second < 0) return false;

            string segment = jwt.Substring(first + 1, second - first - 1);
            if (!TryDecodeBase64Url(segment, out byte[] bytes)) return false;

            try { payloadJson = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
            catch (ArgumentException) { return false; }

            return true;
        }

        private static bool TryDecodeBase64Url(string segment, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var sb = new StringBuilder(segment.Length + 3);
            foreach (char c in segment)
            {
                if (c == '-') sb.Append('+');
                else if (c == '_') sb.Append('/');
                else sb.Append(c);
            }
            switch (sb.Length % 4)
            {
                case 2: sb.Append("=="); break;
                case 3: sb.Append('='); break;
                case 1: return false;
            }

            try { bytes = Convert.FromBase64String(sb.ToString()); }
            catch (FormatException) { return false; }
            return true;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r')) pos++;
        }

        /// <summary>Walk past a nested object or array, honouring strings so a brace inside one does
        /// not end it early.</summary>
        private static bool SkipContainer(string s, ref int pos)
        {
            int depth = 0;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '"')
                {
                    if (!TryReadString(s, ref pos, out _)) return false;
                    continue;
                }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0) { pos++; return true; }
                }
                pos++;
            }
            return false;
        }

        private static bool TryReadString(string s, ref int pos, out string value)
        {
            value = "";
            if (pos >= s.Length || s[pos] != '"') return false;
            pos++;

            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') { value = sb.ToString(); return true; }
                if (c != '\\') { sb.Append(c); continue; }

                if (pos >= s.Length) return false;
                char esc = s[pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > s.Length) return false;
                        if (!ushort.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out ushort code)) return false;
                        sb.Append((char)code);
                        pos += 4;
                        break;
                    default: return false;
                }
            }
            return false;
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
