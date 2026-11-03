using System.Text;
using Kingdom.Core.Contracts;
using Kingdom.Core.Serialization;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Protocol.Tests
{
    /// <summary>
    /// The King's commission on the wire (#421): live progress as an optional key on each seat's
    /// snapshot, the final score as an optional key on the match end. Neither is game state, so
    /// neither is hashed.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class CommissionFieldTests
    {
        private static readonly MatchId M = new MatchId("m-commission");

        private static T RoundTrip<T>(NetMessage msg) where T : NetMessage
        {
            DecodeResult res = ProtocolCodec.Decode(ProtocolCodec.Encode(msg, M, 1));
            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            return (T)res.Message!;
        }

        private static string Json(NetMessage msg) => Encoding.UTF8.GetString(ProtocolCodec.Encode(msg, M, 1));

        [Test]
        public void Round_trips_with_and_without_the_field()
        {
            GameState s = CanonicalStateFixtures.BuildSeedState();

            var full = new CommissionProgress(CommissionIds.Technically, 12, true, 9);
            Assert.That(RoundTrip<SnapshotMsg>(SnapshotMsg.Of(s, full)).Commission, Is.EqualTo(full));

            var unqualified = new CommissionProgress(CommissionIds.Overkill, null, false, null);
            Assert.That(RoundTrip<SnapshotMsg>(SnapshotMsg.Of(s, unqualified)).Commission, Is.EqualTo(unqualified),
                "a null score and a null best survive as null, not 0");

            Assert.That(RoundTrip<SnapshotMsg>(SnapshotMsg.Of(s)).Commission, Is.Null);

            var start = new MatchStartMsg(new PlayerId("p0"), 0, ProtocolContract.Version,
                new OpponentInfo(new PlayerId("p1"), "Rival"), SnapshotMsg.Of(s, full), "tok");
            Assert.That(RoundTrip<MatchStartMsg>(start).Snapshot.Commission, Is.EqualTo(full),
                "the match start's snapshot carries the first progress");
        }

        [Test]
        public void An_old_body_without_commission_decodes_to_null()
        {
            GameState s = CanonicalStateFixtures.BuildSeedState();
            string json = Json(SnapshotMsg.Of(s));
            Assert.That(json, Does.Not.Contain("\"commission\""), "no commission is the absent key");

            DecodeResult res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.True, $"{res.Error} {res.Detail}");
            Assert.That(((SnapshotMsg)res.Message!).Commission, Is.Null);

            string end = Json(new MatchEndMsg(MatchOutcome.Player0Win, MatchEndReason.Win));
            Assert.That(end, Does.Not.Contain("\"commission\""));
            Assert.That(RoundTrip<MatchEndMsg>(new MatchEndMsg(MatchOutcome.Player0Win, MatchEndReason.Win)).Commission,
                Is.Null);
        }

        [Test]
        public void A_decoder_ignoring_the_key_still_reads_the_snapshot()
        {
            GameState s = CanonicalStateFixtures.BuildSeedState();
            SnapshotMsg with = RoundTrip<SnapshotMsg>(SnapshotMsg.Of(s, new CommissionProgress(CommissionIds.WasteNot, 4, true, null)));
            SnapshotMsg without = RoundTrip<SnapshotMsg>(SnapshotMsg.Of(s));

            Assert.That(with.StateVersion, Is.EqualTo(without.StateVersion));
            Assert.That(with.Hash, Is.EqualTo(without.Hash));
            Assert.That(GameStateHasher.Hash(with.State), Is.EqualTo(GameStateHasher.Hash(without.State)),
                "the state read beside the key is the state read without it");
            Assert.That(with.VerifyIntegrity(), Is.True);
        }

        [Test]
        public void The_snapshot_hash_ignores_the_field()
        {
            GameState s = CanonicalStateFixtures.BuildSeedState();
            SnapshotMsg plain = SnapshotMsg.Of(s);
            SnapshotMsg carried = SnapshotMsg.Of(s, new CommissionProgress(CommissionIds.Technically, 30, true, 12));

            Assert.That(carried.Hash, Is.EqualTo(plain.Hash));
            Assert.That(carried.Hash, Is.EqualTo(GameStateHasher.Hash(s)));
        }

        [Test]
        public void A_bad_commission_is_a_bad_field()
        {
            GameState s = CanonicalStateFixtures.BuildSeedState();
            string json = Json(SnapshotMsg.Of(s, new CommissionProgress(CommissionIds.Technically, 30, true, null)))
                .Replace("\"countsIfYouWin\":true", "\"countsIfYouWin\":\"yes\"");
            DecodeResult res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.BadField));
        }

        [Test]
        public void Match_end_carries_the_final_and_new_best()
        {
            var final = new CommissionFinal(CommissionIds.Technically, 7, true);
            MatchEndMsg end = RoundTrip<MatchEndMsg>(new MatchEndMsg(MatchOutcome.Player0Win, MatchEndReason.Concede,
                new[] { "waste_not" }, final));

            Assert.That(end.Commission, Is.EqualTo(final));
            Assert.That(end.FindingIds, Is.EqualTo(new[] { "waste_not" }), "the findings still ride beside it");

            var zero = new CommissionFinal(CommissionIds.Overkill, 0, false);
            Assert.That(RoundTrip<MatchEndMsg>(new MatchEndMsg(MatchOutcome.Player1Win, MatchEndReason.Win,
                Commission: zero)).Commission, Is.EqualTo(zero));
        }
    }
}
