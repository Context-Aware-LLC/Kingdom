using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// EXPERIMENT (effect-model fork), Phase 0.5 item 3. The shape shared by every character that can
    /// carry an <see cref="EffectInstance"/> list — <see cref="HeroInstance"/> and
    /// <see cref="CardInstance"/>. Exists so a caller holding "a minion or a hero, not yet resolved
    /// which" can resolve to ONE reference and call into <see cref="EffectList"/> once, instead of the
    /// <c>if (IsMinion) … else if (IsHero) …</c> pair that used to precede every access to a target's
    /// <c>Effects</c> — see the FROST handler in <c>ActionHandlers.cs</c> and the FROST damage rider in
    /// <c>DamagePipeline.cs</c>, both collapsed onto this interface. Phase 4's candidate enumeration is
    /// expected to widen over this same interface rather than grow a third parallel branch pair.
    ///
    /// <para>Both implementers keep their existing public <c>Effects</c> FIELD untouched and satisfy
    /// this via an explicit interface implementation that forwards to it — every pre-existing
    /// <c>x.Effects</c> read/mutate call site (serializer, keyword semantics, turn-tick sweeps, tests)
    /// keeps compiling against the field exactly as before; only code that holds an
    /// <see cref="IEffectHost"/> reference goes through the interface property.</para>
    /// </summary>
    public interface IEffectHost
    {
        List<EffectInstance> Effects { get; }
    }

    /// <summary>
    /// The two derived booleans <see cref="HeroInstance"/> and <see cref="CardInstance"/> each used to
    /// declare identically. Written once here instead, over <see cref="IEffectHost"/>. Genuine extension
    /// METHODS (not the newer extension-property syntax) — this project is also read by Unity's own,
    /// older Roslyn via `.asmdef`, so nothing here should assume a C# version newer than plain
    /// extension methods can compile on both toolchains.
    /// </summary>
    public static class EffectHostExtensions
    {
        /// <summary>Set by the FROST verb; cleared at END of the controller's next turn (NOR-355/NOR-367).
        /// Derived from <see cref="IEffectHost.Effects"/> — nothing stores this bit directly.</summary>
        public static bool IsFrosted(this IEffectHost host) => EffectList.Has(host.Effects, EffectKind.Frost);

        /// <summary>NOR-367's second boolean: an instance still owing turns has not yet been aged by its
        /// controller's turn start, which is exactly what "pending" meant — e.g. a minion frozen during
        /// its OWN turn (tide_hollowshell_warden's ON_DEATH) still owes a full turn without a flag
        /// conspiring to say so.</summary>
        public static bool FrostThawPending(this IEffectHost host) => EffectList.TurnsRemaining(host.Effects, EffectKind.Frost) > 0;
    }
}
