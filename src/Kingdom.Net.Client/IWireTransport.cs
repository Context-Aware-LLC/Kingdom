using System;
using System.Threading;
using System.Threading.Tasks;

namespace Kingdom.Net.Client
{
    /// <summary>Why a wire closed — the client maps this to a <see cref="ConnectionState"/> exit
    /// (dev-docs/architecture/system-overview.md:17).</summary>
    public enum WireCloseReason
    {
        /// <summary>Clean close initiated locally.</summary>
        Local,
        /// <summary>Peer closed the socket.</summary>
        PeerClosed,
        /// <summary>Transport fault (socket error, writer death).</summary>
        Fault,
    }

    /// <summary>
    /// The transport seam (dev-docs/architecture/system-overview.md:17). <see cref="NetworkedMatchSession"/>
    /// binds to THIS, never to a concrete <c>ClientWebSocket</c>, so the same session drives the
    /// real socket (<c>WebSocketTransport</c>, commit 2) and the in-memory
    /// <see cref="Loopback.LoopbackTransport"/> that peers against B1's real <c>MatchSession</c> in
    /// tests (§8). Frames are whole decoded-length A5 envelopes — the same UTF-8 bytes both
    /// implementations carry; there is no bypass and no second codec.
    /// </summary>
    public interface IWireTransport
    {
        Task ConnectAsync(Uri uri, CancellationToken ct);

        Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct);

        /// <summary>One whole frame, or null when the peer closed.</summary>
        Task<byte[]?> ReceiveAsync(CancellationToken ct);

        Task CloseAsync(WireCloseReason reason);

        /// <summary>Fires once, on either end of the wire closing. <c>detail</c> is the raw text a
        /// peer-initiated close carried (the server's WS close-frame reason, e.g. "deck rejected:
        /// too many copies of X") — null for a local close or a transport fault, which have no such
        /// text (#232: this used to be thrown away entirely, so every refusal looked identical).</summary>
        event Action<WireCloseReason, string?> OnClosed;
    }
}
