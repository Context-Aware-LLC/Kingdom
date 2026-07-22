using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Client;

namespace Emberfall.Net.Client.Tests.Support
{
    /// <summary>
    /// An <see cref="IWireTransport"/> decorator that injects the two wire faults the loopback
    /// transport structurally cannot produce (b3 §11 test 4a's blind spot): an outbound frame that
    /// never reaches the socket, and an inbound frame that fails to decode.
    ///
    /// <para>The point is the REAL transport. <c>LoopbackTransport.SendAsync</c> enqueues
    /// synchronously under a lock and returns <c>Task.CompletedTask</c>, so a lost send cannot
    /// happen there — which is why Test4a was green while the real socket wedged. This wraps the
    /// real <see cref="WebSocketTransport"/> and drops the frame on the way past.</para>
    ///
    /// <para><see cref="DropOutbound"/> models the swallowed throw at
    /// <c>NetworkedMatchSession.SendUnlocked</c> (<c>_ = _transport.SendAsync(...)</c>): the caller
    /// cannot tell the frame never left. Silently discarding it here is behaviourally identical to
    /// the discarded task faulting, and is deterministic.</para>
    /// </summary>
    public sealed class FaultInjectingTransport : IWireTransport
    {
        private readonly IWireTransport _inner;
        private int _corruptInboundCount;
        private int _dropResyncCount;

        public FaultInjectingTransport(IWireTransport inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _inner.OnClosed += r => OnClosed?.Invoke(r);
        }

        /// <summary>Outbound resync frames swallowed before any are allowed through.</summary>
        public int ResyncFramesDropped { get; private set; }

        /// <summary>Outbound resync frames that reached the real socket (retries, once they exist).</summary>
        public int ResyncFramesDelivered { get; private set; }

        /// <summary>Corrupt the next <paramref name="count"/> inbound frames so the client's decode
        /// fails — the §4.5 <c>resync(DecodeError)</c> trigger.</summary>
        public void CorruptNextInbound(int count = 1) => Interlocked.Exchange(ref _corruptInboundCount, count);

        /// <summary>Swallow the next <paramref name="count"/> outbound resync frames. Everything else
        /// (heartbeats, intents) still goes out, so liveness is unaffected and the wedge is isolated.</summary>
        public void DropNextResync(int count = 1) => Interlocked.Exchange(ref _dropResyncCount, count);

        public Task ConnectAsync(Uri uri, CancellationToken ct) => _inner.ConnectAsync(uri, ct);

        public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            if (IsResync(utf8.Span))
            {
                if (Interlocked.Decrement(ref _dropResyncCount) >= 0)
                {
                    ResyncFramesDropped++;
                    return Task.CompletedTask;   // the frame never reaches the socket, and nobody is told
                }
                Interlocked.Exchange(ref _dropResyncCount, 0);
                ResyncFramesDelivered++;
            }
            return _inner.SendAsync(utf8, ct);
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            byte[]? frame = await _inner.ReceiveAsync(ct).ConfigureAwait(false);
            if (frame is null) return null;
            if (Interlocked.Decrement(ref _corruptInboundCount) >= 0)
            {
                // Well-formed UTF-8, no "type" field ⇒ ProtocolCodec.Decode fails ⇒ resync(DecodeError).
                return Encoding.UTF8.GetBytes("{\"v\":1,\"matchId\":\"x\",\"seq\":1}");
            }
            Interlocked.Exchange(ref _corruptInboundCount, 0);
            return frame;
        }

        public Task CloseAsync(WireCloseReason reason) => _inner.CloseAsync(reason);

        public event Action<WireCloseReason>? OnClosed;

        private static bool IsResync(ReadOnlySpan<byte> utf8)
            => Encoding.UTF8.GetString(utf8).Contains("\"type\":\"resync\"", StringComparison.Ordinal);
    }
}
