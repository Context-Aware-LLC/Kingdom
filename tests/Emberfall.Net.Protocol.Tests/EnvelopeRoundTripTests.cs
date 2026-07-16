using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// Encode/decode identity for every message type (a5-wire-protocol-design.md §10.3) —
    /// property-complete bodies, envelope fields preserved, v≠1 refused.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class EnvelopeRoundTripTests
    {
        private static readonly MatchId M = new MatchId("m-wire-test");
        private const ulong Seq = 42;

        private static T RoundTrip<T>(NetMessage msg) where T : NetMessage
        {
            var bytes = ProtocolCodec.Encode(msg, M, Seq);
            var res = ProtocolCodec.Decode(bytes);
            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            Assert.That(res.MatchId, Is.EqualTo(M), "envelope matchId lost");
            Assert.That(res.Seq, Is.EqualTo(Seq), "envelope seq lost");
            Assert.That(res.Message, Is.InstanceOf<T>());
            return (T)res.Message!;
        }

        [Test]
        public void Intent_RoundTrips_With_LastVersion()
        {
            var intent = new PlayCardIntent(new PlayerId("p0"), new InstanceId(5), 2,
                new List<TargetRef> { new TargetRef(new InstanceId(9)) });
            var m = RoundTrip<IntentMsg>(new IntentMsg(17, intent));
            Assert.That(m.LastVersion, Is.EqualTo(17UL));
            Assert.That(m.Intent, Is.EqualTo(intent).Using<PlayerIntent>((a, b) => IntentEquals(a, b)));
        }

        [Test]
        public void Intent_With_Empty_Targets_RoundTrips()
        {
            var intent = new HeroPowerIntent(new PlayerId("p1"), new List<TargetRef>());
            var m = RoundTrip<IntentMsg>(new IntentMsg(3, intent));
            Assert.That(((HeroPowerIntent)m.Intent).Targets, Is.Empty);
        }

        [Test]
        public void Ack_Heartbeat_Reconnect_Resync_RoundTrip()
        {
            Assert.That(RoundTrip<AckMsg>(new AckMsg(99)).Version, Is.EqualTo(99UL));
            RoundTrip<HeartbeatMsg>(new HeartbeatMsg());

            var rc = RoundTrip<ReconnectMsg>(new ReconnectMsg(new PlayerId("p0"), "tok-abc", 12));
            Assert.That((rc.PlayerId.Value, rc.Token, rc.LastVersion), Is.EqualTo(("p0", "tok-abc", 12UL)));

            var rs = RoundTrip<ResyncMsg>(new ResyncMsg(8, ResyncReason.HashMismatch));
            Assert.That((rs.LastVersion, rs.Reason), Is.EqualTo((8UL, ResyncReason.HashMismatch)));
        }

        [Test]
        public void Snapshot_RoundTrips_With_Hash_And_State()
        {
            var s = CanonicalStateFixtures.BuildSeedState();
            var m = RoundTrip<SnapshotMsg>(SnapshotMsg.Of(s));
            Assert.That(m.StateVersion, Is.EqualTo(s.StateVersion));
            Assert.That(m.VerifyIntegrity(), Is.True);
        }

        [Test]
        public void Events_RoundTrip_Including_Null_And_Flag_Shapes()
        {
            var events = new List<GameEvent>
            {
                new GameEvent(VfxEvent.onDamage, 7, new InstanceId(3), new InstanceId(4), 5, EventFlags.Lethal),
                new GameEvent(VfxEvent.onDraw, 8, null, new InstanceId(6), null, EventFlags.Overdraw | EventFlags.IsToken),
                new GameEvent(VfxEvent.onTurnEnd, 9, null, null, null, EventFlags.None),
            };
            var m = RoundTrip<EventsMsg>(new EventsMsg(21, events));
            Assert.That(m.StateVersion, Is.EqualTo(21UL));
            Assert.That(m.Events, Has.Count.EqualTo(3));
            for (int i = 0; i < events.Count; i++)
            {
                Assert.That(m.Events[i].Sequence, Is.EqualTo(events[i].Sequence));
                Assert.That(m.Events[i].Kind, Is.EqualTo(events[i].Kind));
                Assert.That(m.Events[i].Source, Is.EqualTo(events[i].Source));
                Assert.That(m.Events[i].Target, Is.EqualTo(events[i].Target));
                Assert.That(m.Events[i].Amount, Is.EqualTo(events[i].Amount));
                Assert.That(m.Events[i].Flags, Is.EqualTo(events[i].Flags));
            }
        }

        [Test]
        public void Reject_TurnTimer_MatchEnd_RoundTrip()
        {
            var rj = RoundTrip<RejectMsg>(new RejectMsg(RejectReason.NotYourTurn, 13));
            Assert.That((rj.Reason, rj.IntentSeq), Is.EqualTo((RejectReason.NotYourTurn, 13UL)));

            var tt = RoundTrip<TurnTimerMsg>(new TurnTimerMsg(new PlayerId("p1"), 123456789012345UL));
            Assert.That((tt.ActivePlayer.Value, tt.DeadlineTick), Is.EqualTo(("p1", 123456789012345UL)));

            var me = RoundTrip<MatchEndMsg>(new MatchEndMsg(MatchOutcome.Player1Win, MatchEndReason.Concede));
            Assert.That((me.Outcome, me.Reason), Is.EqualTo((MatchOutcome.Player1Win, MatchEndReason.Concede)));
        }

        [Test]
        public void MatchStart_RoundTrips_And_Never_Carries_A_Seed()
        {
            var s = CanonicalStateFixtures.BuildSeedState();
            var msg = new MatchStartMsg(new PlayerId("p0"), 0, "interfaces.v3",
                new OpponentInfo(new PlayerId("p1"), "Rival"), SnapshotMsg.Of(s),
                ReconnectToken: "tok-3f9a");
            var m = RoundTrip<MatchStartMsg>(msg);
            Assert.That(m.Seat, Is.EqualTo(0));
            Assert.That(m.EngineContractVersion, Is.EqualTo("interfaces.v3"));
            Assert.That(m.Opponent.DisplayName, Is.EqualTo("Rival"));
            Assert.That(m.Snapshot.VerifyIntegrity(), Is.True);
            Assert.That(m.ReconnectToken, Is.EqualTo("tok-3f9a"),
                "B1 additive field (b1-match-server-design.md §6-bis) must survive the round trip.");
        }

        [Test]
        public void MatchStart_Without_ReconnectToken_Decodes_As_Empty()
        {
            // Forward-compat (A5 §8): a pre-B1 encoder omits the field; decode must not fail.
            var s = CanonicalStateFixtures.BuildSeedState();
            var msg = new MatchStartMsg(new PlayerId("p0"), 0, "interfaces.v3",
                new OpponentInfo(new PlayerId("p1"), "Rival"), SnapshotMsg.Of(s),
                ReconnectToken: "tok-strip-me");
            string json = Encoding.UTF8.GetString(ProtocolCodec.Encode(msg, M, 1))
                .Replace(",\"reconnectToken\":\"tok-strip-me\"", "");
            var res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.True, $"{res.Error} {res.Detail}");
            Assert.That(((MatchStartMsg)res.Message!).ReconnectToken, Is.Empty);
        }

        [Test]
        public void Version_Mismatch_Is_Refused_Before_The_Body()
        {
            var bytes = ProtocolCodec.Encode(new AckMsg(1), M, 1);
            string json = Encoding.UTF8.GetString(bytes).Replace("\"v\":1", "\"v\":2");
            var res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.VersionMismatch));
        }

        [Test]
        public void U64_Full_Range_Survives_The_Wire()
        {
            var m = RoundTrip<AckMsg>(new AckMsg(ulong.MaxValue));
            Assert.That(m.Version, Is.EqualTo(ulong.MaxValue));
        }

        private static bool IntentEquals(PlayerIntent a, PlayerIntent b)
        {
            if (a is PlayCardIntent pa && b is PlayCardIntent pb)
            {
                if (!pa.Player.Equals(pb.Player) || !pa.Card.Equals(pb.Card) || pa.BoardSlot != pb.BoardSlot) return false;
                if (pa.Targets.Count != pb.Targets.Count) return false;
                for (int i = 0; i < pa.Targets.Count; i++)
                    if (!pa.Targets[i].Equals(pb.Targets[i])) return false;
                return true;
            }
            return a.Equals(b);
        }
    }
}
