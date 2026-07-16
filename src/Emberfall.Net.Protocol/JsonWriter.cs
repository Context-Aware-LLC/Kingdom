using System;
using System.Text;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// The wire's canonical JSON emitter (a5-wire-protocol-design.md §3). Strict subset: objects,
    /// arrays, RFC 8259-escaped strings, integers, booleans, null. <b>There is deliberately no
    /// floating-point method</b> — the ICanonicalWriter philosophy applied to the wire: a float in
    /// a message cannot even be expressed. No indentation, no spaces — encoding the same value
    /// twice yields identical bytes (byte-stability, design §10.6).
    /// </summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(256);
        private ulong _firstStack = 0; // bit per depth: 1 = next element is the first at this depth
        private int _depth;

        private void Comma()
        {
            if (_depth == 0) return;
            ulong bit = 1UL << (_depth - 1);
            if ((_firstStack & bit) != 0) _firstStack &= ~bit;
            else _sb.Append(',');
        }

        private void Push()
        {
            if (_depth >= 64) throw new InvalidOperationException("JsonWriter depth exceeded 64.");
            _depth++;
            _firstStack |= 1UL << (_depth - 1);
        }

        public void BeginObject() { Comma(); _sb.Append('{'); Push(); }
        public void EndObject() { _depth--; _sb.Append('}'); }
        public void BeginArray() { Comma(); _sb.Append('['); Push(); }
        public void EndArray() { _depth--; _sb.Append(']'); }

        /// <summary>Property name; must be followed by exactly one value write.</summary>
        public void Name(string name)
        {
            Comma();
            WriteEscaped(name);
            _sb.Append(':');
            // The value that follows must not emit a comma: re-mark this depth as "first".
            _firstStack |= 1UL << (_depth - 1);
        }

        public void Str(string? s)
        {
            Comma();
            if (s is null) _sb.Append("null");
            else WriteEscaped(s);
        }

        public void I32(int v) { Comma(); _sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        public void U64(ulong v) { Comma(); _sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        public void Bool(bool v) { Comma(); _sb.Append(v ? "true" : "false"); }
        public void Null() { Comma(); _sb.Append("null"); }

        private void WriteEscaped(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            _sb.Append("\\u");
                            _sb.Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public byte[] ToUtf8() => Encoding.UTF8.GetBytes(_sb.ToString());
        public override string ToString() => _sb.ToString();
    }
}
