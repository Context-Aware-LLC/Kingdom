using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Emberfall.Net.Server.Transport
{
    /// <summary>
    /// <see cref="IServerConnection"/> over a real WebSocket (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md §4 outbound
    /// rule, RC F5): a BOUNDED writer queue + one pump task per connection. <see cref="TryEnqueue"/>
    /// never blocks; a full queue (slow client) or dead writer returns false and the session vacates
    /// the seat. Nothing here ever runs on the session's dispatch thread.
    /// </summary>
    public sealed class WebSocketConnection : IServerConnection
    {
        private const int WriterQueueCapacity = 256;

        private readonly WebSocket _socket;
        private readonly Channel<byte[]> _outbound;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile bool _dead;

        public WebSocketConnection(WebSocket socket)
        {
            _socket = socket;
            _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(WriterQueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,   // TryWrite returns false when full — no waiting
            });
            PumpTask = Task.Run(PumpAsync);
        }

        /// <summary>Completes when the writer pump exits (socket closed or faulted).</summary>
        public Task PumpTask { get; }

        public bool TryEnqueue(byte[] frame)
        {
            if (_dead) return false;
            return _outbound.Writer.TryWrite(frame);
        }

        public void Close(int closeCode, string reason)
        {
            _outbound.Writer.TryComplete();
            // The pump drains what it can, then closes with the requested code.
            _closeCode = closeCode;
            _closeReason = reason;
        }

        private volatile int _closeCode = (int)WebSocketCloseStatus.NormalClosure;
        private string _closeReason = "bye";

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var frame in _outbound.Reader.ReadAllAsync(_cts.Token))
                {
                    await _socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, _cts.Token);
                }
                if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _socket.CloseAsync((WebSocketCloseStatus)_closeCode, _closeReason, closeTimeout.Token);
                }
            }
            catch (Exception)
            {
                // Socket faulted mid-send: the receive loop (or heartbeat timeout) reports the
                // disconnect to the session; our only job is to stop writing.
            }
            finally
            {
                _dead = true;
                _outbound.Writer.TryComplete();
            }
        }

        /// <summary>Hard-stop the pump (endpoint teardown).</summary>
        public void Abort()
        {
            _dead = true;
            _outbound.Writer.TryComplete();
            _cts.Cancel();
        }
    }
}
