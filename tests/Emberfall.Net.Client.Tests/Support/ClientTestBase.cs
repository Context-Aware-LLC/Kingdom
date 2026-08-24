using System;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Tests.Support;

namespace Emberfall.Net.Client.Tests
{
    /// <summary>Shared wiring for the B3 client-seam tests: connect a pair of
    /// <see cref="NetworkedMatchSession"/> against the peer's REAL <c>MatchSession</c> over
    /// loopback, and the two-line mulligan confirm.</summary>
    public abstract class ClientTestBase
    {
        protected static readonly Uri LoopbackUri = new Uri("loopback://match");

        /// <summary>Bind, connect both clients, start the match, and resolve when both have their
        /// initial snapshot. Seat 0 rides Transport0, seat 1 rides Transport1.</summary>
        protected static async Task StartPairAsync(LoopbackServerPeer peer, NetworkedMatchSession c0, NetworkedMatchSession c1)
        {
            peer.BindClients(c0, c1);
            Task t0 = c0.ConnectAsync(LoopbackUri);
            Task t1 = c1.ConnectAsync(LoopbackUri);
            peer.Start();
            await Task.WhenAll(t0, t1).ConfigureAwait(false);
        }

        /// <summary>Both seats keep their opening hands — the fastest clean way out of mulligan.</summary>
        protected static async Task ConfirmMulligansAsync(LoopbackServerPeer peer, NetworkedMatchSession c0, NetworkedMatchSession c1)
        {
            Task<CommandResult> a = c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
            Task<CommandResult> b = c1.SubmitAsync(new MulliganIntent(c1.Player, Array.Empty<InstanceId>()));
            await peer.RelayAsync().ConfigureAwait(false);
            await Task.WhenAll(a, b).ConfigureAwait(false);
        }
    }
}
