using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Client
{
    /// <summary>
    /// The real wss <see cref="IWireTransport"/> (b3-networked-session-design.md §2, commit 2):
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/>, present in Unity's netstandard2.1
    /// profile on Mono/IL2CPP desktop (decision 13 desktop-only). It does NOT exist on WebGL —
    /// the kickoff §4 cut already parks the web client. Frames are whole UTF-8 A5 envelopes, sent
    /// as WebSocket Text (matching B1's <c>WebSocketConnection</c>), reassembled on receive up to
    /// <see cref="ProtocolCodec.MaxMessageBytes"/>. This wraps the SAME codec the loopback carries;
    /// there is no second wire format.
    /// </summary>
    public sealed class WebSocketTransport : IWireTransport
    {
        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly int _maxBytes;
        private int _closedRaised;

        public event Action<WireCloseReason>? OnClosed;

        public WebSocketTransport(int maxBytes = ProtocolCodec.MaxMessageBytes)
        {
            _maxBytes = maxBytes;
        }

        public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);

        public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            // ArraySegment overload — the ReadOnlyMemory overload is not on the netstandard2.1
            // ClientWebSocket surface Unity ships, so stay on the portable path.
            byte[] bytes = utf8.ToArray();
            return _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            var chunk = new byte[8192];
            using var acc = new MemoryStream();
            try
            {
                while (true)
                {
                    WebSocketReceiveResult result = await _ws
                        .ReceiveAsync(new ArraySegment<byte>(chunk), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        RaiseClosed(WireCloseReason.PeerClosed);
                        return null;
                    }

                    acc.Write(chunk, 0, result.Count);
                    if (acc.Length > _maxBytes)
                    {
                        // Oversized frame — a T-class hostile input (a5 §10.5). Treat as a fault so
                        // the session drops rather than buffering unbounded.
                        RaiseClosed(WireCloseReason.Fault);
                        return null;
                    }

                    if (result.EndOfMessage) return acc.ToArray();
                }
            }
            catch (OperationCanceledException)
            {
                return null; // shutting down
            }
            catch (WebSocketException)
            {
                RaiseClosed(WireCloseReason.Fault);
                return null;
            }
        }

        public async Task CloseAsync(WireCloseReason reason)
        {
            RaiseClosed(reason);
            try
            {
                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client close", CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch (WebSocketException) { /* already gone */ }
            catch (OperationCanceledException) { /* already gone */ }
        }

        private void RaiseClosed(WireCloseReason reason)
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
                OnClosed?.Invoke(reason);
        }
    }
}
