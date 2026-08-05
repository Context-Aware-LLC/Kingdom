using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// The single damage/heal door (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §7 combat, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §4.1 keyword
    /// hooks). The fixed order is: <b>Aegis absorb → health/armor → Lifedrain heal → Venom lethality
    /// → Frost freeze</b>.
    /// Both DEAL_DAMAGE effects (spells/battlecries) and the Attack command (step 2) route through here so
    /// the keyword interactions resolve identically and deterministically. Integer-only. Death is NOT
    /// resolved here — characters dropped to ≤0 are settled as a batch by the interpreter's DeathCheck.
    ///
    /// <para><b>EF-013 — the queue, and why on-damage triggers ENQUEUE rather than resolve here.</b>
    /// Both entry points now take the live <see cref="EffectInterpreter"/> instead of a bare
    /// <c>(GameState, EventBuffer)</c>, so a hit can raise a follow-on trigger (ON_DAMAGED, EF-014;
    /// ON_HEAL, EF-015) by <b>enqueuing</b> it onto the interpreter's shared queue. It never drains that
    /// queue itself. That matters most for ON_HEAL, which is raised <b>reentrantly inside
    /// <see cref="ApplyDamage"/></b> on the LIFESTEAL path below — enqueue-only is what keeps a heal
    /// raised mid-hit from resolving before the hit that caused it has finished landing.</para>
    ///
    /// <para><b>Decision: observers do NOT drain between hits (enqueue-only).</b> A single command can fire
    /// several <see cref="ApplyDamage"/> calls before anything drains — <see cref="CommandProcessor"/>'s
    /// <c>ApplyAttack</c> lands up to three (attacker→defender, Overwhelm overflow→hero, retaliation→attacker)
    /// ahead of one <c>interp.Drain()</c>, and a <c>DEAL_DAMAGE</c> AoE sweeps every target in
    /// <c>DealDamageHandler</c> before control returns to the drain loop. In both cases every hit lands first;
    /// the queued on-damage triggers resolve afterwards, together, at the existing command boundary. The
    /// rejected alternative — draining after each hit — would let an on-damage Divine Shield gained from hit #1
    /// retroactively absorb hit #2 of the same volley. Three reasons it loses:
    /// <list type="number">
    /// <item>It matches neither today's ordering nor standard TCG semantics (an AoE volley strikes every
    ///   target "simultaneously"; a shield conjured mid-volley protects nobody already in the volley).</item>
    /// <item>Draining after hit #1 settles deaths mid-<c>ApplyAttack</c>, so the defender could leave the board
    ///   before retaliation reads <c>defender.Minion.CurrentAttack</c> at <c>CommandProcessor.cs</c> — silently
    ///   reordering combat.</item>
    /// <item><b>Termination.</b> The only recursion bound in the engine is
    ///   <see cref="MatchEngine.ActionResolutionCap"/>, enforced once, inside <see cref="EffectInterpreter.Drain"/>.
    ///   Routing every triggered effect through that single loop keeps a counter-damage cycle (A damaged → hits
    ///   B → B damaged → hits A → …) under the cap and guaranteed to terminate. An inline drain here would need
    ///   its own bound and would put the cap in two places.</item>
    /// </list></para>
    /// </summary>
    internal static class DamagePipeline
    {
        /// <summary>
        /// Deal <paramref name="amount"/> damage to <paramref name="target"/>, using <paramref name="interp"/>
        /// for state/events and to enqueue any on-damage trigger. <paramref name="sourceKeywords"/> is the
        /// keyword set of the dealing character (drives LIFESTEAL/POISONOUS); pass null for sourceless damage
        /// (e.g. fatigue is handled elsewhere). Emits <c>onDamage</c> (with AegisAbsorbed when a shield eats the
        /// hit) and raises the damaged minion's ON_DAMAGED onto the queue. No-op for non-positive amounts.
        /// </summary>
        public static void ApplyDamage(EffectInterpreter interp, CharacterRef target, int amount,
            InstanceId? source, HashSet<Keyword>? sourceKeywords)
        {
            if (amount <= 0) return;

            var state = interp.State;
            var buf = interp.Events;

            // Aegis (DIVINE_SHIELD) absorbs the whole hit, then clears — no health lost, no on-hit riders,
            // and (returning here) no ON_DAMAGED: an absorbed hit is not "damaged" (R-2(c) no-riders precedent).
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
                // Lifedrain: the source's controller hero heals for the damage dealt (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §4.1).
                if (sourceKeywords.Contains(Keyword.LIFESTEAL) &&
                    Targeting.TryControllerSeat(state, source.Value, out int healerSeat))
                {
                    ApplyHeal(interp, CharacterRef.OfHero(healerSeat, state.Players[healerSeat].Hero),
                        amount, source);
                }

                // Venom: any damage to a minion is lethal — mark for the batch DeathCheck.
                if (sourceKeywords.Contains(Keyword.POISONOUS) && target.IsMinion)
                    target.Minion!.CurrentHealth = 0;

                // Frost: whatever this damaged is frozen (hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/balance-guardrails.md §2.1 "Frost",
                // ~1 mana — "denies one attack from whatever it damages"). Applies to HEROES as
                // well as minions, unlike Venom above: freezing a hero denies a weapon swing, and
                // the FREEZE *action* (FreezeHandler) already treats both the same way, so the
                // keyword and the action must not disagree. Last in the fixed order because it is
                // the only rider that cannot change whether the target dies — Venom must have
                // already run, and freezing a corpse is harmless (RulesEngine clears IsFrozen when
                // an instance leaves the board).
                //
                // Two cases deliberately do NOT freeze, both by existing precedent rather than new
                // rules: an Aegis-absorbed hit returns above before any rider (G0 ruling R-2(c),
                // "no on-hit riders"), and Overwhelm overflow reaches here with a null
                // sourceKeywords (CommandProcessor.cs:530), so it cannot carry Frost to the face.
                if (sourceKeywords.Contains(Keyword.FREEZE))
                {
                    if (target.IsMinion) target.Minion!.IsFrozen = true;
                    else target.Hero!.IsFrozen = true;
                    buf.Emit(VfxEvent.onFreeze, source: source, target: target.Id);
                }
            }

            // ON_DAMAGED (self-trigger): a minion that actually took the hit fires its own on-damaged effects.
            // ENQUEUED only — resolution is deferred to the interpreter's bounded Drain (see class remarks:
            // no drain between hits, cap lives once in EffectInterpreter.Drain). Heroes carry no card effects,
            // so only minions raise; the Aegis-absorbed path returned above, so absorbed ≠ damaged. Poisoned
            // targets still raise here — they were damaged this instant; DeathCheck settles them next drain.
            if (target.IsMinion)
                interp.RaiseSelf(target.Minion!, Trigger.ON_DAMAGED, System.Array.Empty<TargetRef>());
        }

        /// <summary>
        /// Restore up to <paramref name="amount"/> health to <paramref name="target"/>, clamped to its max,
        /// using <paramref name="interp"/> for events and to enqueue the healed minion's ON_HEAL.
        /// Emits <c>onHeal</c> with the effective amount healed. Returns the amount actually restored.
        ///
        /// <para><b>EF-015 — ON_HEAL is a SELF trigger on the healed minion, not an observer sweep.</b>
        /// It mirrors ON_DAMAGED at the sibling site exactly: <see cref="EffectInterpreter.RaiseSelf"/> on
        /// the character that actually gained health, minions only. So it reads "whenever THIS minion is
        /// healed", never "whenever any character is healed". The rejected alternative —
        /// <see cref="EffectInterpreter.RaiseObservers"/>, the shape ON_SPELL_CAST and ON_TURN_END use —
        /// loses on three counts:
        /// <list type="number">
        /// <item><b>Symmetry with its sibling.</b> ApplyDamage raises ON_DAMAGED on the damaged minion.
        ///   A heal arriving through the same door and fanning out to every permanent instead would make
        ///   the two halves of one pipeline mean different things by the same kind of name.</item>
        /// <item><b>Reentrancy cost.</b> This method is called from inside <see cref="ApplyDamage"/> on the
        ///   LIFESTEAL path. An observer sweep there would enqueue one effect per permanent on BOTH boards
        ///   for every lifesteal swing — a fan-out the queue bound (<see cref="MatchEngine.ActionResolutionCap"/>)
        ///   would then have to absorb, on a path no card asked for.</item>
        /// <item><b>Direction of travel.</b> Widening later is additive — a distinct observer trigger
        ///   ("whenever any character is healed") can be added beside this one. Narrowing an observer
        ///   ON_HEAL afterwards would silently break every card authored against it.</item>
        /// </list></para>
        ///
        /// <para><b>Two paths deliberately raise nothing.</b> An <i>ineffective</i> heal — a target already
        /// at max health — returns at the <c>healed &lt;= 0</c> guard below, before the raise: nothing was
        /// restored, so nothing was healed (the same "the no-op is not the event" precedent as an
        /// Aegis-absorbed hit in <see cref="ApplyDamage"/>). And a <b>hero</b> heal raises nothing, because
        /// heroes carry no card effects — which is why LIFESTEAL, whose heal always targets the source's
        /// own hero, fires no ON_HEAL today despite being the reentrant path.</para>
        /// </summary>
        public static int ApplyHeal(EffectInterpreter interp, CharacterRef target, int amount, InstanceId? source)
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

            interp.Events.Emit(VfxEvent.onHeal, source: source, target: target.Id, amount: healed);

            // ON_HEAL (self-trigger, EF-015): a minion that actually gained health fires its own on-heal
            // effects. ENQUEUED only — resolution is deferred to the interpreter's bounded Drain, exactly
            // as ON_DAMAGED is (see class remarks: no drain between hits, cap lives once in
            // EffectInterpreter.Drain). This is the LAST thing the heal does, so the target's health is
            // already restored when the effect resolves. Heroes do not raise (no card effects); an
            // overheal returned above (nothing restored is not "healed").
            if (target.IsMinion)
                interp.RaiseSelf(target.Minion!, Trigger.ON_HEAL, System.Array.Empty<TargetRef>());

            return healed;
        }
    }
}
