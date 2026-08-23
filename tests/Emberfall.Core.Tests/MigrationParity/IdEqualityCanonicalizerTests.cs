using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Emberfall.Core.Tests.MigrationParity
{
    /// <summary>
    /// Phase 2a item 1. The comparator must compare id EQUALITY RELATIONS, not id values — grounded's
    /// reproducer is one match containing one FROST, where the new engine mints an extra instance,
    /// every later id shifts by one, and a value comparison reports 100% failure against zero
    /// behavioral divergence.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class IdEqualityCanonicalizerTests
    {
        [Test]
        public void A_Uniform_Renumbering_Canonicalizes_To_The_Same_Stream()
        {
            var a = Ids("5", "6", "5", "7");
            var b = Ids("12", "13", "12", "14");   // same relation, every value shifted

            Assert.That(Values(IdEqualityCanonicalizer.Canonicalize(a)),
                Is.EqualTo(Values(IdEqualityCanonicalizer.Canonicalize(b))));
        }

        [Test]
        public void A_Broken_Equality_Relation_Does_Not_Canonicalize_Away()
        {
            var shared = Ids("5", "6", "5");       // first and third are the SAME entity
            var split = Ids("5", "6", "7");        // ...and here they are not

            Assert.That(Values(IdEqualityCanonicalizer.Canonicalize(shared)),
                Is.Not.EqualTo(Values(IdEqualityCanonicalizer.Canonicalize(split))));
        }

        [Test]
        public void The_Obl7_Remint_Survives_Canonicalization()
        {
            // MoveCardToHand re-mints on entry to a hidden zone as an anti-correlation privacy
            // guarantee. An engine that dropped the re-mint keeps one id where the real one has two.
            // That difference MUST reach the report — it is a privacy regression, not id noise.
            var reminting = Ids("5", "12");        // deck id, then a fresh id in hand
            var notReminting = Ids("5", "5");      // same card, same id — OBL-7 lost

            var d = TraceComparator.CompareTokens(
                IdEqualityCanonicalizer.Canonicalize(reminting),
                IdEqualityCanonicalizer.Canonicalize(notReminting));

            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.Identity));
        }

        [Test]
        public void Canonicalization_Is_By_Observed_Value_Never_By_Position()
        {
            // If the rewrite keyed on position rather than on the value seen there, every stream of
            // the same length would canonicalize alike and the gate would certify anything.
            var a = Ids("5", "5");
            var b = Ids("5", "6");
            Assert.That(Values(IdEqualityCanonicalizer.Canonicalize(a)),
                Is.Not.EqualTo(Values(IdEqualityCanonicalizer.Canonicalize(b))));
        }

        [Test]
        public void The_Redaction_Sentinel_Is_Never_Renumbered()
        {
            // InstanceId(0) is reserved and never minted: it is a constant meaning "hidden from you".
            // Renumbering it would let a real id on one side align with a redaction on the other.
            var tokens = Ids(StateTokenizer.RedactedId, "5", StateTokenizer.RedactedId);
            var canonical = Values(IdEqualityCanonicalizer.Canonicalize(tokens));

            Assert.That(canonical[0], Is.EqualTo(StateTokenizer.RedactedId));
            Assert.That(canonical[2], Is.EqualTo(StateTokenizer.RedactedId));
            Assert.That(canonical[1], Is.Not.EqualTo(StateTokenizer.RedactedId));
        }

        [Test]
        public void Absence_Is_Never_Renumbered_And_Never_Equals_The_Sentinel()
        {
            var tokens = Ids(StateTokenizer.AbsentId, StateTokenizer.RedactedId, "5");
            var canonical = Values(IdEqualityCanonicalizer.Canonicalize(tokens));

            Assert.That(canonical[0], Is.EqualTo(StateTokenizer.AbsentId));
            Assert.That(canonical[1], Is.EqualTo(StateTokenizer.RedactedId));
            Assert.That(canonical[0], Is.Not.EqualTo(canonical[1]));
        }

        [Test]
        public void Scalars_Are_Left_Alone()
        {
            // A health of 5 and an id of 5 are different facts. Renumbering the health would hide a
            // real stat divergence behind id canonicalization.
            var tokens = new List<TraceToken>
            {
                new TraceToken("a.Id", TokenKind.Id, "5"),
                new TraceToken("a.CurrentHealth", TokenKind.Scalar, "5"),
            };
            var canonical = IdEqualityCanonicalizer.Canonicalize(tokens);

            Assert.That(canonical[1].Value, Is.EqualTo("5"));
            Assert.That(canonical[0].Value, Is.Not.EqualTo("5"));
        }

        [Test]
        public void An_Extra_Minted_Instance_Still_Diverges_Through_The_Counter()
        {
            // The FROST reproducer's residue: canonicalization absorbs the id SHIFT, but the engine
            // that minted an extra instance has a different NextInstanceCounter, which is a scalar.
            // That is the correct outcome — the shift is noise, the extra mint is a real difference.
            var a = new List<TraceToken>
            {
                new TraceToken("s.NextInstanceCounter", TokenKind.Scalar, "10"),
                new TraceToken("s.Players[0].Hero.Id", TokenKind.Id, "5"),
            };
            var b = new List<TraceToken>
            {
                new TraceToken("s.NextInstanceCounter", TokenKind.Scalar, "11"),
                new TraceToken("s.Players[0].Hero.Id", TokenKind.Id, "6"),
            };

            var d = TraceComparator.CompareTokens(
                IdEqualityCanonicalizer.Canonicalize(a),
                IdEqualityCanonicalizer.Canonicalize(b));

            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.Semantic));
            Assert.That(d.Path, Is.EqualTo("s.NextInstanceCounter"));
        }

        private static List<TraceToken> Ids(params string[] values)
            => values.Select((v, i) => new TraceToken($"t[{i}].Id", TokenKind.Id, v)).ToList();

        private static List<string> Values(IEnumerable<TraceToken> tokens)
            => tokens.Select(t => t.Value).ToList();
    }
}
