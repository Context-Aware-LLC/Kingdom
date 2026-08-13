using System;
using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Bots
{
    /// <summary>
    /// The soak driver's decision function (C-G1-8 step 3b) — kept deliberately in step with the
    /// shipped <c>Emberfall.AI.PlayingAi</c>, which lives in a Unity assembly and cannot be
    /// referenced from a .NET tool. Fixes here belong there too, and vice versa.
    ///
    /// <para><b>Why a port rather than a stronger AI.</b> The sample this grinds is judged against
    /// the C4 balance bar, and a bar only means something if the thing measured is the thing that
    /// ships. A search-based driver would give cleaner win-rates *of a player nobody will face*.</para>
    ///
    /// <para><b>2026-07-22 — the three holes that invalidated the first 200-match run.</b> The
    /// original could not (1) target, (2) equip-and-swing a weapon, or (3) use the hero power. That
    /// reads as a mild weakness until you notice it is not evenly distributed: EMBERWARD's removal is
    /// <c>ANY_CHARACTER</c> (flame_bolt ×2, ember_scorch ×2) and its weapon is ember_cinder_lance ×2,
    /// while TIDECALLER's frost_ripple auto-resolves via <c>ENEMY_MINIONS</c> and deep_bargain just
    /// draws. Six of EMBERWARD's twenty cards were unplayable and none of TIDECALLER's were, which
    /// produced a 72.5/27.5 split that was a harness artifact end to end. A driver's weaknesses have
    /// to fall symmetrically on the classes or the sample measures the driver, not the cards.</para>
    ///
    /// <para>Every intent is gated through <see cref="ICommandValidator"/> before it is returned, so
    /// a rejection during a soak run is a real finding rather than routine noise.</para>
    /// </summary>
    public static class GreedyAi
    {
        /// <summary>The intent this seat wants next, or null if it is not this seat's move.</summary>
        public static PlayerIntent? Choose(GameState state, PlayerId me, ICommandValidator validator, ICardDatabase db)
        {
            if (state is null || state.Outcome != MatchOutcome.InProgress) return null;

            if (state.Phase == TurnPhase.Mulligan)
            {
                int mulSeat = state.SeatOf(me);
                bool done = mulSeat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
                return done ? null : new MulliganIntent(me, Array.Empty<InstanceId>());
            }

            if (state.Phase != TurnPhase.Main || state.ActivePlayer != me) return null;

            int seat = state.SeatOf(me);
            int foeSeat = seat == 0 ? 1 : 0;
            PlayerState ps = state.Players[seat];
            PlayerState foe = state.Players[foeSeat];

            // 1. First card in hand we can legally play, aimed if it needs aiming.
            foreach (CardInstance card in ps.Hand)
            {
                CardDefinition def = db.Get(card.DefinitionId);
                if (ps.Mana.Available < def.Cost) continue;

                PlayCardIntent? play = TryPlay(state, me, card, def, validator, db, ps, foe);
                if (play is not null) return play;
            }

            // 2. Attack. Minions trade first — the validator, not this loop, enforces bulwark and
            //    shroud — then the hero swings if a weapon is equipped.
            foreach (CardInstance attacker in ps.Board)
            {
                AttackIntent? atk = TryAttack(state, me, attacker.Id, foe, validator, preferFace: false);
                if (atk is not null) return atk;
            }

            if (ps.Weapon is not null)
            {
                // Face first for the hero: swinging into a minion means taking its attack back, and
                // the weapon has durability to spend either way. Bulwark still forces the trade.
                AttackIntent? swing = TryAttack(state, me, ps.Hero.Id, foe, validator, preferFace: true);
                if (swing is not null) return swing;
            }

            // 3. Hero power with whatever mana is left.
            var power = new HeroPowerIntent(me, Array.Empty<TargetRef>());
            if (validator.CanExecute(state, power, out _)) return power;

            foreach (TargetRef t in HeroPowerCandidates(ps, foe))
            {
                var aimed = new HeroPowerIntent(me, new[] { t });
                if (validator.CanExecute(state, aimed, out _)) return aimed;
            }

            return new EndTurnIntent(me);
        }

        // ── Playing a card ────────────────────────────────────────────────────────

        private static PlayCardIntent? TryPlay(
            GameState state, PlayerId me, CardInstance card, CardDefinition def,
            ICommandValidator validator, ICardDatabase db, PlayerState ps, PlayerState foe)
        {
            int slot = ps.Board.Count;

            // Untargeted first. This covers minions, weapons, draw, and every effect whose selector
            // the interpreter auto-resolves (ENEMY_MINIONS, ENEMY_HERO, SELF). Only the explicit-choice
            // ANY_* selectors fall through, which is exactly the set that needs a target supplied.
            var bare = new PlayCardIntent(me, card.Id, slot, Array.Empty<TargetRef>());
            if (validator.CanExecute(state, bare, out _)) return bare;

            // Build the binding-aware choice plan. Spells activate on ON_CAST; all other types on ON_PLAY.
            // This mirrors the trigger CommandProcessor uses in ValidatePlayCard (line 173 there).
            Trigger playTrigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            IReadOnlyList<TargetChoiceRequirement> plan =
                Targeting.PlayerChoicePlan(state, db, me, def, playTrigger);

            if (plan.Count == 0) return null;

            // Single unnamed binding: the existing heuristic-ordered candidate loop preserves the
            // hostile/friendly priority (killable-first) for single-target cards. This is the common
            // shape (149 of 171 authored targets as of EF-030).
            if (plan.Count == 1 && plan[0].Binding.Length == 0)
            {
                foreach (TargetRef t in PlayCandidates(def, ps, foe))
                {
                    var aimed = new PlayCardIntent(me, card.Id, slot, new[] { t });
                    if (validator.CanExecute(state, aimed, out _)) return aimed;
                }
                return null;
            }

            // Named or multi-binding: build one candidate list per group, tag each TargetRef with
            // the group's binding so the validator can match it to the correct rule, then try
            // combinations depth-first. Each group uses the plan's canonical candidate order;
            // the validator is the truth about legality so a first-valid combination is enough.
            var perGroup = new List<List<TargetRef>>(plan.Count);
            foreach (TargetChoiceRequirement req in plan)
            {
                if (req.Candidates.Count == 0) return null; // unsatisfiable group — card is unplayable now
                var refs = new List<TargetRef>(req.Candidates.Count);
                foreach (TargetCandidate c in req.Candidates)
                    refs.Add(new TargetRef(c.Instance, req.Binding));
                perGroup.Add(refs);
            }

            var chosen = new TargetRef[plan.Count];
            return TryCombinations(state, me, card, slot, validator, perGroup, chosen, 0);
        }

        private static PlayCardIntent? TryCombinations(
            GameState state, PlayerId me, CardInstance card, int slot,
            ICommandValidator validator,
            List<List<TargetRef>> perGroup, TargetRef[] chosen, int depth)
        {
            if (depth == perGroup.Count)
            {
                var intent = new PlayCardIntent(me, card.Id, slot, (TargetRef[])chosen.Clone());
                return validator.CanExecute(state, intent, out _) ? intent : null;
            }
            foreach (TargetRef t in perGroup[depth])
            {
                chosen[depth] = t;
                PlayCardIntent? result = TryCombinations(state, me, card, slot, validator, perGroup, chosen, depth + 1);
                if (result is not null) return result;
            }
            return null;
        }

        /// <summary>
        /// Ordered target candidates for an explicit-choice card. Hostile cards want enemies, the
        /// rest want friends; either way the tail covers the other side so a legal play is never
        /// skipped for want of a guess.
        ///
        /// <para>Within each group the board is walked in <b>slot order</b>, not sorted by threat.
        /// A better AI would sort; a sort here would need a total order to stay deterministic, and
        /// board order already is one. Killable-first is the whole heuristic, and it is the one that
        /// matters — spending removal on something it kills.</para>
        /// </summary>
        private static List<TargetRef> PlayCandidates(CardDefinition def, PlayerState ps, PlayerState foe)
        {
            var list = new List<TargetRef>();

            if (IsHostile(def))
            {
                int dmg = DamageAmount(def);
                foreach (CardInstance m in foe.Board)
                    if (dmg > 0 && m.CurrentHealth <= dmg) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(foe.Hero.Id));
                foreach (CardInstance m in foe.Board) list.Add(new TargetRef(m.Id));
                foreach (CardInstance m in ps.Board) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(ps.Hero.Id));
            }
            else
            {
                if (ps.Hero.CurrentHealth < ps.Hero.MaxHealth) list.Add(new TargetRef(ps.Hero.Id));
                foreach (CardInstance m in ps.Board) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(ps.Hero.Id));
                foreach (CardInstance m in foe.Board) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(foe.Hero.Id));
            }

            return list;
        }

        // ── Attacking ─────────────────────────────────────────────────────────────

        private static AttackIntent? TryAttack(
            GameState state, PlayerId me, InstanceId attacker, PlayerState foe,
            ICommandValidator validator, bool preferFace)
        {
            if (preferFace)
            {
                var face = new AttackIntent(me, attacker, new TargetRef(foe.Hero.Id));
                if (validator.CanExecute(state, face, out _)) return face;
            }

            foreach (CardInstance defender in foe.Board)
            {
                var trade = new AttackIntent(me, attacker, new TargetRef(defender.Id));
                if (validator.CanExecute(state, trade, out _)) return trade;
            }

            if (!preferFace)
            {
                var face = new AttackIntent(me, attacker, new TargetRef(foe.Hero.Id));
                if (validator.CanExecute(state, face, out _)) return face;
            }

            return null;
        }

        // ── Hero power ────────────────────────────────────────────────────────────

        /// <summary>
        /// Hero powers are not declared in card data, so their intent cannot be read the way a card's
        /// can. Enemy hero first (the common damage power), then enemy minions, then friendly — the
        /// validator rejects whatever the power cannot legally hit.
        /// </summary>
        private static List<TargetRef> HeroPowerCandidates(PlayerState ps, PlayerState foe)
        {
            var list = new List<TargetRef> { new TargetRef(foe.Hero.Id) };
            foreach (CardInstance m in foe.Board) list.Add(new TargetRef(m.Id));
            list.Add(new TargetRef(ps.Hero.Id));
            foreach (CardInstance m in ps.Board) list.Add(new TargetRef(m.Id));
            return list;
        }

        // ── Reading the card ──────────────────────────────────────────────────────

        private static bool IsHostile(CardDefinition def)
        {
            foreach (RuleDefinition rule in def.Rules)
            {
                switch (rule.Action)
                {
                    case DamageAction _:
                    case DestroyAction _:
                    case FrostAction _:
                    case SilenceAction _:
                    case ReturnToHandAction _:
                    case TransformAction _:
                    case TakeControlAction _:
                        return true;
                }
            }
            return false;
        }

        private static int DamageAmount(CardDefinition def)
        {
            foreach (RuleDefinition rule in def.Rules)
                if (rule.Action is DamageAction damage)
                    return damage.Amount;
            return 0;
        }
    }
}
