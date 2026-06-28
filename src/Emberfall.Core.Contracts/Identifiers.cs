using System;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// Stable identity of a player seat as supplied by the authority at match start
    /// (interfaces.md §2.1 BeginMatch). Value equality; ordering is by string ordinal
    /// so it can participate in a deterministic canonical walk if ever needed.
    /// </summary>
    public readonly struct PlayerId : IEquatable<PlayerId>, IComparable<PlayerId>
    {
        public readonly string Value;
        public PlayerId(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
        public bool Equals(PlayerId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is PlayerId o && Equals(o);
        public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public int CompareTo(PlayerId other) => string.CompareOrdinal(Value, other.Value);
        public override string ToString() => Value ?? "<null>";
        public static bool operator ==(PlayerId a, PlayerId b) => a.Equals(b);
        public static bool operator !=(PlayerId a, PlayerId b) => !a.Equals(b);
    }

    /// <summary>Identity of a match. Provenance only; never decides rules.</summary>
    public readonly struct MatchId : IEquatable<MatchId>
    {
        public readonly string Value;
        public MatchId(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
        public bool Equals(MatchId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is MatchId o && Equals(o);
        public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? "<null>";
        public static bool operator ==(MatchId a, MatchId b) => a.Equals(b);
        public static bool operator !=(MatchId a, MatchId b) => !a.Equals(b);
    }

    /// <summary>
    /// Unique-within-match card/character id assigned by a deterministic per-match counter
    /// (game-state-and-actions.md §2). Because ids are deterministic they are PART of what the
    /// golden-replay test asserts (golden-replay.md §3.2). Serializes as "i&lt;n&gt;".
    /// </summary>
    public readonly struct InstanceId : IEquatable<InstanceId>, IComparable<InstanceId>
    {
        public readonly int Value;
        public InstanceId(int value) => Value = value;
        public bool Equals(InstanceId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is InstanceId o && Equals(o);
        public override int GetHashCode() => Value;
        public int CompareTo(InstanceId other) => Value.CompareTo(other.Value);
        public override string ToString() => "i" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static bool operator ==(InstanceId a, InstanceId b) => a.Equals(b);
        public static bool operator !=(InstanceId a, InstanceId b) => !a.Equals(b);
    }
}
