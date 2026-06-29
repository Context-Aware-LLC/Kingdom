using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// The single damage/heal door (game-state-and-actions.md §7 combat, card-system.md §4.1 keyword
    /// hooks). The fixed order is: <b>Aegis absorb → health/armor → Lifedrain heal → Venom lethality</b>.
    /// Both DEAL_DAMAGE effects (spells/battlecries) and the Attack command (step 2) route through here so
    /// the keyword interactions resolve identically and deterministically. Integer-only. Death is NOT
    /// resolved here — characters dropped to ≤0 are settled as a batch by the interpreter's DeathCheck.
    /// </summary>
    internal static class DamagePipeline
    {
        /// <summary>
        /// Deal <paramref name="amount"/> damage to <paramref name="target"/>. <paramref name="sourceKeywords"/>
        /// is the keyword set of the dealing character (drives LIFESTEAL/POISONOUS); pass null for sourceless
        /// damage (e.g. fatigue is handled elsewhere). Emits <c>onDamage</c> (with AegisAbsorbed when a shield
        /// eats the hit). No-op for non-positive amounts.
        /// </summary>
        public static void ApplyDamage(GameState state, CharacterRef target, int amount,
            InstanceId? source, HashSet<Keyword>? sourceKeywords, EventBuffer buf)
        {
            if (amount <= 0) return;

            // Aegis (DIVINE_SHIELD) absorbs the whole hit, then clears — no health lost, no on-hit riders.
            if (target.IsMinion && target.Minion!.HasAegisShield)
            {
                target.Minion.HasAegisShield = false;
                buf.Emit(VfxEvent.onDamage, source: source, target: target.Id, amount: 0, flags: EventFlags.AegisAbsorbed);
                return;
            }

            if (target.IsHero)
            {
                var hero = target.Hero!;
                int toHealth = amount;
                if (hero.Armor > 0)
                {
                    int soak = hero.Armor < toHealth ? hero.Armor : toHealth;
                    hero.Armor -= soak;
                    toHealth -= soak;
                }
                hero.CurrentHealth -= toHealth;
            }
            else
            {
                target.Minion!.CurrentHealth -= amount;
            }

            buf.Emit(VfxEvent.onDamage, source: source, target: target.Id, amount: amount);

            if (sourceKeywords != null && source.HasValue)
            {
                // Lifedrain: the source's controller hero heals for the damage dealt (card-system.md §4.1).
                if (sourceKeywords.Contains(Keyword.LIFESTEAL) &&
                    Characters.TryFind(state, source.Value, out var src))
                {
                    int healerSeat = src.Seat;
                    ApplyHeal(state, CharacterRef.OfHero(healerSeat, state.Players[healerSeat].Hero),
                        amount, source, buf);
                }

                // Venom: any damage to a minion is lethal — mark for the batch DeathCheck.
                if (sourceKeywords.Contains(Keyword.POISONOUS) && target.IsMinion)
                    target.Minion!.CurrentHealth = 0;
            }
        }

        /// <summary>
        /// Restore up to <paramref name="amount"/> health to <paramref name="target"/>, clamped to its max.
        /// Emits <c>onHeal</c> with the effective amount healed. Returns the amount actually restored.
        /// </summary>
        public static int ApplyHeal(GameState state, CharacterRef target, int amount, InstanceId? source, EventBuffer buf)
        {
            if (amount <= 0) return 0;

            int before, max;
            if (target.IsHero) { before = target.Hero!.CurrentHealth; max = target.Hero.MaxHealth; }
            else { before = target.Minion!.CurrentHealth; max = target.Minion.MaxHealth; }

            int after = before + amount;
            if (after > max) after = max;
            int healed = after - before;
            if (healed <= 0) return 0;

            if (target.IsHero) target.Hero!.CurrentHealth = after;
            else target.Minion!.CurrentHealth = after;

            buf.Emit(VfxEvent.onHeal, source: source, target: target.Id, amount: healed);
            return healed;
        }
    }
}
