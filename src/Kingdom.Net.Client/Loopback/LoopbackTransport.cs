using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Client.Loopback
{
    /// <summary>
    /// An in-memory <see cref="IWireTransport"/> — a linked queue pair carrying the same UTF-8 A5
    /// envelopes a real socket carries, no bypass and no second codec
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b3-networked-session-design.md §8). The peer end is B1's REAL <c>MatchSession</c>, driven
    /// through its real <c>Dispatch</c> path by <c>LoopbackServerPeer</c> in the test tree — so
    /// "fake" is the wrong word: there is a loopback transport and the real server core (§8, OQ-C
    /// ratified). It removes the client's dependency on deployment completely.
    ///
    /// Dependency-free by design (netstandard2.1, Unity-safe): a lock + queue, NOT
    /// <c>System.Threading.Channels</c> (an external package the client assembly refuses). The two
    /// knobs a socket cannot offer — injectable latency and drop/jitter for Phase 3's
    /// 100/200/400ms + loss gate — attach at <see cref="EnqueueInbound"/> in commit 2; commit 1
    /// delivers immediately.
    /// </summary>
    public sealed class LoopbackTransport : IWireTransport
    {
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _inbound = new Queue<byte[]>();   // server → client
        private readonly Queue<byte[]> _outbound = new Queue<byte[]>();  // client → server
        private TaskCompletionSource<byte[]?>? _waiter;
        private bool _serverClosed;
        private WireCloseReason _serverCloseReason = WireCloseReason.PeerClosed;
        private string? _serverCloseDetail;
        private bool _closed;

        public event Action<WireCloseReason, string?>? OnClosed;

        /// <summary>Phase-3 impairment injection point (§8, §10 soak/loss gate): return true to DROP
        /// a server→client frame. Null (default) delivers everything, which is what keeps commit-1
        /// determinism intact. Latency/jitter attach here too once the reconnect timer path (commit
        /// 3+) can recover from the induced loss — until then the soak runs it at zero.</summary>
        public Func<byte[], bool>? DropInbound { get; set; }

        public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_closed) throw new InvalidOperationException("transport closed");
                _outbound.Enqueue(utf8.ToArray());
            }
            return Task.CompletedTask;
        }

        public Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            TaskCompletionSource<byte[]?>? waiter = null;
            WireCloseReason? peerClose = null;
            string? peerCloseDetail = null;
            lock (_gate)
            {
                if (_inbound.Count > 0) return Task.FromResult<byte[]?>(_inbound.Dequeue());
                if (_serverClosed)
                {
                    peerClose = _serverCloseReason;
                    peerCloseDetail = _serverCloseDetail;
                }
                else
                {
                    waiter = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiter = waiter;
                }
            }
            if (peerClose is WireCloseReason reason)
            {
                RaiseClosed(reason, peerCloseDetail);
                return Task.FromResult<byte[]?>(null);
            }
            if (ct.CanBeCanceled)
                ct.Register(() => waiter!.TrySetResult(null));
            return waiter!.Task;
        }

        public Task CloseAsync(WireCloseReason reason)
        {
            RaiseClosed(reason);
            return Task.CompletedTask;
        }

        private void RaiseClosed(WireCloseReason reason, string? detail = null)
        {
            bool first;
            lock (_gate) { first = !_closed; _closed = true; }
            if (first) OnClosed?.Invoke(reason, detail);
        }

        // ── Peer (server-side) surface — used by LoopbackServerPeer in the test tree ──────────

        /// <summary>Drain one frame the client sent (client → server). False when none pending.</summary>
        public bool TryDequeueOutbound(out byte[] frame)
        {
            lock (_gate)
            {
                if (_outbound.Count > 0) { frame = _outbound.Dequeue(); return true; }
            }
            frame = Array.Empty<byte>();
            return false;
        }

        /// <summary>Deliver one frame to the client (server → client). The client's receive pump
        /// picks it up on its next <see cref="ReceiveAsync"/>.</summary>
        public void EnqueueInbound(byte[] frame)
        {
            if (DropInbound is not null && DropInbound(frame)) return; // Phase-3 loss injection (§8)
            TaskCompletionSource<byte[]?>? waiter = null;
            lock (_gate)
            {
                if (_waiter is not null) { waiter = _waiter; _waiter = null; }
                else _inbound.Enqueue(frame);
            }
            waiter?.TrySetResult(frame);
        }

        /// <summary>The server end closed the socket: the client's <see cref="ReceiveAsync"/> returns
        /// null (peer closed), which the session treats as a drop. <paramref name="detail"/> mirrors
        /// the WS close-frame reason text a real socket would carry (#232) — optional, since most
        /// callers (bot seats, the soak) close with no reason to report.</summary>
        public void ServerClose(WireCloseReason reason = WireCloseReason.PeerClosed, string? detail = null)
        {
            TaskCompletionSource<byte[]?>? waiter = null;
            lock (_gate)
            {
                _serverClosed = true;
                _serverCloseReason = reason;
                _serverCloseDetail = detail;
                if (_waiter is not null) { waiter = _waiter; _waiter = null; }
            }
            // With no buffered frames, waking an active receive is the close boundary and can
            // signal immediately. Otherwise ReceiveAsync drains the queue first and raises the
            // event only when it reaches EOF, matching an orderly socket close.
            //
            // The event goes BEFORE the wake, as a real socket orders them (WebSocketTransport
            // raises, then returns null). Woken first, the reader can act on a close whose reason
            // it has not been told yet — and a reconnecting session reads a missing reason as "the
            // network went" and retries a close the server meant.
            if (waiter is not null) RaiseClosed(reason, detail);
            waiter?.TrySetResult(null);
        }
    }
}
