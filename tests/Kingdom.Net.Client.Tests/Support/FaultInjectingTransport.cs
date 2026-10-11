using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Client;

namespace Kingdom.Net.Client.Tests.Support
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
    /// <para><see cref="DropNextResync"/> models the swallowed throw at
    /// <c>NetworkedMatchSession.SendUnlocked</c> (<c>_ = _transport.SendAsync(...)</c>): the caller
    /// cannot tell the frame never left. Silently discarding it here is behaviourally identical to
    /// the discarded task faulting, and is deterministic.</para>
    /// </summary>
    public sealed class FaultInjectingTransport : IWireTransport
    {
        private readonly IWireTransport _inner;
        private readonly TaskCompletionSource<bool> _firstResyncDropped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstResyncDelivered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The wedge window: true from the instant the first resync is swallowed until the retry
        // reaches the socket. While set, ReceiveAsync discards every inbound frame so no stray
        // server snapshot can clear _resyncPending and short-circuit the retry under test. Volatile
        // because it is written on the client's send-lock thread and read on the pump thread.
        private volatile bool _wedgeActive;
        private int _corruptInboundCount;
        private int _dropResyncCount;

        public FaultInjectingTransport(IWireTransport inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _inner.OnClosed += (r, detail) => OnClosed?.Invoke(r, detail);
        }

        /// <summary>Outbound resync frames swallowed before any are allowed through.</summary>
        public int ResyncFramesDropped { get; private set; }

        /// <summary>Outbound resync frames that reached the real socket (retries, once they exist).</summary>
        public int ResyncFramesDelivered { get; private set; }

        /// <summary>Completes the instant the FIRST outbound resync is swallowed — i.e. the wedge is
        /// formed. The client only ever sends a resync AFTER <c>RequestResync</c> has set its pending
        /// flag, so a swallowed resync is itself proof the client is wedged and waiting on an answer
        /// nobody will send. The test awaits this event rather than polling for the drop and then
        /// reading the pending flag live — under Stage 3 CPU starvation that poll could return only
        /// after the auto-delivered retry had already cleared the flag, which was the precondition
        /// flake (b3 §11: no <c>Task.Delay</c> deciding timing under test). Fires asynchronously so
        /// the awaiting test never runs inline on the client's send-lock stack.</summary>
        public Task FirstResyncDropped => _firstResyncDropped.Task;

        /// <summary>Completes the instant the FIRST outbound resync reaches the real socket — i.e. the
        /// retry that <see cref="ResyncFramesDelivered"/> witnesses. The test awaits this instead of
        /// polling <c>ResyncFramesDelivered</c> against a wall-clock deadline: the retry is driven by
        /// the client's 1s timer loop, whose <c>Task.Delay</c> continuation is starved under the Stage
        /// 3 four-assembly batch, so a tight real-time poll could expire before a loop that is simply
        /// running slow (not broken) got its turn — the ~40% delivery-window flake. An awaited TCS
        /// completes the moment the send happens, however late the scheduler was, so only a genuinely
        /// absent retry (a real product wedge) can still time out. Fires asynchronously so the awaiting
        /// test never runs inline on the client's send-lock stack.</summary>
        public Task FirstResyncDelivered => _firstResyncDelivered.Task;

        /// <summary>Corrupt the next <paramref name="count"/> inbound frames so the client's decode
        /// fails — the §4.5 <c>resync(DecodeError)</c> trigger.</summary>
        public void CorruptNextInbound(int count = 1) => Interlocked.Exchange(ref _corruptInboundCount, count);

        /// <summary>Swallow the next <paramref name="count"/> outbound resync frames, then let the
        /// rest through — so exactly the loss happens, the client's retry is the first frame allowed
        /// out (what <see cref="ResyncFramesDelivered"/> witnesses), and the §4.5 budget is never
        /// exhausted. Everything else (heartbeats, intents) always goes out, so liveness is
        /// unaffected and the wedge is isolated to resync.</summary>
        public void DropNextResync(int count = 1) => Interlocked.Exchange(ref _dropResyncCount, count);

        public Task ConnectAsync(Uri uri, CancellationToken ct) => _inner.ConnectAsync(uri, ct);

        public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            if (IsResync(utf8.Span))
            {
                if (Interlocked.Decrement(ref _dropResyncCount) >= 0)
                {
                    ResyncFramesDropped++;
                    // Form the wedge: swallow this frame and, from here until the retry lands, discard
                    // inbound frames (see ReceiveAsync) so nothing but the retry can resolve the resync.
                    _wedgeActive = true;
                    // Announce the wedge is formed. TrySetResult is idempotent (only the first drop
                    // matters), and RunContinuationsAsynchronously keeps the awaiting test off this
                    // stack — this call runs inside the client's send lock.
                    _firstResyncDropped.TrySetResult(true);
                    return Task.CompletedTask;   // the frame never reaches the socket, and nobody is told
                }
                Interlocked.Exchange(ref _dropResyncCount, 0);
                ResyncFramesDelivered++;
                // The retry reached the wire — release the wedge so the server's answer (and any
                // buffered frame behind it) is delivered and the client recovers. Cleared before the
                // signal so the awaiting test never observes delivery while inbound is still held.
                _wedgeActive = false;
                // Announce the retry reached the wire — symmetric to the drop signal above, and
                // idempotent, so the test observes delivery event-driven rather than polling a clock.
                _firstResyncDelivered.TrySetResult(true);
            }
            return _inner.SendAsync(utf8, ct);
        }

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            while (true)
            {
                byte[]? frame = await _inner.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame is null) return null;

                if (Interlocked.Decrement(ref _corruptInboundCount) >= 0)
                {
                    // Well-formed UTF-8, no "type" field ⇒ ProtocolCodec.Decode fails ⇒ resync(DecodeError).
                    return Encoding.UTF8.GetBytes("{\"v\":1,\"matchId\":\"x\",\"seq\":1}");
                }
                Interlocked.Exchange(ref _corruptInboundCount, 0);

                // Hold the wedge. Between the first resync being swallowed and the retry reaching the
                // socket, discard every inbound frame. The server ships snapshot-then-events per
                // applied command (MatchSession.cs) and leftover turn-start frames can still be in
                // flight, so a valid SnapshotMsg here would clear _resyncPending (it is exempt from the
                // drop guard while pending) and let the client "recover" WITHOUT ever retrying — a path
                // the fixture's own docstring flags as masking the bug. Under load the starved retry
                // timer loses that race (delivered=0) and the test flakes. Discarding holds the wedge
                // until the timer-driven retry fires; that retry clears _wedgeActive before its answer
                // can arrive (a full round-trip later), so the answer is never discarded.
                if (_wedgeActive) continue;

                return frame;
            }
        }

        public Task CloseAsync(WireCloseReason reason) => _inner.CloseAsync(reason);

        public event Action<WireCloseReason, string?>? OnClosed;

        private static bool IsResync(ReadOnlySpan<byte> utf8)
            => Encoding.UTF8.GetString(utf8).Contains("\"type\":\"resync\"", StringComparison.Ordinal);
    }
}
