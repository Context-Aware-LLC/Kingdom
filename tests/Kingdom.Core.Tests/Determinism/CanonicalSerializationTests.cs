using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Kingdom.Core.Serialization;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Determinism
{
    /// <summary>
    /// The anti-divergence guards for the canonical serializer (A2a — dev-docs/architecture/determinism-and-projection.md:13).
    /// The hand-written <see cref="GameStateSerializer"/> walk is the ONE canonical field order; these
    /// tests keep the state types and the walk locked together (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-KICKOFF.md §5 Risk 3):
    ///  1. <b>Field-inventory lock</b> — reflection over the state types vs. a committed manifest.
    ///     Adding/removing/renaming a state field goes red until the author updates BOTH the manifest
    ///     and the walk. (Reflection lives HERE, in a CI guard — never in the encode path, §4.1.)
    ///  2. <b>Per-field mutation totality</b> — every manifest field, when perturbed, must change the
    ///     digest. The inventory proves the walk-owner knows a field exists; this proves it reaches
    ///     the digest.
    ///  3. <b>Pinned known-vector</b> — one hand-built state, one hardcoded digest constant. Catches an
    ///     accidental encoding change even if every fixture were (wrongly) rebaselined.
    ///  4. <b>Stability</b> — repeat and rebuild invariance.
    ///
    /// The manifest, mutation list, and seed state live in <see cref="CanonicalStateFixtures"/> —
    /// shared with the A5 wire mutation sweep (dev-docs/contracts/wire-protocol.md:41) so there is exactly
    /// ONE list for both the hash walk and the wire walk.
    /// </summary>
    [TestFixture]
    [Category("Firewall")]
    public sealed class CanonicalSerializationTests
    {
        // ── 1. field-inventory lock ─────────────────────────────────────────────────────────────

        [Test]
        public void State_Type_Fields_Match_Committed_Manifest()
        {
            foreach (var (type, expected) in CanonicalStateFixtures.Manifest.Select(kv => (kv.Key, kv.Value)))
            {
                var actual = type
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(f => f.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

                Assert.That(actual, Is.EqualTo(expected),
                    $"{type.Name}'s public fields diverged from the canonical manifest. If you added/" +
                    "removed/renamed a state field: update GameStateSerializer's walk, the A5 wire walk " +
                    "(SnapshotJsonCodec), AND the manifest in CanonicalStateFixtures together " +
                    "(dev-docs/architecture/determinism-and-projection.md:13). A GameState field addition is hash-canonical " +
                    "— it needs the §7 scoped review + fixture rebaseline.");
            }
        }

        // ── 2. per-field mutation totality ──────────────────────────────────────────────────────

        [Test]
        public void Every_Manifest_Field_Has_A_Mutation_Case()
        {
            var expectedKeys = CanonicalStateFixtures.Manifest
                .SelectMany(kv => kv.Value.Select(f => $"{kv.Key.Name}.{f}"))
                .OrderBy(k => k, StringComparer.Ordinal);

            Assert.That(CanonicalStateFixtures.Mutations.Keys.OrderBy(k => k, StringComparer.Ordinal), Is.EqualTo(expectedKeys),
                "Mutation coverage diverged from the manifest — every state field needs a perturbation " +
                "case proving it reaches the digest (dev-docs/architecture/determinism-and-projection.md:13).");
        }

        [TestCaseSource(nameof(MutationKeys))]
        public void Perturbing_Field_Changes_The_Digest(string key)
        {
            string baseline = GameStateHasher.Hash(CanonicalStateFixtures.BuildSeedState());

            var mutated = CanonicalStateFixtures.BuildSeedState();
            CanonicalStateFixtures.Mutations[key](mutated);

            Assert.That(GameStateHasher.Hash(mutated), Is.Not.EqualTo(baseline),
                $"Mutating {key} did NOT change the canonical digest — the field never reaches the walk. " +
                "This is exactly the silent divergence Risk 3 warns about: fix GameStateSerializer.");
        }

        private static IEnumerable<string> MutationKeys()
            => CanonicalStateFixtures.Mutations.Keys.OrderBy(k => k, StringComparer.Ordinal);

        // ── 3. stability ────────────────────────────────────────────────────────────────────────

        [Test]
        public void Hash_Is_Repeatable_And_Rebuild_Invariant()
        {
            var s = CanonicalStateFixtures.BuildSeedState();
            string first = GameStateHasher.Hash(s);
            Assert.That(GameStateHasher.Hash(s), Is.EqualTo(first), "Repeat hash drifted.");
            Assert.That(GameStateHasher.Hash(CanonicalStateFixtures.BuildSeedState()), Is.EqualTo(first),
                "Two identically-built states hash differently — hidden nondeterminism in the walk.");
        }
    }
}
