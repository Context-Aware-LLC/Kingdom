using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Emberfall.Core.Serialization
{
    /// <summary>
    /// The canonical primitive sink (dev-docs/architecture/determinism-and-projection.md:13). Implementations MUST write every
    /// integer little-endian and strings length-prefixed UTF-8. There is deliberately NO float
    /// method — a float in state cannot even be expressed through this sink; the banned-API
    /// reflection scan is the static twin that keeps floats out of the state types.
    /// </summary>
    public interface ICanonicalWriter
    {
        void I32(int v);
        void U64(ulong v);
        void Bool(bool v);
        void Enum<TEnum>(TEnum v) where TEnum : struct, Enum;
        void NullableEnum<TEnum>(TEnum? v) where TEnum : struct, Enum;
        void NullableI32(int? v);
        void Str(string? s);
    }

    /// <summary>
    /// Shared canonical encoding for the primitives so every sink emits IDENTICAL bytes — the
    /// encoding lives once, sinks only differ in where the bytes go.
    /// </summary>
    public abstract class CanonicalWriterBase : ICanonicalWriter
    {
        protected abstract void Append(ReadOnlySpan<byte> bytes);

        public void I32(int v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            Append(b);
        }

        public void U64(ulong v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, v);
            Append(b);
        }

        public void Bool(bool v)
        {
            Span<byte> b = stackalloc byte[1];
            b[0] = v ? (byte)1 : (byte)0;
            Append(b);
        }

        public void Enum<TEnum>(TEnum v) where TEnum : struct, Enum
            => I32(Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture));

        public void NullableEnum<TEnum>(TEnum? v) where TEnum : struct, Enum
        {
            if (v.HasValue) { Bool(true); Enum(v.Value); }
            else Bool(false);
        }

        public void NullableI32(int? v)
        {
            if (v.HasValue) { Bool(true); I32(v.Value); }
            else Bool(false);
        }

        public void Str(string? s)
        {
            if (s is null) { I32(-1); return; }      // distinguish null from empty
            var bytes = Encoding.UTF8.GetBytes(s);
            I32(bytes.Length);                        // length-prefixed UTF-8 (§4.2)
            Append(bytes);
        }
    }

    /// <summary>
    /// Streams canonical bytes straight into an <see cref="IncrementalHash"/> — no intermediate
    /// buffer. The only sink the hasher needs (dev-docs/architecture/determinism-and-projection.md:13).
    /// </summary>
    public sealed class HashSink : CanonicalWriterBase
    {
        private readonly IncrementalHash _hash;
        public HashSink(IncrementalHash hash) => _hash = hash;
        protected override void Append(ReadOnlySpan<byte> bytes) => _hash.AppendData(bytes);
    }

    /// <summary>
    /// Buffers the canonical byte stream for debugging / replay-audit consumers. Not on any
    /// critical path; provided because it is nearly free once the sink abstraction exists.
    /// </summary>
    public sealed class BufferSink : CanonicalWriterBase
    {
        private readonly List<byte> _bytes = new List<byte>();

        protected override void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes) _bytes.Add(b);
        }

        public byte[] ToArray() => _bytes.ToArray();
        public int Length => _bytes.Count;
    }
}
