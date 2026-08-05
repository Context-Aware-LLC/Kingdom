using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Client.Tests
{
    /// <summary>§11 test 1 (seam conformance, lifecycle half) + the RC-F10 Dead SIGNAL — the
    /// client half of "Dead stops the runner" (the MatchRunner.StopMatch wiring itself is
    /// play-mode, §11.1).</summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class SeamLifecycleTests : ClientTestBase
    {
        [Test]
        public async Task Connects_Pairs_And_Applies_A_Verified_Initial_Snapshot()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0" });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
            Assert.That(new[] { c0.Seat, c1.Seat }, Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(c0.Current.Phase, Is.EqualTo(TurnPhase.Mulligan));
            // The mirror is a real projected GameState the presentation layer already renders.
            Assert.That(c0.Current.StateVersion, Is.EqualTo(c0.StateVersion));
        }

        [Test]
        public async Task OnSnapshot_Fires_Before_OnEvents_And_Version_Is_Monotonic()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0" });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            var order = new List<string>();
            ulong lastVersion = c0.StateVersion;
            c0.OnSnapshot += s =>
            {
                Assert.That(s.StateVersion, Is.GreaterThanOrEqualTo(lastVersion), "version never goes backwards");
                lastVersion = s.StateVersion;
                order.Add("snap");
            };
            c0.OnEvents += _ => order.Add("events");

            await ConfirmMulligansAsync(peer, c0, c1);

            Assert.That(c0.Current.Phase, Is.Not.EqualTo(TurnPhase.Mulligan), "mulligan complete");
            // Every 'events' is preceded by a 'snap' of the same version (§5.2 ordering).
            for (int i = 0; i < order.Count; i++)
                if (order[i] == "events")
                    Assert.That(order.GetRange(0, i), Does.Contain("snap"), "OnSnapshot precedes OnEvents");
        }

        [Test]
        public async Task Concede_Drives_A_Decided_MatchEnd_On_Both_Clients()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0" });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);
            await ConfirmMulligansAsync(peer, c0, c1);

            _ = c1.SubmitAsync(new ConcedeIntent(c1.Player));
            await peer.RelayAsync();

            Assert.That(c0.State, Is.EqualTo(ConnectionState.Ended));
            Assert.That(c1.State, Is.EqualTo(ConnectionState.Ended));
            Assert.That(c0.Current.Outcome, Is.Not.EqualTo(MatchOutcome.InProgress));
        }

        [Test]
        public async Task Peer_Socket_Drop_Signals_Dead_And_Frees_A_Pending_Submit_RC_F10()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0" });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            var deadSignal = new TaskCompletionSource<ConnectionState>(TaskCreationOptions.RunContinuationsAsynchronously);
            c0.OnConnectionStateChanged += s => { if (s == ConnectionState.Dead) deadSignal.TrySetResult(s); };

            // A submit in flight, then the socket vanishes: the runner would spin forever on a frosted
            // mirror (RC-F10) if nothing pushed a terminal signal in. The client provides it.
            Task<CommandResult> pending = c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
            peer.Transport0.ServerClose();

            ConnectionState reached = await deadSignal.Task;
            Assert.That(reached, Is.EqualTo(ConnectionState.Dead));
            Assert.That(c0.DeadReason, Is.EqualTo(DeadReason.Forfeited));
            // The pending task RESOLVES (never hangs, never faults into runner error-spam).
            CommandResult r = await pending;
            Assert.That(r.Accepted, Is.False);
        }
    }
}
