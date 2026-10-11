using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests
{
    /// <summary>
    /// §11 test 4 — the RC-F8 regression. v0.9's apply dropped the resync remedy (server answers at
    /// the SAME version, <c>MatchSession.cs:193</c>) and applied unverified snapshots regardless of
    /// the verify result. These tests FAIL against that apply by construction: the invariant is
    /// "<c>_current</c> only ever holds a snapshot that passed VerifyIntegrity()".
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class SnapshotApplyTests : ClientTestBase
    {
        /// <summary>Flip one hex digit of a snapshot frame's wire "hash" (leaves events/turn_timer
        /// frames — which carry no "hash" — untouched), so VerifyIntegrity fails.</summary>
        private static byte[] TamperSnapshotHash(byte[] frame)
        {
            string json = Encoding.UTF8.GetString(frame);
            const string marker = "\"hash\":\"";
            int i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return frame; // not a snapshot-bearing frame — pass through unchanged
            int hashStart = i + marker.Length;
            char c = json[hashStart];
            char flipped = c == '0' ? '1' : '0';
            var sb = new StringBuilder(json);
            sb[hashStart] = flipped;
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        [Test]
        public async Task Test4a_Unverified_Snapshot_Is_Never_Installed_Then_Recovers_Via_Resync()
        {
            var captures = new List<DesyncCapture>();
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions
            {
                DeviceToken = "test-device-0",
                DevCapture = true,
                OnDesyncCapture = captures.Add,
            });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);
            await ConfirmMulligansAsync(peer, c0, c1);

            ulong good = c0.StateVersion;
            int snapshots = 0;
            c0.OnSnapshot += _ => snapshots++;

            // Corrupt every snapshot heading to seat 0, then advance the game one command.
            peer.InterceptToClient0(TamperSnapshotHash);
            int active = c0.Current.SeatOf(c0.Current.ActivePlayer);
            NetworkedMatchSession actor = active == 0 ? c0 : c1;
            PlayerId actorId = active == 0 ? peer.P0 : peer.P1;
            Task<CommandResult> submit = actor.SubmitAsync(new EndTurnIntent(actorId));
            await peer.RelayAsync();

            // 4a core: the corrupt snapshot was NEVER installed — the mirror holds the last good state.
            Assert.That(c0.StateVersion, Is.EqualTo(good), "corrupt snapshot must not advance the mirror");
            Assert.That(c0.ResyncPending, Is.True, "a resync was requested");
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live), "recoverable mismatch never kills the session");
            Assert.That(captures, Is.Not.Empty, "4d: the raw frame is captured before recovery");
            Assert.That(captures[0].WireHash, Is.Not.EqualTo(captures[0].LocalHash), "capture records the divergence");

            // 4a recovery: clear the tamper, let the real server answer the resync.
            peer.ClearInterceptToClient0();
            await peer.RelayAsync();

            Assert.That(c0.ResyncPending, Is.False, "the resync-pending snapshot applied");
            Assert.That(c0.StateVersion, Is.GreaterThan(good), "recovered to the fresh server state");
            Assert.That(snapshots, Is.GreaterThan(0), "OnSnapshot fired on recovery, not on the corrupt frame");
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
            if (active == 0) Assert.That((await submit).Accepted, Is.True, "the actor's own submit resolved on recovery");
        }

        [Test]
        public async Task Test4b_Resync_Answered_At_The_Same_Version_Is_Applied_Not_Dropped()
        {
            // The pure RC-F8 livelock: client and server both at V, a resync round-trips at V, and
            // the `<= ` guard would drop the cure. The _resyncPending exemption lets it land.
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0" });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            ulong v = c0.StateVersion;
            int baseline = c0.InboundProcessed;
            int snapshots = 0;
            c0.OnSnapshot += _ => snapshots++;

            // Feed an undecodable frame → client sends resync(DecodeError) while still at V; mirror unchanged.
            peer.Transport0.EnqueueInbound(Encoding.UTF8.GetBytes("{ this is not a wire envelope }"));
            await c0.WaitInboundAsync(baseline + 1);

            Assert.That(c0.StateVersion, Is.EqualTo(v), "a decode error does not touch the mirror");
            Assert.That(c0.ResyncPending, Is.True);

            // The server (also at V) answers the resync with SendSnapshot(V) — same version.
            await peer.RelayAsync();

            Assert.That(c0.ResyncPending, Is.False, "same-version resync answer APPLIED, not dropped (the livelock cure)");
            Assert.That(c0.StateVersion, Is.EqualTo(v));
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live));
            Assert.That(snapshots, Is.EqualTo(1), "exactly one recovery snapshot, on the first resync");
        }

        [Test]
        public async Task Test4c_A_Genuinely_Undecodable_Stream_Dies_At_The_Budget_Without_Throwing()
        {
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0", ResyncBudget = 3 });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);

            ulong good = c0.StateVersion;
            int baseline = c0.InboundProcessed;

            // Four undecodable frames: 3 resyncs, the 4th escalates → Dead(Desync). No throw ever,
            // and the mirror still holds the last VERIFIED state (never a corrupt one).
            for (int i = 1; i <= 4; i++)
            {
                peer.Transport0.EnqueueInbound(Encoding.UTF8.GetBytes("garbage-" + i));
                await c0.WaitInboundAsync(baseline + i);
            }

            Assert.That(c0.ResyncCount, Is.GreaterThan(3));
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Dead));
            Assert.That(c0.DeadReason, Is.EqualTo(DeadReason.Desync));
            Assert.That(c0.StateVersion, Is.EqualTo(good), "even at death the mirror is the last good state, never corrupt");
        }
    }
}
