using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests
{
    /// <summary>
    /// The client half of match-lifecycle §7.2, against the REAL server session. Until 2026-11-04
    /// there was none: the server held the seat and accepted <c>?reconnect=1</c>, and the client
    /// called every dropped socket Dead, which the match-end screen drew as DEFEAT. A three-second
    /// Wi-Fi band switch ended three matches in five minutes that way, while the server played on
    /// without the player and scored the first one as a timeout loss.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class ReconnectTests : ClientTestBase
    {
        private static readonly Func<int, CancellationToken, Task> NoPause = (_, _) => Task.CompletedTask;

        [Test]
        [CancelAfter(10_000)]
        public async Task A_Dropped_Socket_Reconnects_And_The_Same_Match_Carries_On()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = Reconnecting(peer, 0);
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);
            await ConfirmMulligansAsync(peer, c0, c1);

            var seen = Record(c0);
            ulong before = c0.StateVersion;
            peer.DropOriginalWire(0);
            await UntilAsync(c0, ConnectionState.Live, seen, after: ConnectionState.Reconnecting);
            // The state events are raised off the pool, so the last one may still be in flight.
            await UntilAsync(() => { lock (seen) return seen.Contains(ConnectionState.Live); });

            lock (seen)
            {
                Assert.That(seen, Does.Contain(ConnectionState.Reconnecting), "a drop is announced, not hidden");
                Assert.That(seen, Does.Not.Contain(ConnectionState.Dead));
            }
            Assert.That(c0.StateVersion, Is.EqualTo(before), "nothing happened while away, so nothing moved");
            Assert.That(peer.ReconnectUris.Single().Query, Is.EqualTo("?reconnect=1"));

            // The proof the server took the new wire: an intent sent on it is accepted, and both
            // clients see the result.
            NetworkedMatchSession active = c0.Current.ActivePlayer.Equals(c0.Player) ? c0 : c1;
            Task<CommandResult> end = active.SubmitAsync(new EndTurnIntent(active.Player));
            if (active == c1) await peer.RelayAsync();
            Assert.That((await end).Accepted, Is.True);
            await UntilAsync(() => c0.StateVersion == c1.StateVersion && c0.StateVersion > before);
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task What_Happened_While_Away_Arrives_As_One_Snapshot()
        {
            using var peer = new LoopbackServerPeer();
            var gate = new SemaphoreSlim(0);
            using var c0 = Reconnecting(peer, 0, delay: (_, ct) => gate.WaitAsync(ct));
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);
            await ConfirmMulligansAsync(peer, c0, c1);

            var seen = Record(c0);
            peer.NetworkDown = true;
            peer.DropOriginalWire(0);
            await UntilAsync(() => peer.ReconnectUris.Count >= 1);   // the first try has failed

            // The opponent plays on. Seat 0 is away, so only seat 1 is waited on.
            NetworkedMatchSession active = c0.Current.ActivePlayer.Equals(c0.Player) ? c0 : c1;
            if (active == c0)
            {
                // Seat 0 cannot act while away; the server's turn clock acts for it.
                await peer.AdvanceAndTickAsync(new Kingdom.Net.Server.ServerOptions().TurnMs + 1);
            }
            else
            {
                Task<CommandResult> end = c1.SubmitAsync(new EndTurnIntent(c1.Player));
                await peer.RelayAsync();
                Assert.That((await end).Accepted, Is.True);
            }
            ulong away = c1.StateVersion;
            Assert.That(c0.StateVersion, Is.LessThan(away), "seat 0 missed it");

            peer.NetworkDown = false;
            gate.Release();
            await UntilAsync(c0, ConnectionState.Live, seen, after: ConnectionState.Reconnecting);

            Assert.That(c0.StateVersion, Is.EqualTo(away), "the board jumps to the present (§7.2: snapshot-only)");
            Assert.That(c0.Current.ActivePlayer, Is.EqualTo(c1.Current.ActivePlayer));
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task A_Match_That_Ended_While_Away_Is_MatchGone_Not_A_Defeat()
        {
            using var peer = new LoopbackServerPeer();
            var gate = new SemaphoreSlim(0);
            using var c0 = Reconnecting(peer, 0, delay: (_, ct) => gate.WaitAsync(ct));
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);
            await ConfirmMulligansAsync(peer, c0, c1);

            peer.NetworkDown = true;
            peer.DropOriginalWire(0);
            await UntilAsync(() => peer.ReconnectUris.Count >= 1);

            Task<CommandResult> concede = c1.SubmitAsync(new ConcedeIntent(c1.Player));
            await peer.RelayAsync();
            await concede;
            Assert.That(peer.Session.Ended, Is.True);

            peer.NetworkDown = false;
            gate.Release();
            await UntilAsync(c0, ConnectionState.Dead);

            Assert.That(c0.DeadReason, Is.EqualTo(DeadReason.MatchGone));
            Assert.That(c0.DeadReasonDetail, Is.EqualTo(NetworkedMatchSession.ReconnectRefusedReason));
            Assert.That(c0.Current.Outcome, Is.EqualTo(MatchOutcome.InProgress),
                "the client never learned the result, and must not invent one");
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task A_Network_That_Stays_Down_Spends_The_Budget_Then_Is_ConnectionLost()
        {
            using var peer = new LoopbackServerPeer();
            var clock = new FakeClientClock();
            int pauses = 0;
            using var c0 = Reconnecting(peer, 0, clock: clock, delay: (ms, _) =>
            {
                pauses++;
                clock.NowMs += ms;   // the pauses are the only thing that moves time here
                return Task.CompletedTask;
            });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            peer.NetworkDown = true;
            peer.DropOriginalWire(0);
            await UntilAsync(c0, ConnectionState.Dead);

            Assert.That(c0.DeadReason, Is.EqualTo(DeadReason.ConnectionLost));
            Assert.That(peer.ReconnectUris.Count, Is.EqualTo(pauses + 1), "one try after each pause, and one before");
            Assert.That(pauses, Is.GreaterThan(10), "60 s of 3 s pauses is many tries, not one");
        }

        [TestCase("superseded")]
        [TestCase("protocol violations")]
        [TestCase("rate limit")]
        [CancelAfter(10_000)]
        public async Task A_Close_The_Server_Meant_Is_Not_Argued_With(string reason)
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = Reconnecting(peer, 0);
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            peer.Transport0.ServerClose(WireCloseReason.PeerClosed, reason);
            await UntilAsync(c0, ConnectionState.Dead);

            Assert.That(c0.DeadReason, Is.EqualTo(DeadReason.Forfeited));
            Assert.That(c0.DeadReasonDetail, Is.EqualTo(reason));
            Assert.That(peer.ReconnectUris, Is.Empty);
        }

        [Test]
        [CancelAfter(10_000)]
        public async Task A_Submit_In_Flight_When_The_Wire_Drops_Resolves_Rejected()
        {
            using var peer = new LoopbackServerPeer();
            var gate = new SemaphoreSlim(0);
            using var c0 = Reconnecting(peer, 0, delay: (_, ct) => gate.WaitAsync(ct));
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            Task<CommandResult> pending = c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
            peer.NetworkDown = true;
            peer.DropOriginalWire(0);

            Assert.That((await pending).Accepted, Is.False, "never hangs the runner's submit loop");
            await UntilAsync(() => c0.State == ConnectionState.Reconnecting);
            Assert.That((await c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()))).Accepted,
                Is.False, "nothing is sent while away");
            gate.Release(100);
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        private static NetworkedMatchSession Reconnecting(
            LoopbackServerPeer peer, int seat,
            Func<int, CancellationToken, Task>? delay = null, IClientClock? clock = null)
        {
            var options = new ClientOptions
            {
                DeviceToken = "test-device-" + seat,
                ReconnectTransport = peer.ReconnectWireFor(seat),
                ReconnectDelay = delay ?? NoPause,
            };
            if (clock is not null) options = options with { Clock = clock };
            return new NetworkedMatchSession(seat == 0 ? peer.Transport0 : peer.Transport1, options);
        }

        private static List<ConnectionState> Record(NetworkedMatchSession client)
        {
            var seen = new List<ConnectionState>();
            client.OnConnectionStateChanged += s => { lock (seen) seen.Add(s); };
            return seen;
        }

        /// <summary>Until the client is in <paramref name="state"/>, having been through the state
        /// <paramref name="seen"/> must hold first, so a test cannot pass on the Live it started in.
        /// <paramref name="seen"/> is recorded from before the drop: the reconnect can finish before
        /// a subscription made afterwards would see it start.</summary>
        private static Task UntilAsync(NetworkedMatchSession client, ConnectionState state,
                                       List<ConnectionState> seen, ConnectionState after)
            => UntilAsync(() =>
            {
                bool through;
                lock (seen) through = seen.Contains(after);
                return through && client.State == state;
            });

        private static Task UntilAsync(NetworkedMatchSession client, ConnectionState state)
            => UntilAsync(() => client.State == state);

        /// <summary>Polls, because the reconnect runs on its own task and the condition is
        /// state, not a frame count. Fails rather than hangs.</summary>
        private static async Task UntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("condition not reached within 5 s");
                await Task.Delay(5);
            }
        }
    }
}
