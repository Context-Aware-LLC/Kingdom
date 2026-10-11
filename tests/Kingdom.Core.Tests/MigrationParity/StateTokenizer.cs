using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.MigrationParity
{
    /// <summary>
    /// Renders a <see cref="GameState"/> as an ordered token stream for the migration comparator.
    ///
    /// <para><b>Why this is not <c>GameStateSerializer</c>.</b> The comparator is specified to compare
    /// "a normalized behavioral trace, never internal hashes or raw event rows"
    /// (`docs/effect-model-fork.md`, "Migration is a temporary whole-engine manifest"). Two concrete
    /// consequences: the comparator needs a stream it can rewrite (id canonicalization) and attribute
    /// (which field diverged), which a digest cannot provide; and pinning parity to the hash walk's
    /// field ORDER would pin the implementation being replaced — the same reason draw-sequence
    /// equality is never demanded.</para>
    ///
    /// <para><b>Why it is still safe from drift.</b> The walk is driven by
    /// <see cref="CanonicalStateFixtures.Manifest"/>, which
    /// <c>CanonicalSerializationTests.State_Type_Fields_Match_Committed_Manifest</c> already locks
    /// against the real state types by reflection. A new state field cannot reach the engine without
    /// going through that lock, and once it is in the manifest this walk reaches it automatically.
    /// The walk <b>fails closed</b> on a field type it does not handle rather than skipping it,
    /// because a silently skipped field is a field the gate is not checking
    /// (`docs/effect-model-fork.md`: "what it does not compare is not being checked").</para>
    /// </summary>
    public static class StateTokenizer
    {
        /// <summary>The reserved redaction sentinel — never minted, so never renumbered.</summary>
        public const string RedactedId = "redacted";

        /// <summary>Absence, distinct from the sentinel since Phase 0.5 item 1.</summary>
        public const string AbsentId = "none";

        private const BindingFlags Fields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static List<TraceToken> Tokenize(GameState state)
        {
            if (state is null) throw new ArgumentNullException(nameof(state));
            var tokens = new List<TraceToken>();
            WalkStateObject("", state, typeof(GameState), tokens);
            return tokens;
        }

        /// <summary>Flat text rendering — the form a divergence report quotes.</summary>
        public static string Render(IReadOnlyList<TraceToken> tokens)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in tokens) sb.Append(t.Path).Append('=').Append(t.Value).Append('\n');
            return sb.ToString();
        }

        private static void WalkStateObject(string path, object obj, Type type, List<TraceToken> tokens)
        {
            // Manifest order (ordinal-sorted) is the walk order: deterministic, and already locked
            // against the type by the inventory guard.
            foreach (var name in CanonicalStateFixtures.Manifest[type])
            {
                var f = type.GetField(name, Fields)
                    ?? throw new InvalidOperationException(
                        $"{type.Name}.{name} is in the manifest but not on the type — the inventory lock should have caught this.");
                WalkValue(path.Length == 0 ? name : path + "." + name,
                    f.GetValue(obj), f.FieldType, type, name, tokens);
            }
        }

        private static void WalkValue(string path, object? value, Type declared,
            Type ownerType, string fieldName, List<TraceToken> tokens)
        {
            if (IdFields.IsIdField(ownerType, fieldName))
            {
                tokens.Add(new TraceToken(path, TokenKind.Id, IdText(value)));
                return;
            }

            switch (value)
            {
                case null when declared == typeof(PendingChoice) || declared == typeof(CardInstance):
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, "absent"));
                    return;
                case null:
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, "null"));
                    return;

                case bool b:
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, b ? "true" : "false"));
                    return;
                case int i:
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, i.ToString(CultureInfo.InvariantCulture)));
                    return;
                case ulong u:
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, u.ToString(CultureInfo.InvariantCulture)));
                    return;
                case string s:
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, s));
                    return;
                case Enum e:
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, e.ToString()));
                    return;
                case PlayerId p:
                    // A seat id is supplied by the authority, not minted from the counter — it is
                    // stable across engines and compares by value.
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, p.Value));
                    return;
                case MatchId m:
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, m.Value));
                    return;
                case InstanceId:
                    throw new NotSupportedException(
                        $"{path}: an InstanceId reached the scalar walk. {ownerType.Name}.{fieldName} " +
                        "carries an identity but IdFields does not classify it — the comparator would " +
                        "compare it by value and report a pure renumbering as a divergence.");
                case DeterministicRandom rng:
                    tokens.Add(new TraceToken(path + ".RawState", TokenKind.Scalar,
                        rng.RawState.ToString(CultureInfo.InvariantCulture)));
                    return;
            }

            var t = value.GetType();

            if (CanonicalStateFixtures.Manifest.ContainsKey(t))
            {
                if (declared == typeof(PendingChoice) || declared == typeof(CardInstance))
                    tokens.Add(new TraceToken(path, TokenKind.Scalar, "present"));
                WalkStateObject(path, value, t, tokens);
                return;
            }

            if (value is IEnumerable seq && t != typeof(string))
            {
                var items = new List<object?>();
                foreach (var item in seq) items.Add(item);

                // The one unordered collection in state gets the same total order the canonical walk
                // imposes, for the same reason: a set has no order of its own to compare.
                if (t == typeof(List<Keyword>))
                    items.Sort((a, b) => ((int)(Keyword)a!).CompareTo((int)(Keyword)b!));

                tokens.Add(new TraceToken(path + ".Count", TokenKind.Scalar,
                    items.Count.ToString(CultureInfo.InvariantCulture)));
                for (int i = 0; i < items.Count; i++)
                {
                    var element = items[i];
                    var elementPath = path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element is not null && CanonicalStateFixtures.Manifest.ContainsKey(element.GetType()))
                        WalkStateObject(elementPath, element, element.GetType(), tokens);
                    else
                        WalkValue(elementPath, element, element?.GetType() ?? typeof(object),
                            ownerType, fieldName + "[]", tokens);
                }
                return;
            }

            throw new NotSupportedException(
                $"{path}: the comparator has no rendering for {t.FullName} ({ownerType.Name}.{fieldName}). " +
                "Add one — a state field the tokenizer skips is a field the migration gate is not checking.");
        }

        private static string IdText(object? value)
        {
            InstanceId? id = value switch
            {
                null => null,
                InstanceId v => v,
                _ => throw new NotSupportedException($"expected an InstanceId, got {value.GetType().FullName}"),
            };
            if (!id.HasValue) return AbsentId;
            // D-P1: InstanceId(0) is reserved and never minted, so it is a CONSTANT with a meaning
            // rather than an identity. Renumbering it would let a real id in one engine align with a
            // redaction in the other.
            if (id.Value.Value == 0) return RedactedId;
            return id.Value.Value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
