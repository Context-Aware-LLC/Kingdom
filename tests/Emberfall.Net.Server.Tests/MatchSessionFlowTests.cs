using System;
using System.Text;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>Happy-path session flow (dev-docs/contracts/gameplay-and-session.md:20).</summary>
    [TestFixture]
    [Category("Contract")]
    public class MatchSessionFlowTests
    {
        [Test]
        public void Start_Sends_MatchStart_With_Distinct_Tokens_And_No_Seed()
        {
            var h = new SessionHarness();

            var ms0 = h.Conn0.Last<MatchStartMsg>();
            var ms1 = h.Conn1.Last<MatchStartMsg>();

            Assert.That(ms0.Seat, Is.EqualTo(0));
            Assert.That(ms1.Seat, Is.EqualTo(1));
            Assert.That(ms0.ReconnectToken, Is.Not.Empty);
            Assert.That(ms1.ReconnectToken, Is.Not.Empty);
            Assert.That(ms0.ReconnectToken, Is.Not.EqualTo(ms1.ReconnectToken),
                "tokens are per-seat credentials (design §5)");
            Assert.That(ms0.Snapshot.VerifyIntegrity(), Is.True, "projected snapshot must self-verify");
            Assert.That(ms0.Opponent.PlayerId.Value, Is.EqualTo("p1"));

            foreach (var frame in h.Conn0.Frames)
                Assert.That(Encoding.UTF8.GetString(frame), Does.Not.Contain("\"seed\""),
                    "the seed never reaches the wire (ADR-0004)");
        }

        [Test]
        public void Scripted_Match_To_Concession_Replays_To_The_Same_Authority_Hash()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();

            // A few real turns through the wire path, then a concession.
            for (int i = 0; i < 4; i++)
            {
                var active = h.Session.Host.State.ActivePlayer;
                int seat = h.Session.Host.State.SeatOf(active);
                h.Post(seat, h.Client(seat).Intent(new EndTurnIntent(active), h.Id));
            }
            h.Post(1, h.Client1.Intent(new ConcedeIntent(h.P1), h.Id));

            Assert.That(h.Session.Ended, Is.True);
            var end0 = h.Conn0.Last<MatchEndMsg>();
            Assert.That(end0.Outcome, Is.EqualTo(MatchOutcome.Player0Win));
            Assert.That(end0.Reason, Is.EqualTo(MatchEndReason.Concede));
            Assert.That(h.Conn1.Last<MatchEndMsg>().Outcome, Is.EqualTo(MatchOutcome.Player0Win));

            // Replay determinism (§14.1): the recorded (seed, intents) reproduce the authority
            // state bit-for-bit through a FRESH core — the log order IS the execution order.
            string authorityHash = GameStateHasher.Hash(h.Session.Host.State);
            var rec = h.Replay.Matches[h.Id.Value];
            Assert.That(rec.Outcome, Is.EqualTo(MatchOutcome.Player0Win));

            var processor = new CommandProcessor(SessionHarness.Db);
            var fresh = new LocalMatchHost(processor, SessionHarness.Db, PrebuiltDecks.EmberwardVsTidecaller());
            fresh.BeginMatch(h.Id, rec.P0, rec.P1, rec.Seed);
            foreach (var intent in rec.Intents)
            {
                var r = fresh.Submit(intent);
                Assert.That(r.Accepted, Is.True, $"replayed intent {intent.GetType().Name} must re-apply");
            }
            Assert.That(GameStateHasher.Hash(fresh.State), Is.EqualTo(authorityHash),
                "replaying the session log must reproduce the authority hash");
        }

        [Test]
        public void Every_Applied_Command_Ships_Snapshot_Then_Events_Per_Viewer()
        {
            var h = new SessionHarness();
            int before0 = h.Conn0.Frames.Count;
            int before1 = h.Conn1.Frames.Count;

            h.ConfirmMulligans();

            // Both seats saw fresh projected snapshots for each applied mulligan (netcode §3.3).
            Assert.That(h.Conn0.Frames.Count, Is.GreaterThan(before0));
            Assert.That(h.Conn1.Frames.Count, Is.GreaterThan(before1));
            foreach (var snap in h.Conn0.MessagesOf<SnapshotMsg>())
                Assert.That(snap.VerifyIntegrity(), Is.True);
        }

        [Test]
        public void Heartbeat_Is_Echoed()
        {
            var h = new SessionHarness();
            h.Post(0, h.Client0.Frame(new HeartbeatMsg(), h.Id));
            Assert.That(h.Conn0.Last<HeartbeatMsg>(), Is.Not.Null);
        }

        [Test]
        public void Client_Resync_Request_Gets_A_Fresh_Snapshot()
        {
            var h = new SessionHarness();
            int snapsBefore = CountSnaps(h);
            h.Post(0, h.Client0.Frame(new ResyncMsg(0, ResyncReason.HashMismatch), h.Id));
            Assert.That(CountSnaps(h), Is.EqualTo(snapsBefore + 1));
        }

        private static int CountSnaps(SessionHarness h)
        {
            int n = 0;
            foreach (var _ in h.Conn0.MessagesOf<SnapshotMsg>()) n++;
            return n;
        }
    }
}
