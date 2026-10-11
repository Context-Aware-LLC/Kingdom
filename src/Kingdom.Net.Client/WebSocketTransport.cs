using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Client
{
    /// <summary>
    /// The real wss <see cref="IWireTransport"/> (dev-docs/architecture/system-overview.md:17). A hand-rolled
    /// RFC6455 client over <see cref="TcpClient"/> + <see cref="SslStream"/> — NOT
    /// <c>System.Net.WebSockets.ClientWebSocket</c>.
    ///
    /// WHY hand-rolled (2026-07-19): Unity's Mono <c>ClientWebSocket.ReceiveAsync</c> silently HANGS
    /// on inbound frames that span multiple TLS records (match_start is ~14 KB) — the wss handshake
    /// completes, the server sends the frame, .NET/CoreCLR receives it fine, but the editor never
    /// surfaces it, so connect never reaches Live. Proven with a raw-<see cref="SslStream"/> probe:
    /// the TLS stream delivers the exact bytes under Mono; only ClientWebSocket's frame layer is
    /// broken. This impl reads frames straight off the stream (reassembling continuations and TLS
    /// segments), so it works identically under Mono editor, IL2CPP standalone, and mobile. WebGL is
    /// parked (decision 13); it would need the JS socket bridge, not this.
    ///
    /// Frames are whole UTF-8 A5 envelopes, sent as WebSocket Text with a client mask (RFC6455 §5.3
    /// requires client→server frames be masked; Kestrel rejects unmasked ones), reassembled on
    /// receive up to <see cref="ProtocolCodec.MaxMessageBytes"/>. Same codec the loopback carries —
    /// no second wire format.
    /// </summary>
    public sealed class WebSocketTransport : IWireTransport
    {
        // RFC6455 §1.3 handshake GUID.
        private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly int _maxBytes;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        private TcpClient? _tcp;
        private Stream? _stream;
        private int _closedRaised;

        public event Action<WireCloseReason, string?>? OnClosed;

        public WebSocketTransport(int maxBytes = ProtocolCodec.MaxMessageBytes)
        {
            _maxBytes = maxBytes;
        }

        // ── Connect / handshake ─────────────────────────────────────────────────────

        public async Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            bool tls = uri.Scheme == "wss";
            int port = uri.IsDefaultPort ? (tls ? 443 : 80) : uri.Port;

            var tcp = new TcpClient { NoDelay = true };
            _tcp = tcp;
            await tcp.ConnectAsync(uri.Host, port).ConfigureAwait(false);
            Stream stream = tcp.GetStream();
            if (tls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(uri.Host).ConfigureAwait(false);
                stream = ssl;
            }
            _stream = stream;

            byte[] nonce = new byte[16];
            _rng.GetBytes(nonce);
            string secKey = Convert.ToBase64String(nonce);

            string req = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                         $"Host: {uri.Host}\r\n" +
                         "Upgrade: websocket\r\n" +
                         "Connection: Upgrade\r\n" +
                         $"Sec-WebSocket-Key: {secKey}\r\n" +
                         "Sec-WebSocket-Version: 13\r\n\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(req);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            string header = await ReadHttpHeaderAsync(stream, ct).ConfigureAwait(false);
            if (!header.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                throw new IOException("wss handshake not 101: " + FirstLine(header));

            string expected = Convert.ToBase64String(
                SHA1.Create().ComputeHash(Encoding.ASCII.GetBytes(secKey + WsGuid)));
            if (header.IndexOf(expected, StringComparison.Ordinal) < 0)
                throw new IOException("wss handshake Sec-WebSocket-Accept mismatch");
        }

        private static string FirstLine(string s)
        {
            int i = s.IndexOf('\r');
            return i < 0 ? s : s.Substring(0, i);
        }

        /// <summary>Read response headers byte-by-byte so we never swallow the first frame's bytes.</summary>
        private static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken ct)
        {
            var sb = new StringBuilder(256);
            var one = new byte[1];
            while (true)
            {
                int n = await stream.ReadAsync(one, 0, 1, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("stream closed during handshake");
                sb.Append((char)one[0]);
                int L = sb.Length;
                if (L >= 4 && sb[L - 1] == '\n' && sb[L - 2] == '\r' && sb[L - 3] == '\n' && sb[L - 4] == '\r')
                    return sb.ToString();
                if (L > 16384) throw new IOException("handshake header too large");
            }
        }

        // ── Send ────────────────────────────────────────────────────────────────────

        public async Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            Stream? stream = _stream;
            if (stream is null) throw new InvalidOperationException("SendAsync before ConnectAsync");
            byte[] frame = BuildClientFrame(0x1 /* Text */, utf8.Span);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        /// <summary>A single FIN client frame, masked per RFC6455 §5.3.</summary>
        private byte[] BuildClientFrame(int opcode, ReadOnlySpan<byte> payload)
        {
            int len = payload.Length;
            int headerLen = 2 + (len <= 125 ? 0 : len <= ushort.MaxValue ? 2 : 8) + 4 /* mask */;
            var frame = new byte[headerLen + len];
            frame[0] = (byte)(0x80 | (opcode & 0x0F)); // FIN + opcode
            int p;
            if (len <= 125) { frame[1] = (byte)(0x80 | len); p = 2; }
            else if (len <= ushort.MaxValue)
            {
                frame[1] = 0x80 | 126;
                frame[2] = (byte)(len >> 8); frame[3] = (byte)len; p = 4;
            }
            else
            {
                frame[1] = 0x80 | 127;
                long ll = len;
                for (int i = 0; i < 8; i++) frame[2 + i] = (byte)(ll >> (56 - 8 * i));
                p = 10;
            }
            byte[] maskBytes = new byte[4];
            _rng.GetBytes(maskBytes);
            frame[p] = maskBytes[0]; frame[p + 1] = maskBytes[1]; frame[p + 2] = maskBytes[2]; frame[p + 3] = maskBytes[3];
            p += 4;
            for (int i = 0; i < len; i++) frame[p + i] = (byte)(payload[i] ^ maskBytes[i & 3]);
            return frame;
        }

        // ── Receive ───────────────────────────────────────────────────────────────

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            Stream? stream = _stream;
            if (stream is null) return null;
            using var acc = new MemoryStream();
            try
            {
                while (true)
                {
                    byte b0 = await ReadByteAsync(stream, ct).ConfigureAwait(false);
                    bool fin = (b0 & 0x80) != 0;
                    int opcode = b0 & 0x0F;

                    byte b1 = await ReadByteAsync(stream, ct).ConfigureAwait(false);
                    bool masked = (b1 & 0x80) != 0; // server frames MUST be unmasked; tolerate either
                    long len = b1 & 0x7F;
                    if (len == 126)
                    {
                        byte[] e = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
                        len = (e[0] << 8) | e[1];
                    }
                    else if (len == 127)
                    {
                        byte[] e = await ReadExactAsync(stream, 8, ct).ConfigureAwait(false);
                        len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
                    }
                    byte[]? mask = masked ? await ReadExactAsync(stream, 4, ct).ConfigureAwait(false) : null;

                    if (len > _maxBytes || acc.Length + len > _maxBytes)
                    {
                        // Oversized — a T-class hostile input (a5 §10.5). Fault rather than buffer unbounded.
                        RaiseClosed(WireCloseReason.Fault);
                        return null;
                    }

                    byte[] payload = await ReadExactAsync(stream, (int)len, ct).ConfigureAwait(false);
                    if (mask != null)
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                    switch (opcode)
                    {
                        case 0x8: // Close
                            // #232: RFC6455 §5.5.1 — a close frame's body is [2-byte status code]
                            // [optional UTF-8 reason]. This used to be read into `payload` and
                            // dropped; the server's refusal text (MatchGate.CloseReason) lives
                            // entirely in that reason, so every close read identically upstream —
                            // "Connection closed before the match started (Forfeited)" regardless
                            // of which of the server's four refusals actually fired.
                            RaiseClosed(WireCloseReason.PeerClosed, ParseCloseReason(payload));
                            return null;
                        case 0x9: // Ping — echo a Pong, keep reading.
                            await SendControlAsync(0xA, payload, ct).ConfigureAwait(false);
                            continue;
                        case 0xA: // Pong — ignore.
                            continue;
                        default:  // 0x0 continuation, 0x1 text, 0x2 binary — accumulate.
                            acc.Write(payload, 0, payload.Length);
                            if (fin) return acc.ToArray();
                            continue;
                    }
                }
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception) when (!(ct.IsCancellationRequested)) { RaiseClosed(WireCloseReason.Fault); return null; }
        }

        private async Task SendControlAsync(int opcode, byte[] payload, CancellationToken ct)
        {
            Stream? stream = _stream;
            if (stream is null) return;
            byte[] frame = BuildClientFrame(opcode, payload);
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken ct)
            => (await ReadExactAsync(stream, 1, ct).ConfigureAwait(false))[0];

        private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
        {
            var buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = await stream.ReadAsync(buf, off, count - off, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("stream closed mid-frame");
                off += n;
            }
            return buf;
        }

        // ── Close ───────────────────────────────────────────────────────────────────

        public async Task CloseAsync(WireCloseReason reason)
        {
            RaiseClosed(reason);
            Stream? stream = _stream;
            if (stream != null)
            {
                try
                {
                    // Best-effort Close frame (empty body), then tear down the socket.
                    byte[] frame = BuildClientFrame(0x8, ReadOnlySpan<byte>.Empty);
                    await _writeLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                    finally { _writeLock.Release(); }
                }
                catch { /* already gone */ }
            }
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
        }

        private void RaiseClosed(WireCloseReason reason, string? detail = null)
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
                OnClosed?.Invoke(reason, detail);
        }

        /// <summary>Decode a close frame's optional reason text. Malformed input (too short to hold
        /// even the status code, or bytes that aren't valid UTF-8) yields null rather than throwing —
        /// a badly-formed close frame is still a close, and losing the human-readable reason is far
        /// better than losing the close itself.</summary>
        private static string? ParseCloseReason(byte[] payload)
        {
            const int StatusCodeLen = 2;
            if (payload.Length <= StatusCodeLen) return null;
            try
            {
                string text = Encoding.UTF8.GetString(payload, StatusCodeLen, payload.Length - StatusCodeLen);
                return text.Length == 0 ? null : text;
            }
            catch (DecoderFallbackException) { return null; }
        }
    }
}
