using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests
{
    /// <summary>
    /// EXPERIMENT (effect-model fork). Arranges frost in tests that used to assign
    /// <c>IsFrosted = true</c> directly, back when it was a settable field.
    ///
    /// <para>The default is <c>turnsRemaining: 0</c> — "already frosted and already aged", which is
    /// what a bare <c>IsFrosted = true</c> meant: the character is frozen NOW and thaws at the end of
    /// its controller's current turn. Pass <c>1</c> for a freeze that has not yet been carried through
    /// a turn start, i.e. one that still owes a full turn.</para>
    ///
    /// <para>The no-source overloads apply with <c>Source = null</c> — after Phase 0.5 item 1 that is
    /// the correct "nothing applied this" encoding, not a collision with the redaction sentinel
    /// (<c>default</c> for <c>InstanceId?</c> already means the same thing, so their behavior is
    /// unchanged). Use the <see cref="InstanceId"/>-taking overloads only where a test's assertions
    /// genuinely depend on WHO applied the frost — most arrangement-only calls do not.</para>
    /// </summary>
    public static class TestFrost
    {
        public static void Apply(CardInstance c, int turnsRemaining = 0)
            => Apply(c.Effects, null, turnsRemaining);

        public static void Apply(CardInstance c, InstanceId source, int turnsRemaining = 0)
            => Apply(c.Effects, source, turnsRemaining);

        public static void Apply(HeroInstance h, int turnsRemaining = 0)
            => Apply(h.Effects, null, turnsRemaining);

        public static void Apply(HeroInstance h, InstanceId source, int turnsRemaining = 0)
            => Apply(h.Effects, source, turnsRemaining);

        private static void Apply(List<EffectInstance> effects, InstanceId? source, int turnsRemaining)
            => EffectList.Apply(effects, new EffectInstance(
                EffectKind.Frost, source, EffectScope.ControllerTurns, turnsRemaining));
    }
}
