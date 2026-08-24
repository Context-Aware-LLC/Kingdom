using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Tests.GoldenReplay;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.MigrationParity
{
    /// <summary>
    /// <b>Phase 2a item 2 — the comparator's own gate.</b>
    ///
    /// <para>"A comparator that has never been shown to fail is not evidence"
    /// (`docs/effect-model-fork.md`, finding 4). And the abandonment condition built on it: "The
    /// comparator cannot be made to pass its own mutation test. If a deliberately injected semantic
    /// divergence cannot be reliably detected, the migration has no gate, and a dual-engine period
    /// without a gate is strictly worse than either engine alone."</para>
    ///
    /// <para>Every case below injects a known difference into a trace recorded off the REAL engine
    /// and asserts both that it is detected and that it is classified as the right KIND — because
    /// misattributing an ordering difference as a logic bug is the specific failure finding 4
    /// describes, not a cosmetic one.</para>
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class ComparatorMutationTests
    {
        private const string Fixture = "opening_pyra_maren";

        private static BehavioralTrace Recorded()
            => TraceRecorder.Record(ReplayFixture.Load(
                Path.Combine(RepoPaths.FixturesDir, Fixture + ".replay.json")));

        private static List<TraceToken> Canonical(BehavioralTrace t)
            => IdEqualityCanonicalizer.Canonicalize(TraceFlattener.Flatten(t));

        // ── the control ─────────────────────────────────────────────────────────────────────────

        [Test]
        public void A_Trace_Agrees_With_Itself()
        {
            var d = TraceComparator.Compare(Recorded(), Recorded());
            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.None), d.ToString());
        }

        [Test]
        public void The_Recorded_Trace_Is_Not_Trivially_Small()
        {
            // A gate over an empty stream passes everything. Pin that the trace has real content, so
            // a recorder regression that silently produced nothing cannot read as a green gate.
            var tokens = Canonical(Recorded());
            Assert.That(tokens.Count, Is.GreaterThan(500), "the recorded trace is too small to be meaningful");
            Assert.That(tokens.Count(t => t.Kind == TokenKind.Id), Is.GreaterThan(10), "no identities in the trace");
        }

        // ── no false positives ──────────────────────────────────────────────────────────────────

        [Test]
        public void A_Pure_Renumbering_Is_Not_A_Divergence()
        {
            // Grounded's reproducer, exactly: one match containing one FROST, the candidate engine
            // mints an extra instance id early, and every entity created afterwards is off by one.
            // Behavioral divergence: zero. A value-comparing comparator reports 100% failure.
            var left = Canonical(Recorded());
            var right = IdEqualityCanonicalizer.Canonicalize(Renumber(TraceFlattener.Flatten(Recorded()), by: 1000));

            var d = TraceComparator.CompareTokens(left, right);
            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.None), d.ToString());
        }

        [Test]
        public void Renumbering_Does_Not_Disturb_The_Reserved_Values()
        {
            var renumbered = Renumber(TraceFlattener.Flatten(Recorded()), by: 1000);
            Assert.That(renumbered.Any(t => t.Kind == TokenKind.Id && t.Value == StateTokenizer.AbsentId)
                     || renumbered.Any(t => t.Kind == TokenKind.Id && t.Value == StateTokenizer.RedactedId),
                Is.True, "the fixture exercises neither absence nor redaction — the case is untested here");
            Assert.That(renumbered.Any(t => t.Value == "1000"), Is.False, "a reserved value was shifted into the id space");
        }

        // ── real divergences, correctly classified ──────────────────────────────────────────────

        [Test]
        public void An_Injected_Semantic_Divergence_Is_Detected()
        {
            var left = Canonical(Recorded());
            var right = Canonical(Recorded());

            // Perturb one player-visible scalar deep in the stream — a stat that differs by one.
            int at = right.FindLastIndex(t => t.Kind == TokenKind.Scalar && t.Path.EndsWith(".CurrentHealth", StringComparison.Ordinal));
            Assert.That(at, Is.GreaterThanOrEqualTo(0), "fixture has no health token to perturb");
            right[at] = new TraceToken(right[at].Path, TokenKind.Scalar,
                (int.Parse(right[at].Value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));

            var d = TraceComparator.CompareTokens(left, right);
            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.Semantic), d.ToString());
            Assert.That(d.Path, Does.EndWith(".CurrentHealth"));
        }

        [Test]
        public void A_Broken_Id_Equality_Relation_Is_Detected_As_Identity()
        {
            var left = Canonical(Recorded());

            // Collapse two distinct identities into one: the shape of an engine that failed to
            // re-mint on entry to a hidden zone (OBL-7), which is a privacy regression rather than
            // id noise and must never canonicalize away.
            var raw = TraceFlattener.Flatten(Recorded());
            var distinct = raw.Where(t => t.Kind == TokenKind.Id && IsMinted(t.Value))
                              .Select(t => t.Value).Distinct().Take(2).ToArray();
            Assert.That(distinct.Length, Is.EqualTo(2), "fixture does not contain two distinct identities");

            var collapsed = raw.Select(t =>
                t.Kind == TokenKind.Id && t.Value == distinct[1]
                    ? new TraceToken(t.Path, TokenKind.Id, distinct[0])
                    : t).ToList();

            var d = TraceComparator.CompareTokens(left, IdEqualityCanonicalizer.Canonicalize(collapsed));
            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.Identity), d.ToString());
        }

        [Test]
        public void An_Order_Difference_Reports_As_Order_Not_As_A_Logic_Bug()
        {
            // "Injected decisions expressed as indices require both engines to agree on candidate
            // order, while the comparator is specified to compare candidate SETS — so an order
            // difference is either invisible or misreported as a logic bug." It must be neither.
            var left = Canonical(Recorded());
            var right = Canonical(Recorded());

            int at = FindSwappablePair(right);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), "no adjacent differing pair to transpose");
            (right[at], right[at + 1]) = (right[at + 1], right[at]);

            var d = TraceComparator.CompareTokens(left, right);
            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.Order), d.ToString());
        }

        [Test]
        public void A_Truncated_Stream_Reports_As_Structural()
        {
            var left = Canonical(Recorded());
            var right = Canonical(Recorded());
            right.RemoveRange(right.Count - 5, 5);

            var d = TraceComparator.CompareTokens(left, right);
            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.Structural), d.ToString());
        }

        [Test]
        public void A_Divergence_Report_Names_The_Position_And_Both_Values()
        {
            // A bare "not equal" is useless for fixing a desync — the same reason
            // ReplayDivergenceReport exists for the golden gate.
            var left = Canonical(Recorded());
            var right = Canonical(Recorded());
            int at = right.FindLastIndex(t => t.Kind == TokenKind.Scalar && t.Path.EndsWith(".CurrentHealth", StringComparison.Ordinal));
            right[at] = new TraceToken(right[at].Path, TokenKind.Scalar, "999");

            var text = TraceComparator.CompareTokens(left, right).ToString();
            Assert.That(text, Does.Contain("Semantic"));
            Assert.That(text, Does.Contain("CurrentHealth"));
            Assert.That(text, Does.Contain("999"));
        }

        // ── coverage of the real corpus ─────────────────────────────────────────────────────────

        [TestCaseSource(nameof(RecordableFixtures))]
        public void Every_Fixture_Records_And_Agrees_With_Itself(string name)
        {
            // Proves the tokenizer handles every state shape the real corpus produces, not just the
            // hand-built seed — including the shapes the seed has no branch for.
            var fx = ReplayFixture.Load(Path.Combine(RepoPaths.FixturesDir, name + ".replay.json"));
            var d = TraceComparator.Compare(TraceRecorder.Record(fx), TraceRecorder.Record(fx));
            Assert.That(d.Kind, Is.EqualTo(DivergenceKind.None), d.ToString());
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────

        private static bool IsMinted(string value)
            => value != StateTokenizer.AbsentId && value != StateTokenizer.RedactedId;

        /// <summary>Renumber every MINTED identity by a constant — the shape of an engine whose counter ran ahead.</summary>
        private static List<TraceToken> Renumber(List<TraceToken> tokens, int by)
            => tokens.Select(t => t.Kind == TokenKind.Id && IsMinted(t.Value)
                ? new TraceToken(t.Path, TokenKind.Id,
                    (int.Parse(t.Value, CultureInfo.InvariantCulture) + by).ToString(CultureInfo.InvariantCulture))
                : t).ToList();

        private static int FindSwappablePair(List<TraceToken> tokens)
        {
            for (int i = 0; i + 1 < tokens.Count; i++)
                if (tokens[i].Kind == tokens[i + 1].Kind
                    && !string.Equals(tokens[i].Value, tokens[i + 1].Value, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static IEnumerable<string> RecordableFixtures()
        {
            // unearth_choice is excluded: it carries a pre-existing illegal-intent defect at HEAD
            // (the fixture submits an UnearthChoiceIntent the processor rejects), so it cannot be
            // driven to completion by ANY harness today. Tracked as one of the 8 red tests at HEAD;
            // re-include it the moment that fixture replays.
            var skip = new HashSet<string>(StringComparer.Ordinal) { "unearth_choice" };
            return Directory.EnumerateFiles(RepoPaths.FixturesDir, "*.replay.json")
                .Select(p => Path.GetFileName(p).Replace(".replay.json", "", StringComparison.Ordinal))
                .Where(n => !skip.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal);
        }
    }
}
