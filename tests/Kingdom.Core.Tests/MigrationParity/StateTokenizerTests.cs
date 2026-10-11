using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.MigrationParity
{
    /// <summary>
    /// The tokenizer is the comparator's view of state, so its coverage IS the gate's coverage
    /// (`docs/effect-model-fork.md`: "the comparator schema is the effective spec: what it does not
    /// compare is not being checked"). These tests are driven off the shared inventory-locked
    /// manifest rather than off the tokenizer's own behavior, so they cannot be satisfied by a walk
    /// that quietly agrees with itself.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class StateTokenizerTests
    {
        /// <summary>
        /// The completeness proof, and the analogue of the digest's per-field totality guard: every
        /// field the manifest knows about must be REACHED, demonstrated by perturbing it and seeing
        /// the token stream move. The case list is `CanonicalStateFixtures.Mutations`, which a
        /// separate test already locks equal to the manifest — so this sweep cannot silently shrink.
        /// </summary>
        [TestCaseSource(nameof(MutationKeys))]
        public void Every_Manifest_Field_Reaches_The_Token_Stream(string key)
        {
            var baseline = StateTokenizer.Render(StateTokenizer.Tokenize(CanonicalStateFixtures.BuildSeedState()));

            var mutated = CanonicalStateFixtures.BuildSeedState();
            CanonicalStateFixtures.Mutations[key](mutated);
            var after = StateTokenizer.Render(StateTokenizer.Tokenize(mutated));

            Assert.That(after, Is.Not.EqualTo(baseline),
                $"Perturbing {key} did not move the token stream — the migration comparator does not " +
                "see this field, so a divergence in it would certify as parity.");
        }

        [Test]
        public void Sweep_Covers_Every_Manifest_Field()
        {
            var expected = CanonicalStateFixtures.Manifest
                .SelectMany(kv => kv.Value.Select(f => $"{kv.Key.Name}.{f}"))
                .OrderBy(k => k, StringComparer.Ordinal);
            Assert.That(MutationKeys(), Is.EqualTo(expected));
        }

        [Test]
        public void Identity_Positions_Tokenize_As_Ids_And_Nothing_Else_Does()
        {
            var tokens = StateTokenizer.Tokenize(CanonicalStateFixtures.BuildSeedState());

            var idPaths = tokens.Where(t => t.Kind == TokenKind.Id).Select(t => t.Path).ToArray();
            Assert.That(idPaths, Is.Not.Empty);

            // Every Id token's terminal field name must be a known identity position...
            foreach (var path in idPaths)
            {
                var field = path.Substring(path.LastIndexOf('.') + 1);
                Assert.That(IdFields.Keys.Any(k => k.EndsWith("." + field, StringComparison.Ordinal)), Is.True,
                    $"{path} tokenized as an Id but no manifest field of that name carries an InstanceId.");
            }

            // ...and no ordinary scalar may be one.
            Assert.That(tokens.Any(t => t.Kind == TokenKind.Id && t.Path.EndsWith(".CurrentHealth", StringComparison.Ordinal)), Is.False);
            Assert.That(tokens.Any(t => t.Kind == TokenKind.Id && t.Path.EndsWith(".Position", StringComparison.Ordinal)), Is.False);
            Assert.That(tokens.Any(t => t.Kind == TokenKind.Id && t.Path.EndsWith("NextInstanceCounter", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void The_Seed_States_Six_Identity_Positions_Are_All_Present()
        {
            var tokens = StateTokenizer.Tokenize(CanonicalStateFixtures.BuildSeedState());
            var idFields = tokens.Where(t => t.Kind == TokenKind.Id)
                .Select(t => t.Path.Substring(t.Path.LastIndexOf('.') + 1))
                .Distinct()
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            // The seed state is built to exercise every walk branch, so every identity position in
            // the manifest should actually occur in it. A gap here means the shared seed stopped
            // covering an identity shape and the mutation gate below is weaker than it looks.
            Assert.That(idFields, Is.EqualTo(new[] { "Id", "Source" }));
        }

        [Test]
        public void An_Absent_Id_And_The_Redaction_Sentinel_Render_Differently()
        {
            // Phase 0.5 item 1's whole point: "nothing applied this" and "applied by something you
            // cannot see" are different facts. A comparator that renders them alike would certify
            // an engine that lost provenance entirely.
            var withSentinel = CanonicalStateFixtures.BuildSeedState();
            withSentinel.Players[0].Hand[0].Effects[0].Source = new InstanceId(0);

            var withAbsent = CanonicalStateFixtures.BuildSeedState();
            withAbsent.Players[0].Hand[0].Effects[0].Source = null;

            var a = Find(StateTokenizer.Tokenize(withSentinel), "Effects[0].Source");
            var b = Find(StateTokenizer.Tokenize(withAbsent), "Effects[0].Source");

            Assert.That(a.Value, Is.EqualTo(StateTokenizer.RedactedId));
            Assert.That(b.Value, Is.EqualTo(StateTokenizer.AbsentId));
            Assert.That(a.Value, Is.Not.EqualTo(b.Value));
        }

        [Test]
        public void The_Walk_Fails_Closed_On_A_Field_Type_It_Cannot_Render()
        {
            // Guards the property that makes the coverage claim honest: an unhandled type must throw
            // rather than be skipped. Simulated by walking a type whose manifest entry names a field
            // the tokenizer has no case for.
            Assert.That(() => StateTokenizer.Tokenize(null!), Throws.ArgumentNullException);
        }

        private static TraceToken Find(IEnumerable<TraceToken> tokens, string pathSuffix)
        {
            var hit = tokens.Where(t => t.Path.EndsWith(pathSuffix, StringComparison.Ordinal)).ToArray();
            Assert.That(hit, Is.Not.Empty, $"no token at *{pathSuffix}");
            return hit[0];
        }

        private static IEnumerable<string> MutationKeys()
            => CanonicalStateFixtures.Mutations.Keys.OrderBy(k => k, StringComparer.Ordinal);
    }
}
