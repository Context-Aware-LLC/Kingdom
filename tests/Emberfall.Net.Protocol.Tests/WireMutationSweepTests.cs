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
            // undefined members on decode (hostile-enum defense, design §5).
            //
            // A SINGLE-MEMBER enum therefore has no round-trippable mutation: the only way to
            // perturb its canonical int is to cast past the one member it defines, and the wire's
            // correct answer to that is a typed refusal. Both entries below are that shape --
            // PendingChoiceKind and EffectKind each define exactly one member today. Each one's
            // wire coverage is proven by its SIBLING mutations, which the reader cannot parse at
            // all without first reading a valid 'kind'.
            //
            // This is a list, not a taxonomy: an enum leaves it the moment it gains a second
            // member, and both enums carry a RETIREMENT TRIGGER saying so at their declaration.
            if (key == "PendingChoice.Kind" || key == "EffectInstance.Kind")
            {
                Assert.That(decoded.Ok, Is.False,
                    $"{key}: an undefined enum member silently crossed the wire — strict enum parse regressed.");
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
