using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Determinism
{
    /// <summary>
    /// The anti-divergence guards for the canonical serializer (A2a — a2-serializer-design.md §2.1/§5).
    /// The hand-written <see cref="GameStateSerializer"/> walk is the ONE canonical field order; these
    /// tests keep the state types and the walk locked together (M2-KICKOFF.md §5 Risk 3):
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
    /// shared with the A5 wire mutation sweep (a5-wire-protocol-design.md §6) so there is exactly
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
                    .GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Select(f => f.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray();

                Assert.That(actual, Is.EqualTo(expected),
                    $"{type.Name}'s public fields diverged from the canonical manifest. If you added/" +
                    "removed/renamed a state field: update GameStateSerializer's walk, the A5 wire walk " +
                    "(SnapshotJsonCodec), AND the manifest in CanonicalStateFixtures together " +
                    "(a2-serializer-design.md §2.1). A GameState field addition is hash-canonical " +
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
                "case proving it reaches the digest (a2-serializer-design.md §2.1).");
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

        // ── 3. pinned known-vector ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Digest of <see cref="CanonicalStateFixtures.BuildSeedState"/>, pinned by hand. Independent
        /// of fixtures and card data: if this moves and you didn't intend an encoding change, the
        /// serializer broke; if you DID intend one, that is a hash-canonical change (§7 review + full
        /// fixture rebaseline) and this constant is re-pinned in the same reviewed commit.
        /// </summary>
        // Re-pinned 2026-07-15 (A2b, §7-reviewed): walk gained the PendingChoice tail + the seed
        // state now exercises it non-null. Old pin: abb617ccf5e1c67c7697f4b52b1658c29e63045abda9e990f2561b1bfca439db
        private const string PinnedSeedDigest = "23b592020596174461142909891e0b33b412929f98e47b3e7ae6d641c3030009";

        [Test]
        public void Known_Vector_Digest_Is_Pinned()
        {
            Assert.That(GameStateHasher.Hash(CanonicalStateFixtures.BuildSeedState()), Is.EqualTo(PinnedSeedDigest),
                "The canonical encoding changed (or the seed-state builder did). If unintentional: the " +
                "serializer broke — fix it. If intentional: this is hash-canonical; convene §7, rebaseline " +
                "fixtures, and re-pin this constant in the same commit.");
        }

        // ── 4. stability ────────────────────────────────────────────────────────────────────────

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
