using System;
using System.Collections.Generic;
using System.Reflection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.MigrationParity
{
    /// <summary>
    /// What a token in a normalized behavioral trace is. <see cref="Id"/> is the whole point: the
    /// migration comparator must compare id <b>equality relations</b> rather than id values
    /// (`docs/effect-model-fork.md` §"Under adversarial review" finding 4), and it cannot do that
    /// unless the tokenizer says which values are identities. Every other canonical value compares
    /// by value.
    /// </summary>
    public enum TokenKind
    {
        Scalar = 0,
        Id = 1,
    }

    /// <summary>
    /// One canonical value at one structural position. <see cref="Path"/> is a stable structural
    /// address (`Players[0].Hand[0].Id`) rather than an offset, so a length difference names the
    /// position it happened at instead of desynchronizing every later comparison.
    /// </summary>
    public readonly struct TraceToken : IEquatable<TraceToken>
    {
        public readonly string Path;
        public readonly TokenKind Kind;
        public readonly string Value;

        public TraceToken(string path, TokenKind kind, string value)
        {
            Path = path;
            Kind = kind;
            Value = value;
        }

        public bool Equals(TraceToken other)
            => string.Equals(Path, other.Path, StringComparison.Ordinal)
            && Kind == other.Kind
            && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is TraceToken t && Equals(t);
        public override int GetHashCode() => HashCode.Combine(Path, (int)Kind, Value);
        public override string ToString() => $"{Path}={Value}{(Kind == TokenKind.Id ? " (id)" : "")}";
    }

    /// <summary>
    /// Which canonical state fields carry an identity — <b>derived from the field's declared type</b>,
    /// never hand-listed.
    ///
    /// This is the whole reason the comparator does not need a second field list. The id positions
    /// are a fact the state types already state (`InstanceId` / `InstanceId?`), and
    /// `CanonicalStateFixtures.Manifest` is already locked against those types by
    /// `CanonicalSerializationTests.State_Type_Fields_Match_Committed_Manifest`. Deriving from the
    /// pair means an added id field is picked up by the tokenizer the moment the inventory lock
    /// forces it into the manifest — there is no third place to forget.
    /// </summary>
    public static class IdFields
    {
        private const BindingFlags Fields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly HashSet<string> _keys = Derive();

        /// <summary>"Type.Field" for every manifest field whose declared type is an InstanceId.</summary>
        public static IReadOnlyCollection<string> Keys => _keys;

        /// <summary>True when this manifest field's declared type is an InstanceId (nullable or not).</summary>
        public static bool IsIdField(Type declaringType, string fieldName)
            => _keys.Contains(declaringType.Name + "." + fieldName);

        private static HashSet<string> Derive()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in CanonicalStateFixtures.Manifest)
            {
                foreach (var name in kv.Value)
                {
                    var f = kv.Key.GetField(name, Fields);
                    if (f is null) continue;   // the inventory lock is what reports this, not us
                    if (f.FieldType == typeof(InstanceId) || f.FieldType == typeof(InstanceId?))
                        keys.Add(kv.Key.Name + "." + name);
                }
            }
            return keys;
        }
    }
}
