using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Tests.Support;
using Kingdom.Net.Protocol;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests
{
    /// <summary>
    /// C-G1-2a — the client seam raises <see cref="IMatchSession.OnTurnTimer"/> for a
    /// <see cref="TurnTimerMsg"/> instead of dropping it (was <c>case TurnTimerMsg: break;</c> at
    /// NetworkedMatchSession.cs:253), so the E2 visible clock has something to bind to. The message
    /// carries an ABSOLUTE Unix-epoch deadline and the seam passes it through as
    /// <see cref="TurnTimer.DeadlineMs"/> unchanged — presentation does the countdown, not the seam.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class TurnTimerSeamTests : ClientTestBase
    {
        [Test]
        public async Task TurnTimerMsg_Raises_OnTurnTimer_With_Player_And_Deadline()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0" });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);
            int baseline = c0.InboundProcessed;

            TurnTimer? captured = null;
            int fired = 0;
            c0.OnTurnTimer += t => { captured = t; fired++; };

            const ulong deadline = 1_726_000_000_000UL; // absolute Unix-epoch ms
            byte[] frame = ProtocolCodec.Encode(new TurnTimerMsg(peer.P0, deadline), peer.Id, 123);
            peer.Transport0.EnqueueInbound(frame);
            await c0.WaitInboundAsync(baseline + 1);

            Assert.That(fired, Is.EqualTo(1), "exactly one OnTurnTimer per TurnTimerMsg");
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured.ActivePlayer, Is.EqualTo(peer.P0));
            Assert.That(captured.DeadlineMs, Is.EqualTo((long)deadline), "absolute deadline passed through unchanged");
            Assert.That(c0.ResyncCount, Is.EqualTo(0), "a turn-timer frame is not corruption");
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
        }
    }
}
