using System.Collections.Generic;
using System.Text;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Serialization;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Protocol.Tests
{
    /// <summary>
    /// Per-seat card backs on the wire (#421): the back a player asks for rides their deck
    /// submission as optional <c>cardBackId</c>, and the backs the server settled on ride the match
    /// start as optional <c>backIds</c>, seat 0 then seat 1. A back is a cosmetic, not game state, so
    /// it sits beside the snapshot and never inside the hash.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class CardBackFieldTests
    {
        private static readonly MatchId M = new MatchId("m-card-backs");

        private static T RoundTrip<T>(NetMessage msg, MatchId matchId) where T : NetMessage
        {
            DecodeResult res = ProtocolCodec.Decode(ProtocolCodec.Encode(msg, matchId, 1));
            Assert.That(res.Ok, Is.True, $"decode failed: {res.Error} {res.Detail}");
            return (T)res.Message!;
        }

        private static string Json(NetMessage msg, MatchId matchId)
            => Encoding.UTF8.GetString(ProtocolCodec.Encode(msg, matchId, 1));

        private static SubmitDeckMsg Deck(int cardBackId = 0) => new SubmitDeckMsg(
            CardClass.GRAVEKEEP, CardClass.VERDANT, new[] { new DeckCardLine(FixtureCard.Id("ember_recruit"), 2) }, cardBackId);

        private static MatchStartMsg Start(GameState s, IReadOnlyList<int>? backIds = null) => new MatchStartMsg(
            new PlayerId("p0"), 0, ProtocolContract.Version, new OpponentInfo(new PlayerId("p1"), "Rival"),
            SnapshotMsg.Of(s), "tok", backIds);

        [Test]
        public void Round_trips_both_fields()
        {
            Assert.That(RoundTrip<SubmitDeckMsg>(Deck(7), ProtocolCodec.PreMatch).CardBackId, Is.EqualTo(7));

            GameState s = CanonicalStateFixtures.BuildSeedState();
            MatchStartMsg start = RoundTrip<MatchStartMsg>(Start(s, new[] { 7, 3 }), M);
            Assert.That(start.BackIds, Is.EqualTo(new[] { 7, 3 }), "seat 0 first, then seat 1");
            Assert.That(start.BackIdOf(0), Is.EqualTo(7));
            Assert.That(start.BackIdOf(1), Is.EqualTo(3));
            Assert.That(start.ReconnectToken, Is.EqualTo("tok"), "the fields beside it still read");
        }

        [Test]
        public void Old_bodies_decode_to_the_defaults()
        {
            string deck = Json(Deck(), ProtocolCodec.PreMatch);
            Assert.That(deck, Does.Not.Contain("\"cardBackId\""), "an unset back is the absent key");
            Assert.That(RoundTrip<SubmitDeckMsg>(Deck(), ProtocolCodec.PreMatch).CardBackId, Is.Zero,
                "absent is the unset selection, 0, which the server resolves to the default");

            GameState s = CanonicalStateFixtures.BuildSeedState();
            string start = Json(Start(s), M);
            Assert.That(start, Does.Not.Contain("\"backIds\""));
            MatchStartMsg decoded = RoundTrip<MatchStartMsg>(Start(s), M);
            Assert.That(decoded.BackIds, Is.EqualTo(new[] { CardBackDefinitions.DefaultId, CardBackDefinitions.DefaultId }));
            Assert.That(decoded.BackIdOf(0), Is.EqualTo(CardBackDefinitions.DefaultId));
            Assert.That(decoded.BackIdOf(1), Is.EqualTo(CardBackDefinitions.DefaultId));
        }

        [Test]
        public void The_protocol_default_is_the_catalog_default()
        {
            // The protocol cannot reference Kingdom.Core.Collection, so it carries its own copy.
            Assert.That(MatchStartMsg.DefaultBackId, Is.EqualTo(CardBackDefinitions.DefaultId));
        }

        [Test]
        public void The_backs_ride_outside_the_snapshot_hash()
        {
            GameState s = CanonicalStateFixtures.BuildSeedState();
            MatchStartMsg with = RoundTrip<MatchStartMsg>(Start(s, new[] { 7, 3 }), M);
            MatchStartMsg without = RoundTrip<MatchStartMsg>(Start(s), M);

            Assert.That(with.Snapshot.Hash, Is.EqualTo(without.Snapshot.Hash));
            Assert.That(with.Snapshot.Hash, Is.EqualTo(GameStateHasher.Hash(s)));
            Assert.That(with.Snapshot.VerifyIntegrity(), Is.True);
        }

        [TestCase("\"cardBackId\":7", "\"cardBackId\":\"7\"")]
        [TestCase("\"cardBackId\":7", "\"cardBackId\":7.5")]
        public void A_bad_card_back_id_is_a_bad_field(string from, string to)
        {
            string json = Json(Deck(7), ProtocolCodec.PreMatch).Replace(from, to);
            DecodeResult res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.False);
        }

        [TestCase("[7]")]
        [TestCase("[7,3,1]")]
        [TestCase("[7,\"3\"]")]
        [TestCase("7")]
        public void A_bad_back_ids_is_a_bad_field(string replacement)
        {
            GameState s = CanonicalStateFixtures.BuildSeedState();
            string json = Json(Start(s, new[] { 7, 3 }), M).Replace("\"backIds\":[7,3]", "\"backIds\":" + replacement);
            Assert.That(json, Does.Contain("\"backIds\":" + replacement), "the replacement landed");
            DecodeResult res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.False);
            Assert.That(res.Error, Is.EqualTo(ProtocolError.BadField));
        }
    }
}
