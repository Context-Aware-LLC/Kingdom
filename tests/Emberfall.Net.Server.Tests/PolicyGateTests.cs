using System;
using System.Text;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>
    /// The named B1 enforcement set (dev-docs/contracts/wire-protocol.md:37;
    /// dev-docs/contracts/wire-protocol.md:37).
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public class PolicyGateTests
    {
        [Test]
        public void Client_Submitted_AutoConfirm_Is_Refused_By_The_Allowlist()
        {
            // Load-bearing (RC F6): the codec DECODES AutoConfirmIntent fine and CommandProcessor
            // would ACCEPT it — the allowlist is the only gate. Two-gates rule, a5 §5 finding 4.
            var h = new SessionHarness();
            ulong versionBefore = h.Session.StateVersion;

            h.Post(0, h.Client0.Intent(new AutoConfirmIntent(h.P0), h.Id));

            var reject = h.Conn0.Last<RejectMsg>();
            Assert.That(reject.Reason, Is.EqualTo(RejectReason.UnknownIntent));
            Assert.That(h.Session.StateVersion, Is.EqualTo(versionBefore), "state must not move");
        }

        [Test]
        public void Out_Of_Turn_Intent_Is_Rejected_Before_The_Core_With_Resync_For_Stale_Clients()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();

            var idle = h.Session.Host.State.ActivePlayer.Value == "p0" ? 1 : 0;
            var idlePlayer = idle == 0 ? h.P0 : h.P1;
            int snapsBefore = Count<SnapshotMsg>(h.Conn(idle));

            // Stale lastVersion (0) + out-of-turn end-turn → reject AND a server-initiated resync (§6).
            h.Post(idle, h.Client(idle).Intent(new EndTurnIntent(idlePlayer), h.Id, lastVersion: 0));

            Assert.That(h.Conn(idle).Last<RejectMsg>().Reason, Is.EqualTo(RejectReason.NotYourTurn));
            Assert.That(Count<SnapshotMsg>(h.Conn(idle)), Is.EqualTo(snapsBefore + 1),
                "stale rejected client gets a fresh snapshot (two triggers, one remedy)");
        }

        [Test]
        public void Replayed_Seq_Is_A_Strike_And_Three_Strikes_Close_The_Connection()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();

            int rejectsBefore = Count<RejectMsg>(h.Conn0);
            for (int i = 0; i < 3; i++)
                h.Post(0, h.Client0.ReplayedIntent(new EndTurnIntent(h.P0), h.Id));

            Assert.That(Count<RejectMsg>(h.Conn0), Is.EqualTo(rejectsBefore),
                "seq violations are protocol offenses — dropped, never RejectMsg (§6.2)");
            Assert.That(h.Conn0.CloseCode, Is.EqualTo(1008), "3 strikes → close (§6.1)");
        }

        [Test]
        public void Oversized_Frame_Is_Dropped_Without_Decode_And_Strikes()
        {
            var h = new SessionHarness();
            var oversized = new byte[h.Options.MaxFrameBytes + 1];
            Array.Fill(oversized, (byte)'x');

            for (int i = 0; i < 3; i++) h.Post(0, oversized);

            Assert.That(h.Conn0.CloseCode, Is.EqualTo(1008));
            Assert.That(Count<RejectMsg>(h.Conn0), Is.Zero, "never decoded, never answered");
        }

        [Test]
        public void Malformed_Bytes_Strike_And_Close_Without_A_Reject()
        {
            var h = new SessionHarness();
            byte[] junk = Encoding.UTF8.GetBytes("{\"v\":1,\"this is\":\"not the wire subset\"");

            for (int i = 0; i < 3; i++) h.Post(1, junk);

            Assert.That(h.Conn1.CloseCode, Is.EqualTo(1008));
            Assert.That(Count<RejectMsg>(h.Conn1), Is.Zero,
                "decode failures are strike-and-close, not RejectMsg (RC F3)");
        }

        [Test]
        public void Spoofed_PlayerId_Is_A_Protocol_Offense()
        {
            var h = new SessionHarness();
            // Seat 0 submits an intent carrying seat 1's PlayerId: impossible from an honest client.
            for (int i = 0; i < 3; i++)
                h.Post(0, h.Client0.Intent(new ConcedeIntent(h.P1), h.Id));

            Assert.That(h.Conn0.CloseCode, Is.EqualTo(1008));
            Assert.That(h.Session.Ended, Is.False, "the spoofed concede must never reach the core");
        }

        [Test]
        public void Rate_Limit_Trips_Recovers_After_Refill_And_Closes_On_Sustained_Flood()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            h.Time.Advance(10_000);   // refill to cap: the mulligan confirms cost a token each
            int active = h.Session.Host.State.ActivePlayer.Value == "p0" ? 0 : 1;
            var player = active == 0 ? h.P0 : h.P1;

            // Burn the bucket (capacity 10, in-turn cost 1) with legal-shape intents the core
            // rejects harmlessly (bogus attacker instance): each still costs a token.
            var bogus = new AttackIntent(player, new InstanceId(9_999), new TargetRef(new InstanceId(9_998)));
            for (int i = 0; i < h.Options.RateCapacity; i++)
                h.Post(active, h.Client(active).Intent(bogus, h.Id, lastVersion: h.Session.StateVersion));
            Assert.That(h.Conn(active).Last<RejectMsg>().Reason, Is.Not.EqualTo(RejectReason.RateLimited),
                "within capacity the core answers, not the limiter");

            // 11th trips the limiter.
            h.Post(active, h.Client(active).Intent(bogus, h.Id, lastVersion: h.Session.StateVersion));
            Assert.That(h.Conn(active).Last<RejectMsg>().Reason, Is.EqualTo(RejectReason.RateLimited));

            // Refill (2 tokens/s) → recovery: the next intent reaches the core again.
            h.Time.Advance(1_000);
            h.Post(active, h.Client(active).Intent(bogus, h.Id, lastVersion: h.Session.StateVersion));
            Assert.That(h.Conn(active).Last<RejectMsg>().Reason, Is.Not.EqualTo(RejectReason.RateLimited),
                "rate-limit trip must recover after refill (§7.2)");

            // Sustained flood with no refill: empty-bucket rejects escalate to disconnect (§6.5).
            h.Post(active, h.Client(active).Intent(bogus, h.Id, lastVersion: h.Session.StateVersion)); // drains refill
            for (int i = 0; i < h.Options.EmptyRejectsToDisconnect; i++)
                h.Post(active, h.Client(active).Intent(bogus, h.Id, lastVersion: h.Session.StateVersion));
            Assert.That(h.Conn(active).CloseCode, Is.EqualTo(1008));
        }

        private static int Count<T>(FakeConnection c) where T : NetMessage
        {
            int n = 0;
            foreach (var _ in c.MessagesOf<T>()) n++;
            return n;
        }
    }
}
