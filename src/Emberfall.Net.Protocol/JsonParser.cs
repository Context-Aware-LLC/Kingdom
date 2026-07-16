using System;
using System.Collections.Generic;
using System.Text;

namespace Emberfall.Net.Protocol
{
    internal enum JKind { Object, Array, String, Number, Bool, Null }

    /// <summary>
    /// Parsed JSON value (small DOM — messages are bounded by <see cref="ProtocolCodec.MaxMessageBytes"/>,
    /// so a DOM is simpler and safer than a streaming reader). Numbers keep their raw text; typed
    /// accessors parse on demand and report failure instead of throwing (never-throw decode, design §7).
    /// </summary>
    internal sealed class JVal
    {
        public JKind Kind;
        public string? Str;                                   // String
        public string? Num;                                   // Number (raw integer text)
        public bool Flag;                                     // Bool
        public List<JVal>? Items;                             // Array
        public List<KeyValuePair<string, JVal>>? Props;       // Object (insertion order)

        public bool TryGet(string name, out JVal value)
        {
            if (Props != null)
                foreach (var kv in Props)
                    if (string.Equals(kv.Key, name, StringComparison.Ordinal)) { value = kv.Value; return true; }
            value = null!;
            return false;
        }

        public bool TryU64(out ulong v)
        {
            v = 0;
            return Kind == JKind.Number && Num != null && Num.Length > 0 && Num[0] != '-'
                && ulong.TryParse(Num, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        public bool TryI32(out int v)
        {
            v = 0;
            return Kind == JKind.Number && Num != null
                && int.TryParse(Num, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        public bool TryStr(out string s)
        {
            if (Kind == JKind.String && Str != null) { s = Str; return true; }
            s = null!;
            return false;
        }

        public bool TryBool(out bool b)
        {
            b = Flag;
            return Kind == JKind.Bool;
        }

        public bool IsNull => Kind == JKind.Null;
    }

    /// <summary>
    /// Strict recursive-descent parser for the wire's JSON subset (design §3). Rejects: floats and
    /// exponents (a '.', 'e', 'E' in a number is a decode error), leading zeros, duplicate keys,
    /// trailing data, depth &gt; 64, invalid UTF-8/escapes. Unknown keys are the CALLER's concern
    /// (skipped by simply not reading them — forward compatibility within a protocol version).
    /// Never throws on wire input: all failures surface as a false return + error string.
    /// </summary>
    internal static class JsonParser
    {
        private const int MaxDepth = 64;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

        public static bool TryParse(ReadOnlySpan<byte> utf8, out JVal root, out string error)
        {
            root = null!;
            string text;
            try { text = StrictUtf8.GetString(utf8); }
            catch (ArgumentException) { error = "invalid UTF-8"; return false; } // DecoderFallbackException derives from this

            int pos = 0;
            if (!ParseValue(text, ref pos, 0, out root, out error)) return false;
            SkipWs(text, ref pos);
            if (pos != text.Length) { error = $"trailing data at offset {pos}"; return false; }
            return true;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r')) pos++;
        }

        private static bool ParseValue(string s, ref int pos, int depth, out JVal val, out string error)
        {
            val = null!;
            error = "";
            if (depth > MaxDepth) { error = "depth exceeded 64"; return false; }
            SkipWs(s, ref pos);
            if (pos >= s.Length) { error = "unexpected end of input"; return false; }

            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos, depth, out val, out error);
                case '[': return ParseArray(s, ref pos, depth, out val, out error);
                case '"':
                    if (!ParseString(s, ref pos, out string str, out error)) return false;
                    val = new JVal { Kind = JKind.String, Str = str };
                    return true;
                case 't':
                    if (!Expect(s, ref pos, "true", out error)) return false;
                    val = new JVal { Kind = JKind.Bool, Flag = true };
                    return true;
                case 'f':
                    if (!Expect(s, ref pos, "false", out error)) return false;
                    val = new JVal { Kind = JKind.Bool, Flag = false };
                    return true;
                case 'n':
                    if (!Expect(s, ref pos, "null", out error)) return false;
                    val = new JVal { Kind = JKind.Null };
                    return true;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref pos, out val, out error);
                    error = $"unexpected character '{c}' at offset {pos}";
                    return false;
            }
        }

        private static bool Expect(string s, ref int pos, string literal, out string error)
        {
            if (pos + literal.Length <= s.Length && string.CompareOrdinal(s, pos, literal, 0, literal.Length) == 0)
            {
                pos += literal.Length;
                error = "";
                return true;
            }
            error = $"bad literal at offset {pos}";
            return false;
        }

        private static bool ParseObject(string s, ref int pos, int depth, out JVal val, out string error)
        {
            val = null!;
            pos++; // '{'
            var props = new List<KeyValuePair<string, JVal>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; val = new JVal { Kind = JKind.Object, Props = props }; error = ""; return true; }

            while (true)
            {
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != '"') { error = $"expected property name at offset {pos}"; return false; }
                if (!ParseString(s, ref pos, out string name, out error)) return false;
                if (!seen.Add(name)) { error = $"duplicate key '{name}'"; return false; }
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') { error = $"expected ':' at offset {pos}"; return false; }
                pos++;
                if (!ParseValue(s, ref pos, depth + 1, out JVal pv, out error)) return false;
                props.Add(new KeyValuePair<string, JVal>(name, pv));
                SkipWs(s, ref pos);
                if (pos >= s.Length) { error = "unexpected end of object"; return false; }
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; break; }
                error = $"expected ',' or '}}' at offset {pos}";
                return false;
            }
            val = new JVal { Kind = JKind.Object, Props = props };
            return true;
        }

        private static bool ParseArray(string s, ref int pos, int depth, out JVal val, out string error)
        {
            val = null!;
            pos++; // '['
            var items = new List<JVal>();
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; val = new JVal { Kind = JKind.Array, Items = items }; error = ""; return true; }

            while (true)
            {
                if (!ParseValue(s, ref pos, depth + 1, out JVal item, out error)) return false;
                items.Add(item);
                SkipWs(s, ref pos);
                if (pos >= s.Length) { error = "unexpected end of array"; return false; }
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; break; }
                error = $"expected ',' or ']' at offset {pos}";
                return false;
            }
            val = new JVal { Kind = JKind.Array, Items = items };
            return true;
        }

        private static bool ParseString(string s, ref int pos, out string result, out string error)
        {
            result = "";
            error = "";
            pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) { error = "unterminated string"; return false; }
                char c = s[pos++];
                if (c == '"') { result = sb.ToString(); return true; }
                if (c == '\\')
                {
                    if (pos >= s.Length) { error = "unterminated escape"; return false; }
                    char e = s[pos++];
                    switch (e)
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
                            if (pos + 4 > s.Length) { error = "truncated \\u escape"; return false; }
                            if (!ushort.TryParse(s.Substring(pos, 4), System.Globalization.NumberStyles.AllowHexSpecifier,
                                    System.Globalization.CultureInfo.InvariantCulture, out ushort cp))
                            { error = "bad \\u escape"; return false; }
                            sb.Append((char)cp);
                            pos += 4;
                            break;
                        default:
                            error = $"bad escape '\\{e}'";
                            return false;
                    }
                    continue;
                }
                if (c < 0x20) { error = "unescaped control character"; return false; }
                sb.Append(c);
            }
        }

        private static bool ParseNumber(string s, ref int pos, out JVal val, out string error)
        {
            val = null!;
            error = "";
            int start = pos;
            if (pos < s.Length && s[pos] == '-') pos++;
            int digitsStart = pos;
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            if (pos == digitsStart) { error = $"bad number at offset {start}"; return false; }
            // Strict: no leading zeros ("01"), and any fraction/exponent is a WIRE ERROR — the
            // float-free discipline enforced at parse time (design §3).
            if (pos - digitsStart > 1 && s[digitsStart] == '0') { error = "leading zero in number"; return false; }
            if (pos < s.Length && (s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
            { error = "non-integer number on the wire (floats are banned)"; return false; }
            val = new JVal { Kind = JKind.Number, Num = s.Substring(start, pos - start) };
            return true;
        }
    }
}
