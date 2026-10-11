using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kingdom.Core.Tests.MigrationParity
{
    /// <summary>
    /// The normalized behavioral trace the migration comparator comes down to
    /// (`docs/effect-model-fork.md`, "Migration is a temporary whole-engine manifest"): command
    /// accept/reject with reason; each choice request and its legal option set; player-visible
    /// ordered outcomes; projected state after each command; terminal outcome.
    ///
    /// <para>Deliberately NOT here: internal hashes and raw event rows. The redesign exists to change
    /// both, so comparing them would pin the implementation being replaced.</para>
    /// </summary>
    public sealed class BehavioralTrace
    {
        public string Name = "";
        public List<TraceStep> Steps = new List<TraceStep>();
        public string TerminalOutcome = "";
    }

    /// <summary>
    /// One command and everything observable that followed it, already tokenized. A step is a flat
    /// token list rather than a record of typed fields because the whole trace has to be rewritable
    /// (id canonicalization) and diffable in one pass — a second shape would just have to be
    /// flattened anyway, and the path prefixes (`step[3].accepted`, `step[3].choice.option[1]`)
    /// carry the structure legibly.
    /// </summary>
    public sealed class TraceStep
    {
        /// <summary>Human label for reports; not compared.</summary>
        public string Label = "";
        public List<TraceToken> Tokens = new List<TraceToken>();
    }

    public sealed class ChoiceRequest
    {
        public string Chooser = "";
        public string Kind = "";
        public List<string> Options = new List<string>();
    }

    /// <summary>
    /// Flattens a trace into ONE ordered token stream. Everything the gate checks is a token, so
    /// "what the comparator compares" is an enumerable list rather than a claim — which is the
    /// property the document asks for ("its exclusions are enumerated in the harness itself").
    /// </summary>
    public static class TraceFlattener
    {
        public static List<TraceToken> Flatten(BehavioralTrace trace)
        {
            if (trace is null) throw new ArgumentNullException(nameof(trace));
            var all = new List<TraceToken>();
            for (int i = 0; i < trace.Steps.Count; i++)
            {
                var prefix = "step[" + i.ToString(CultureInfo.InvariantCulture) + "].";
                foreach (var t in trace.Steps[i].Tokens)
                    all.Add(new TraceToken(prefix + t.Path, t.Kind, t.Value));
            }
            all.Add(new TraceToken("terminalOutcome", TokenKind.Scalar, trace.TerminalOutcome));
            return all;
        }
    }

    /// <summary>
    /// Phase 2a item 1. Rewrites every identity to its <b>equality-class ordinal by first
    /// appearance</b>, so a trace compares by which ids are the same as which, never by their values.
    ///
    /// <para>The rewrite reads only the id VALUES in the stream — never any notion of which entity
    /// "really" underlies them. That restriction is the whole safety property: a canonicalizer that
    /// renumbered by underlying entity would map an engine that re-mints on hidden-zone entry and one
    /// that does not onto the same canonical form, silently deleting OBL-7's anti-correlation
    /// guarantee from the gate (`ActionHandlers.cs` MoveCardToHand).</para>
    /// </summary>
    public static class IdEqualityCanonicalizer
    {
        public static List<TraceToken> Canonicalize(IReadOnlyList<TraceToken> tokens)
        {
            if (tokens is null) throw new ArgumentNullException(nameof(tokens));

            var classes = new Dictionary<string, string>(StringComparer.Ordinal);
            var result = new List<TraceToken>(tokens.Count);

            foreach (var t in tokens)
            {
                if (t.Kind != TokenKind.Id)
                {
                    result.Add(t);      // a scalar 5 is not an identity 5
                    continue;
                }

                // Neither reserved value is a minted identity, so neither joins the numbering.
                if (t.Value == StateTokenizer.RedactedId || t.Value == StateTokenizer.AbsentId)
                {
                    result.Add(t);
                    continue;
                }

                if (!classes.TryGetValue(t.Value, out var canonical))
                {
                    canonical = "c" + classes.Count.ToString(CultureInfo.InvariantCulture);
                    classes.Add(t.Value, canonical);
                }
                result.Add(new TraceToken(t.Path, TokenKind.Id, canonical));
            }

            return result;
        }
    }

    public enum DivergenceKind
    {
        None = 0,
        /// <summary>The id equality relation differs — who is the same as whom changed.</summary>
        Identity = 1,
        /// <summary>A non-identity value differs. The bug class the gate exists for.</summary>
        Semantic = 2,
        /// <summary>Same facts, different arrangement — the RNG/candidate-order case.</summary>
        Order = 3,
        /// <summary>The streams do not line up at all: a step or field is present on one side only.</summary>
        Structural = 4,
    }

    public sealed class TraceDivergence
    {
        public DivergenceKind Kind;
        public string Path = "";
        public string? Left;
        public string? Right;
        public int Index = -1;

        public static readonly TraceDivergence None = new TraceDivergence { Kind = DivergenceKind.None };

        public override string ToString() => Kind == DivergenceKind.None
            ? "no divergence"
            : string.Format(CultureInfo.InvariantCulture,
                "{0} divergence at token {1} ({2}): left={3} right={4}", Kind, Index, Path, Left ?? "<end>", Right ?? "<end>");
    }

    public static class TraceComparator
    {
        /// <summary>
        /// Compare two traces. Both sides are flattened and id-canonicalized first, so a pure
        /// renumbering is not a divergence and an equality-relation change is.
        /// </summary>
        public static TraceDivergence Compare(BehavioralTrace left, BehavioralTrace right)
            => CompareTokens(
                IdEqualityCanonicalizer.Canonicalize(TraceFlattener.Flatten(left)),
                IdEqualityCanonicalizer.Canonicalize(TraceFlattener.Flatten(right)));

        /// <summary>
        /// Classify the FIRST divergence between two already-canonicalized streams.
        ///
        /// <para>Order is tested before value because a reordering otherwise surfaces as a run of
        /// unrelated semantic differences — the "misreported as a logic bug" half of finding 4. The
        /// test is a multiset comparison over (kind, value) ignoring path: the same facts in a
        /// different arrangement. Its honest limit is that two equal-valued facts swapping between
        /// different positions also reads as Order, which is the correct call for a candidate-order
        /// difference and an imprecise one for a coincidence.</para>
        /// </summary>
        public static TraceDivergence CompareTokens(IReadOnlyList<TraceToken> left, IReadOnlyList<TraceToken> right)
        {
            if (left is null) throw new ArgumentNullException(nameof(left));
            if (right is null) throw new ArgumentNullException(nameof(right));

            int n = Math.Min(left.Count, right.Count);
            int firstDiff = -1;
            for (int i = 0; i < n; i++)
            {
                if (!left[i].Equals(right[i])) { firstDiff = i; break; }
            }
            if (firstDiff < 0 && left.Count == right.Count) return TraceDivergence.None;

            if (SameMultiset(left, right))
            {
                int at = firstDiff < 0 ? n : firstDiff;
                return new TraceDivergence
                {
                    Kind = DivergenceKind.Order,
                    Index = at,
                    Path = at < left.Count ? left[at].Path : right[at].Path,
                    Left = at < left.Count ? left[at].Value : null,
                    Right = at < right.Count ? right[at].Value : null,
                };
            }

            if (firstDiff < 0)
            {
                // One stream is a strict prefix of the other: a step or field exists on one side only.
                return new TraceDivergence
                {
                    Kind = DivergenceKind.Structural,
                    Index = n,
                    Path = left.Count > right.Count ? left[n].Path : right[n].Path,
                    Left = left.Count > right.Count ? left[n].Value : null,
                    Right = right.Count > left.Count ? right[n].Value : null,
                };
            }

            var l = left[firstDiff];
            var r = right[firstDiff];

            if (!string.Equals(l.Path, r.Path, StringComparison.Ordinal) || l.Kind != r.Kind)
            {
                return new TraceDivergence
                {
                    Kind = DivergenceKind.Structural,
                    Index = firstDiff,
                    Path = l.Path,
                    Left = l.Path + "=" + l.Value,
                    Right = r.Path + "=" + r.Value,
                };
            }

            return new TraceDivergence
            {
                Kind = l.Kind == TokenKind.Id ? DivergenceKind.Identity : DivergenceKind.Semantic,
                Index = firstDiff,
                Path = l.Path,
                Left = l.Value,
                Right = r.Value,
            };
        }

        private static bool SameMultiset(IReadOnlyList<TraceToken> a, IReadOnlyList<TraceToken> b)
        {
            if (a.Count != b.Count) return false;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in a)
            {
                var k = (int)t.Kind + " " + t.Value;
                counts.TryGetValue(k, out var c);
                counts[k] = c + 1;
            }
            foreach (var t in b)
            {
                var k = (int)t.Kind + " " + t.Value;
                if (!counts.TryGetValue(k, out var c) || c == 0) return false;
                counts[k] = c - 1;
            }
            return true;
        }
    }
}
