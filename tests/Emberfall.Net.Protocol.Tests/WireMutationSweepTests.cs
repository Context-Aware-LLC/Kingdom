using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// The per-field mutation sweep over the WIRE round-trip (dev-docs/contracts/wire-protocol.md:41):
    /// for each canonical field, perturb it on the shared seed state and assert the mutated value
    /// survives Encode→Decode (hash-equal to the mutated original). A field the JSON walk drops or
    /// misreads goes red HERE even if no fixture happens to exercise it. Uses the ONE shared
    /// mutation list (<see cref="CanonicalStateFixtures"/>) — never a duplicate (§12 finding 2).
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class WireMutationSweepTests
    {
        private static IEnumerable<string> MutationKeys()
            => CanonicalStateFixtures.Mutations.Keys.OrderBy(k => k, StringComparer.Ordinal);

        [TestCaseSource(nameof(MutationKeys))]
        public void Mutated_Field_Survives_The_Wire(string key)
        {
            var mutated = CanonicalStateFixtures.BuildSeedState();
            CanonicalStateFixtures.Mutations[key](mutated);

            var bytes = ProtocolCodec.Encode(SnapshotMsg.Of(mutated), mutated.Id, seq: 1);
            var decoded = ProtocolCodec.Decode(bytes);

            // The one domain split between the two walks: the hash walk writes enums as raw ints
            // (total over all bit patterns); the wire writes MEMBER NAMES and strictly refuses
            // undefined members on decode (hostile-enum defense, design §5). The shared
            // PendingChoice.Kind mutation casts to an UNDEFINED member — no second member exists
            // until Phase 2's §7 tail-append — so the wire's correct behavior is a typed refusal,
            // not a round-trip. (Kind's wire coverage is proven by the other PendingChoice.*
            // mutations — the reader requires 'kind' to parse them at all.)
            if (key == "PendingChoice.Kind")
            {
                Assert.That(decoded.Ok, Is.False,
                    "an undefined enum member silently crossed the wire — strict enum parse regressed.");
                Assert.That(decoded.Error, Is.EqualTo(ProtocolError.BadField));
                return;
            }

            string expectedHash = GameStateHasher.Hash(mutated);
            Assert.That(decoded.Ok, Is.True, $"decode failed after mutating {key}: {decoded.Error} {decoded.Detail}");
            var snap = (SnapshotMsg)decoded.Message!;
            Assert.That(GameStateHasher.Hash(snap.State), Is.EqualTo(expectedHash),
                $"Mutating {key} did not survive the wire round-trip — SnapshotJsonCodec drops or " +
                "misreads this field (the two-walks drift the bridge exists to catch).");
        }

        [Test]
        public void Sweep_Covers_Every_Manifest_Field()
        {
            // Belt-and-braces: the Core.Tests inventory test already locks Mutations == Manifest;
            // this asserts THIS sweep saw the same complete list (guards against a filtered rerun).
            var expected = CanonicalStateFixtures.Manifest
                .SelectMany(kv => kv.Value.Select(f => $"{kv.Key.Name}.{f}"))
                .OrderBy(k => k, StringComparer.Ordinal);
            Assert.That(MutationKeys(), Is.EqualTo(expected));
        }
    }
}
