using System;
using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// Stable, immutable serialization tag for a <see cref="PlayerIntent"/> subtype
    /// (game-state-and-actions.md §6.3). Fixtures serialize by this tag, never by C# type
    /// name, so renaming a record never breaks a golden fixture baked today.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ReplayTagAttribute : Attribute
    {
        public string Tag { get; }
        public ReplayTagAttribute(string tag) => Tag = tag;
    }

    /// <summary>
    /// A reference to a target character/permanent within a play/attack/hero-power intent.
    /// Resolved and legality-checked up front (card-system.md §3.2) so resolution stays
    /// server-authoritative and deterministic.
    /// </summary>
    public readonly struct TargetRef : IEquatable<TargetRef>
    {
        public readonly InstanceId Instance;
        public TargetRef(InstanceId instance) => Instance = instance;
        public bool Equals(TargetRef other) => Instance.Equals(other.Instance);
        public override bool Equals(object? obj) => obj is TargetRef o && Equals(o);
        public override int GetHashCode() => Instance.GetHashCode();
        public override string ToString() => Instance.ToString();
    }

    /// <summary>Tier-1 raw intent from a source (UI / AI / network). May be illegal until validated.</summary>
    public abstract record PlayerIntent(PlayerId Player);

    [ReplayTag("play_card")]
    public sealed record PlayCardIntent(PlayerId Player, InstanceId Card, int BoardSlot, IReadOnlyList<TargetRef> Targets)
        : PlayerIntent(Player);

    [ReplayTag("attack")]
    public sealed record AttackIntent(PlayerId Player, InstanceId Attacker, TargetRef Defender)
        : PlayerIntent(Player);

    [ReplayTag("hero_power")]
    public sealed record HeroPowerIntent(PlayerId Player, IReadOnlyList<TargetRef> Targets)
        : PlayerIntent(Player);

    [ReplayTag("end_turn")]
    public sealed record EndTurnIntent(PlayerId Player) : PlayerIntent(Player);

    [ReplayTag("mulligan")]
    public sealed record MulliganIntent(PlayerId Player, IReadOnlyList<InstanceId> ToReplace) : PlayerIntent(Player);

    [ReplayTag("discover_choice")]
    public sealed record DiscoverChoiceIntent(PlayerId Player, string ChosenCardId) : PlayerIntent(Player);

    [ReplayTag("concede")]
    public sealed record ConcedeIntent(PlayerId Player) : PlayerIntent(Player);

    /// <summary>
    /// System-issued command produced by a session-layer timer expiry (mulligan/turn clock,
    /// GDD §1b). The core is timeless (game-state-and-actions.md §6.2): timer expiry is just
    /// another command in the log, never a wall-clock call.
    /// </summary>
    [ReplayTag("auto_confirm")]
    public sealed record AutoConfirmIntent(PlayerId Player) : PlayerIntent(Player);
}
