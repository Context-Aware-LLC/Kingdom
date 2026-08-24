using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// Encode/decode identity for every message type (dev-docs/contracts/wire-protocol.md:41) —
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
        public void Intent_With_Named_PerEffect_Targets_RoundTrips()
        {
            var intent = new PlayCardIntent(new PlayerId("p0"), new InstanceId(5), 0,
                new[]
                {
                    new TargetRef(new InstanceId(9), "first"),
                    new TargetRef(new InstanceId(10), "second"),
                });

            var message = RoundTrip<IntentMsg>(new IntentMsg(7, intent));
            var roundTripped = (PlayCardIntent)message.Intent;

            Assert.That(roundTripped.Targets, Is.EqualTo(intent.Targets));
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
        public void SubmitDeck_RoundTrips_With_Classes_And_Cards()
        {
            // EF-071 — the deck-submission message. Classes travel as strict enum names; card lines as
            // {cardId, count}. Legality is not the codec's business (server-side), only the shape.
            var cards = new List<DeckCardLine>
            {
                new DeckCardLine(FixtureCard.Id("ember_recruit"), 2),
                new DeckCardLine(FixtureCard.Id("neutral_stoneback_sentry"), 1),
            };
            var m = RoundTrip<SubmitDeckMsg>(
                new SubmitDeckMsg("char-7", CardClass.GRAVEKEEP, CardClass.VERDANT, cards));
            Assert.That(m.CharacterId, Is.EqualTo("char-7"));
            Assert.That(m.PrimaryClass, Is.EqualTo(CardClass.GRAVEKEEP));
            Assert.That(m.SecondaryClass, Is.EqualTo(CardClass.VERDANT));
            Assert.That(m.Cards, Has.Count.EqualTo(2));
            Assert.That((m.Cards[0].CardId, m.Cards[0].Count), Is.EqualTo((FixtureCard.Id("ember_recruit"), 2)));
            Assert.That((m.Cards[1].CardId, m.Cards[1].Count), Is.EqualTo((FixtureCard.Id("neutral_stoneback_sentry"), 1)));
        }

        [Test]
        public void SubmitDeck_With_A_Non_Enum_Class_Is_Refused()
        {
            // Strict enum-name parse (WirePrimitives): a class the server does not define is a BadField,
            // never a silent default. Belt-and-braces with the server-side legality check.
            var bytes = ProtocolCodec.Encode(
                new SubmitDeckMsg("c", CardClass.GRAVEKEEP, CardClass.VERDANT,
                    new List<DeckCardLine> { new DeckCardLine(FixtureCard.Id("x"), 1) }), M, Seq);
            string json = Encoding.UTF8.GetString(bytes).Replace("\"primaryClass\":\"GRAVEKEEP\"", "\"primaryClass\":\"WIZARD\"");
            var res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.BadField));
        }

        [Test]
        public void Snapshot_RoundTrips_With_Hash_And_State()
        {
            var s = CanonicalStateFixtures.BuildSeedState();
            var m = RoundTrip<SnapshotMsg>(SnapshotMsg.Of(s));
            Assert.That(m.StateVersion, Is.EqualTo(s.StateVersion));
            Assert.That(m.VerifyIntegrity(), Is.True);
            Assert.That(m.State.PendingChoice, Is.Not.Null);
            Assert.That(m.State.PendingChoice!.Options, Is.EqualTo(s.PendingChoice!.Options),
                "the RNG offer order must survive snapshot transport exactly");
            Assert.That((m.State.PendingChoice.Chooser, m.State.PendingChoice.Kind,
                    m.State.PendingChoice.Source, m.State.PendingChoice.ChoiceSequence),
                Is.EqualTo((s.PendingChoice.Chooser, s.PendingChoice.Kind,
                    s.PendingChoice.Source, s.PendingChoice.ChoiceSequence)));
            Assert.That(GameStateHasher.Hash(m.State), Is.EqualTo(GameStateHasher.Hash(s)));
        }

        [Test]
        public void Projected_Ongoing_Effect_RoundTrips_Without_Leaking_Its_Rule_Id()
        {
            GameState authority = CanonicalStateFixtures.BuildSeedState();
            GameState projected = GameStateProjector.Project(authority, authority.Players[0].Id);

            byte[] bytes = ProtocolCodec.Encode(SnapshotMsg.Of(projected), projected.Id, Seq);
            string json = Encoding.UTF8.GetString(bytes);
            DecodeResult decoded = ProtocolCodec.Decode(bytes);

            Assert.Multiple(() =>
            {
                Assert.That(json, Does.Not.Contain("turn_end_echo"));
                Assert.That(json, Does.Contain("\"ruleId\":\"\""),
                    "The projected authority key is an empty sentinel, never hidden truth.");
                Assert.That(decoded.Ok, Is.True, $"{decoded.Error} {decoded.Detail}");
            });
            var snapshot = (SnapshotMsg)decoded.Message!;
            Assert.That(snapshot.State.OngoingEffects, Has.Count.EqualTo(1));
            Assert.That(snapshot.State.OngoingEffects[0].SourceDefinitionId,
                Is.EqualTo(FixtureCard.Id("ongoing_seed")));
            Assert.That(snapshot.State.OngoingEffects[0].RemainingUses, Is.EqualTo(2));
            Assert.That(snapshot.VerifyIntegrity(), Is.True);
        }

        [Test]
        public void Snapshot_Strictly_Rejects_Invalid_Or_Missing_Ongoing_Lifetime_State()
        {
            GameState state = CanonicalStateFixtures.BuildSeedState();
            string json = Encoding.UTF8.GetString(
                ProtocolCodec.Encode(SnapshotMsg.Of(state), state.Id, Seq));

            DecodeResult permanentWithUses = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(
                json.Replace("\"lifetime\":\"FINITE\"", "\"lifetime\":\"PERMANENT\"")));
            DecodeResult oneShotWithTwoUses = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(
                json.Replace("\"lifetime\":\"FINITE\"", "\"lifetime\":\"ONE_SHOT\"")));
            DecodeResult missingList = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(
                json.Replace("\"ongoingEffects\"", "\"unknownOngoingEffects\"")));

            Assert.Multiple(() =>
            {
                Assert.That(permanentWithUses.Ok, Is.False);
                Assert.That(permanentWithUses.Error, Is.EqualTo(ProtocolError.BadField));
                Assert.That(oneShotWithTwoUses.Ok, Is.False);
                Assert.That(oneShotWithTwoUses.Error, Is.EqualTo(ProtocolError.BadField));
                Assert.That(missingList.Ok, Is.False);
                Assert.That(missingList.Error, Is.EqualTo(ProtocolError.BadField));
            });
        }

        [Test]
        public void Events_RoundTrip_Including_Null_And_Flag_Shapes()
        {
            var events = new List<GameEvent>
            {
                new GameEvent(GameEventKind.DamageApplied, 7, new InstanceId(3), new InstanceId(4), 5, EventFlags.Lethal, PresentationCue.Frost),
                new GameEvent(GameEventKind.CardCreatedInHand, 8, null, new InstanceId(6), null, EventFlags.IsToken),
                new GameEvent(GameEventKind.TurnEnded, 9, null, null, null, EventFlags.None),
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
                Assert.That(m.Events[i].CueOverride, Is.EqualTo(events[i].CueOverride));
            }
        }

        [Test]
        public void Unknown_Event_Kind_Rejects_The_Whole_Events_Batch()
        {
            var events = new[]
            {
                new GameEvent(GameEventKind.CardPlayed, 1, null, null, null, EventFlags.None),
                new GameEvent(GameEventKind.TurnEnded, 2, null, null, null, EventFlags.None),
            };
            string json = Encoding.UTF8.GetString(ProtocolCodec.Encode(new EventsMsg(7, events), M, Seq))
                .Replace("\"kind\":\"TurnEnded\"", "\"kind\":\"FutureSemanticFact\"");

            var result = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));

            Assert.Multiple(() =>
            {
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Message, Is.Null, "No prefix of the batch may escape.");
                Assert.That(result.Error, Is.EqualTo(ProtocolError.UnknownEventKind));
            });
        }

        [Test]
        public void Unknown_Presentation_Cue_Falls_Back_Without_Rejecting_The_Event()
        {
            var e = new GameEvent(
                GameEventKind.DamageApplied, 1, null, null, 2, EventFlags.None, PresentationCue.Frost);
            string json = Encoding.UTF8.GetString(ProtocolCodec.Encode(new EventsMsg(7, new[] { e }), M, Seq))
                .Replace("\"cue\":\"Frost\"", "\"cue\":\"FutureSpark\"");

            var result = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));

            Assert.That(result.Ok, Is.True, $"{result.Error} {result.Detail}");
            var decoded = (EventsMsg)result.Message!;
            Assert.That(decoded.Events[0].CueOverride, Is.Null);
            Assert.That(PresentationCueDefaults.For(decoded.Events[0].Kind), Is.EqualTo(PresentationCue.Damage));
        }

        [Test]
        public void NonString_Presentation_Cue_Is_A_Bad_Field()
        {
            var e = new GameEvent(
                GameEventKind.DamageApplied, 1, null, null, 2, EventFlags.None, PresentationCue.Frost);
            string json = Encoding.UTF8.GetString(ProtocolCodec.Encode(new EventsMsg(7, new[] { e }), M, Seq))
                .Replace("\"cue\":\"Frost\"", "\"cue\":7");

            var result = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Is.EqualTo(ProtocolError.BadField));
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
            var msg = new MatchStartMsg(new PlayerId("p0"), 0, ProtocolContract.Version,
                new OpponentInfo(new PlayerId("p1"), "Rival"), SnapshotMsg.Of(s),
                ReconnectToken: "tok-3f9a");
            var m = RoundTrip<MatchStartMsg>(msg);
            Assert.That(m.Seat, Is.EqualTo(0));
            Assert.That(m.WireContractVersion, Is.EqualTo(ProtocolContract.Version));
            Assert.That(m.Opponent.DisplayName, Is.EqualTo("Rival"));
            Assert.That(m.Snapshot.VerifyIntegrity(), Is.True);
            Assert.That(m.ReconnectToken, Is.EqualTo("tok-3f9a"),
                "B1 additive field (dev-docs/contracts/wire-protocol.md:41) must survive the round trip.");
        }

        [Test]
        public void MatchStart_Without_ReconnectToken_Decodes_As_Empty()
        {
            // Forward-compat (A5 §8): a pre-B1 encoder omits the field; decode must not fail.
            var s = CanonicalStateFixtures.BuildSeedState();
            var msg = new MatchStartMsg(new PlayerId("p0"), 0, ProtocolContract.Version,
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
