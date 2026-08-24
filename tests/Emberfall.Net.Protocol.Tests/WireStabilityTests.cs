using System.Globalization;
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
    /// Byte-stability + internal-hash + seed-never-on-the-wire (dev-docs/contracts/wire-protocol.md:14).
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class WireStabilityTests
    {
        [Test]
        public void Encode_Is_Byte_Stable_Across_Repeats_And_RoundTrips()
        {
            var s = CanonicalStateFixtures.BuildSeedState();

            var first = ProtocolCodec.Encode(SnapshotMsg.Of(s), s.Id, 1);
            var second = ProtocolCodec.Encode(SnapshotMsg.Of(s), s.Id, 1);
            Assert.That(second, Is.EqualTo(first), "encoding the same state twice diverged.");

            var decoded = ProtocolCodec.Decode(first);
            Assert.That(decoded.Ok, Is.True);
            var reEncoded = ProtocolCodec.Encode((SnapshotMsg)decoded.Message!, s.Id, 1);
            Assert.That(reEncoded, Is.EqualTo(first),
                "Encode(Decode(Encode(s))) is not byte-identical — an ordering leak (keyword sort?) " +
                "or a lossy read (design §6).");
        }

        [Test]
        public void Snapshot_Hash_Is_Computed_Internally_Over_The_Canonical_Form()
        {
            var s = CanonicalStateFixtures.BuildSeedState();
            var msg = SnapshotMsg.Of(s);
            Assert.That(msg.Hash, Is.EqualTo(GameStateHasher.Hash(s)),
                "SnapshotMsg.Of must hash the canonical serialized form itself (ADR-0003 — never the JSON bytes).");
            Assert.That(msg.StateVersion, Is.EqualTo(s.StateVersion));
        }

        [Test]
        public void Corrupted_Snapshot_Fails_Mirror_Integrity()
        {
            var s = CanonicalStateFixtures.BuildSeedState();
            var bytes = ProtocolCodec.Encode(SnapshotMsg.Of(s), s.Id, 1);
            // Flip a stat inside the state payload without touching the hash field.
            string json = Encoding.UTF8.GetString(bytes).Replace("\"turnNumber\":3", "\"turnNumber\":4");
            var res = ProtocolCodec.Decode(Encoding.UTF8.GetBytes(json));
            Assert.That(res.Ok, Is.True, $"{res.Error} {res.Detail}");
            Assert.That(((SnapshotMsg)res.Message!).VerifyIntegrity(), Is.False,
                "a corrupted mirror must fail the per-message hash check (→ client sends resync).");
        }

        [Test]
        public void Seed_And_Live_Rng_Cursor_Never_Reach_The_Wire()
        {
            // The wire carries PROJECTED states; A3 normalizes Rng to cursor 0. Belt-and-braces for
            // decision 19 / ADR-0004: no message encoded for a viewer may contain the authoritative
            // cursor value or any "seed" key (design §10.7).
            var authoritative = CanonicalStateFixtures.BuildSeedState();
            string liveCursor = authoritative.Rng.RawState.ToString(CultureInfo.InvariantCulture);
            Assert.That(authoritative.Rng.RawState, Is.Not.Zero, "seed state must have a live cursor for this test to bite");

            var viewer = authoritative.Players[0].Id;
            var projected = GameStateProjector.Project(authoritative, viewer);

            var messages = new NetMessage[]
            {
                SnapshotMsg.Of(projected),
                new MatchStartMsg(viewer, 0, ProtocolContract.Version,
                    new OpponentInfo(authoritative.Players[1].Id, "Rival"), SnapshotMsg.Of(projected)),
                new MatchEndMsg(MatchOutcome.Player0Win, MatchEndReason.Win),
            };

            foreach (var msg in messages)
            {
                string json = Encoding.UTF8.GetString(ProtocolCodec.Encode(msg, authoritative.Id, 1));
                Assert.That(json, Does.Not.Contain("\"seed\""), $"{msg.GetType().Name} carries a seed key.");
                Assert.That(json, Does.Not.Contain(liveCursor),
                    $"{msg.GetType().Name} leaked the live RNG cursor — projection was skipped upstream.");
            }

            // And the projected snapshot's rngRawState is the A3-normalized zero.
            string snapJson = Encoding.UTF8.GetString(ProtocolCodec.Encode(SnapshotMsg.Of(projected), authoritative.Id, 1));
            Assert.That(snapJson, Does.Contain("\"rngRawState\":0"));
        }
    }
}
