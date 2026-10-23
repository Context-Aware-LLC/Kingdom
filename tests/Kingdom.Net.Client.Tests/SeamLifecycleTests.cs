using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Loopback;
using Kingdom.Net.Client.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests
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

            Task<CommandResult> submitted = c1.SubmitAsync(new ConcedeIntent(c1.Player));
            await peer.RelayAsync();

            Assert.That((await submitted).Accepted, Is.True,
                "the terminal snapshot must resolve the accepted intent before the buffered peer close");
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Ended));
            Assert.That(c1.State, Is.EqualTo(ConnectionState.Ended));
            Assert.That(c0.Current.Outcome, Is.Not.EqualTo(MatchOutcome.InProgress));
        }

        [Test]
        public async Task Server_Close_Drains_Buffered_Frames_Before_Signaling_Peer_Close()
        {
            var transport = new LoopbackTransport();
            byte[] buffered = { 1, 2, 3 };
            WireCloseReason? closed = null;
            transport.OnClosed += (reason, _) => closed = reason;

            transport.EnqueueInbound(buffered);
            transport.ServerClose();

            Assert.That(closed, Is.Null,
                "a close signal must not overtake server frames already accepted by the transport");
            Assert.That(await transport.ReceiveAsync(default), Is.EqualTo(buffered));
            Assert.That(await transport.ReceiveAsync(default), Is.Null);
            Assert.That(closed, Is.EqualTo(WireCloseReason.PeerClosed));
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

            // A submit in flight, then the socket vanishes: the runner would spin forever on a stale
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

        /// <summary>Regression for the BotSeatRunner round-2 fix: a transport close that lands
        /// while ConnectAsync is still awaiting the match_start frame — e.g. BotSeatRunner.Close()
        /// racing Bind()/ConnectAsync before the server ever enqueues match_start — must fault
        /// ConnectAsync's task instead of leaving it (and every caller awaiting it) blocked
        /// forever. No peer drains this transport at all, so match_start can never arrive; only
        /// the fix in NetworkedMatchSession.OnPeerClosed can unblock this.</summary>
        [Test]
        [CancelAfter(5000)]
        public async Task Peer_Close_Before_Match_Start_Faults_ConnectAsync_Instead_Of_Hanging()
        {
            var transport = new LoopbackTransport();
            using var c0 = new NetworkedMatchSession(transport, new ClientOptions { DeviceToken = "test-device-close-race" });

            Task connect = c0.ConnectAsync(LoopbackUri);
            transport.ServerClose();

            // A bounded wait, not the bare 'await' — a regression here must fail fast with a clear
            // "did not complete" message rather than riding the whole [CancelAfter] budget.
            Task winner = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.That(winner, Is.SameAs(connect), "ConnectAsync hung instead of faulting on a pre-match_start peer close.");

            Exception? caught = null;
            try { await connect; } catch (Exception ex) { caught = ex; }
            Assert.That(caught, Is.Not.Null, "ConnectAsync should fault, not complete silently, when the peer closes before match_start.");
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Dead));
            Assert.That(c0.DeadReason, Is.EqualTo(DeadReason.Forfeited));
        }

        /// <summary>Item 4b: the server's capacity refusal is the one close the boot answers by
        /// re-queueing, so it must arrive as its own bucket and not as a forfeit.</summary>
        [Test]
        [CancelAfter(5000)]
        public async Task A_Capacity_Refusal_Reads_As_ServerFull()
        {
            var transport = new LoopbackTransport();
            using var c0 = new NetworkedMatchSession(transport, new ClientOptions { DeviceToken = "test-device-full" });

            Task connect = c0.ConnectAsync(LoopbackUri);
            transport.ServerClose(WireCloseReason.PeerClosed, NetworkedMatchSession.ServerFullReason);
            try { await connect; } catch (InvalidOperationException) { /* expected: no match */ }

            Assert.That(c0.State, Is.EqualTo(ConnectionState.Dead));
            Assert.That(c0.DeadReason, Is.EqualTo(DeadReason.ServerFull));
            Assert.That(c0.DeadReasonDetail, Is.EqualTo(NetworkedMatchSession.ServerFullReason));
        }

        /// <summary>A queued player who leaves must leave the server's queue: Dispose closes the
        /// socket rather than only ceasing to read it.</summary>
        [Test]
        public void Dispose_Closes_The_Transport()
        {
            var transport = new LoopbackTransport();
            WireCloseReason? closed = null;
            transport.OnClosed += (reason, _) => closed = reason;
            var c0 = new NetworkedMatchSession(transport, new ClientOptions { DeviceToken = "test-device-leave" });

            c0.Dispose();

            Assert.That(closed, Is.EqualTo(WireCloseReason.Local));
            Assert.That(c0.State, Is.Not.EqualTo(ConnectionState.Dead),
                "a local close is not reported back as the peer's");
        }
    }
}
