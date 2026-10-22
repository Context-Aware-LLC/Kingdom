using System;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Server.Tests.Support;
using Kingdom.Tools.NetSoak;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// A seat on the soak peer may think for as long as it likes. The peer relays only when a seat
    /// submits, so while one thinks no frame reaches either client; on the wall clock that silence
    /// ruled the link dead, and the seat's next intent came back a reject. It failed a capture
    /// with 285 such rejects, nothing in its log naming the cause.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SoakLoopbackClockTests
    {
        [Test]
        public async Task A_decision_longer_than_the_silence_limit_is_still_accepted()
        {
            using var peer = new SoakLoopback(SessionHarness.Db, 0xC10CUL, "soak-clock");
            // The client's timer checks once a second, so a 1 ms limit is crossed on its first tick.
            using var c0 = peer.Client(0, new ClientOptions { DeviceToken = "soak-clock-0", InboundSilenceMs = 1 });
            using var c1 = peer.Client(1, new ClientOptions { DeviceToken = "soak-clock-1", InboundSilenceMs = 1 });
            peer.Bind(c0, c1);
            Task connect0 = c0.ConnectAsync(new Uri("loopback://clock"));
            Task connect1 = c1.ConnectAsync(new Uri("loopback://clock"));
            peer.Start();
            await Task.WhenAll(connect0, connect1);

            await Task.Delay(1500);   // the seat thinks past a tick

            Task<CommandResult> submitted = c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
            await peer.RelayAsync();
            CommandResult result = await submitted;
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
            Assert.That(result.Accepted, Is.True, $"rejected: {result.Reason}");
        }
    }
}
