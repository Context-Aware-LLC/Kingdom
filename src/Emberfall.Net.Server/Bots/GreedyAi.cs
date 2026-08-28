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
    /// <c>ANY_CHARACTER</c> (ember_flame_bolt ×2, ember_scorch ×2) and its weapon is ember_cinder_lance ×2,
    /// while TIDECALLER's frost_ripple auto-resolves via <c>ANY_ENEMY_MINION</c> and deep_bargain just
    /// draws. Six of EMBERWARD's twenty cards were unplayable and none of TIDECALLER's were, which
    /// produced a 72.5/27.5 split that was a harness artifact end to end. A driver's weaknesses have
    /// to fall symmetrically on the classes or the sample measures the driver, not the cards.</para>
    ///
    /// <para>Every intent is gated through <see cref="ICommandValidator"/> before it is returned, so
    /// a rejection during a soak run is a real finding rather than routine noise.</para>
    /// </summary>
    public static class GreedyAi
    {
        /// <summary>Dataset provenance label. Bump whenever choice ordering or heuristics change.</summary>
        public const string PolicyVersion = "greedy-ai.v2";

        /// <summary>The intent this seat wants next, or null if it is not this seat's move.</summary>
        public static PlayerIntent? Choose(GameState state, PlayerId me, ICommandValidator validator, ICardDatabase db)
        {
            if (state is null || state.Outcome != MatchOutcome.InProgress) return null;

            // A pending choice comes FIRST, ahead of the phase checks — CommandProcessor.CanExecute
            // rejects every other intent with ChoicePending while one is open, END TURN INCLUDED. A
            // driver that cannot answer one does not merely misplay the card: it stalls its own seat
            // for the rest of the match, because BotSeatRunner only re-decides on a new snapshot and
            // no snapshot arrives while the turn cannot end. Unearth is the only kind (Q1 ruling,
            // 2026-07-15) and card 230 Gear Cache is the only collectible that raises it, so the hole
            // was invisible until a CLOCKWRIGHT deck entered the bot roster.
            if (state.PendingChoice is PendingChoice pending && pending.Chooser == me)
            {
                int chosen = ChooseUnearth(pending, state.PlayerOf(me), db);
                if (chosen != 0) return new UnearthChoiceIntent(me, chosen, pending.ChoiceSequence);
            }

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

        /// <summary>
        /// Which Unearth option to take: the dearest one this seat could still afford to cast at its
        /// current crystal ceiling, else the cheapest on offer. Ties break on the lower card id so
        /// the pick is reproducible.
        ///
        /// <para><b>Capped by <c>Mana.Max</c>, not by <c>Available</c>.</b> The card lands in hand and
        /// is cast on some later turn, so spendable-right-now is the wrong bound — but an option that
        /// costs more than the ceiling this seat has actually reached is a card the greedy loop will
        /// step over every turn from here on, which is indistinguishable from having drawn nothing.</para>
        ///
        /// <para>Returns 0 when nothing is offered, or when no option resolves against the database —
        /// the caller then leaves the choice alone rather than submitting an intent the validator will
        /// refuse with <c>InvalidChoice</c>.</para>
        /// </summary>
        private static int ChooseUnearth(PendingChoice pending, PlayerState chooser, ICardDatabase db)
        {
            int best = 0, bestCost = -1;
            int cheapest = 0, cheapestCost = int.MaxValue;
            int ceiling = chooser.Mana.Max;

            foreach (int cardId in pending.Options)
            {
                if (!db.TryGet(cardId, out CardDefinition def)) continue;

                if (def.Cost < cheapestCost || (def.Cost == cheapestCost && cardId < cheapest))
                {
                    cheapestCost = def.Cost;
                    cheapest = cardId;
                }
                if (def.Cost > ceiling) continue;
                if (def.Cost > bestCost || (def.Cost == bestCost && cardId < best))
                {
                    bestCost = def.Cost;
                    best = cardId;
                }
            }

            return best != 0 ? best : cheapest;
        }

        // ── Playing a card ────────────────────────────────────────────────────────

        private static PlayCardIntent? TryPlay(
            GameState state, PlayerId me, CardInstance card, CardDefinition def,
            ICommandValidator validator, ICardDatabase db, PlayerState ps, PlayerState foe)
        {
            int slot = ps.Board.Count;

            // Untargeted first. This covers minions, weapons, draw, and every effect whose selector
            // the interpreter auto-resolves (ANY_ENEMY_MINION, ENEMY_HERO, SELF). Only the explicit-choice
            // ANY_* selectors fall through, which is exactly the set that needs a target supplied.
            var bare = new PlayCardIntent(me, card.Id, slot, Array.Empty<TargetRef>());
            if (validator.CanExecute(state, bare, out _)) return bare;

            // Build the binding-aware choice plan. Spells activate on ON_CAST; all other types on ON_PLAY.
            // This mirrors the trigger CommandProcessor uses in ValidatePlayCard (line 173 there).
            Trigger playTrigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            IReadOnlyList<TargetChoiceRequirement> plan =
                Targeting.PlayerChoicePlan(state, db, me, def, playTrigger);

            if (plan.Count == 0) return null;

            // Single unnamed binding needing ONE pick: the heuristic-ordered candidate loop
            // preserves the hostile/friendly priority (killable-first) for single-target cards. This
            // is the common shape (149 of 171 authored targets as of EF-030). A group wanting more
            // than one pick falls through to the combination search below - it used to fall in HERE
            // and supply exactly one ref, which the validator refused every time.
            if (plan.Count == 1 && plan[0].Binding.Length == 0 && plan[0].RequiredCount <= 1)
            {
                foreach (TargetRef t in PlayCandidates(def, ps, foe))
                {
                    var aimed = new PlayCardIntent(me, card.Id, slot, new[] { t });
                    if (validator.CanExecute(state, aimed, out _)) return aimed;
                }
                return null;
            }

            // Everything else: one candidate list per group, tagged with the group's binding so the
            // validator can match each ref to the correct rule, searched depth-first for the first
            // combination the validator accepts. Each group walks the plan's canonical candidate
            // order, so the pick is reproducible.
            var perGroup = new List<List<TargetRef>>(plan.Count);
            var perGroupCount = new List<int>(plan.Count);
            foreach (TargetChoiceRequirement req in plan)
            {
                var refs = new List<TargetRef>(req.Candidates.Count);
                foreach (TargetCandidate c in req.Candidates)
                    refs.Add(new TargetRef(c.Instance, req.Binding));

                // RequiredCount can exceed what the board offers, and carries int.MaxValue as the
                // conflicting-binding sentinel (Targeting.PlayerChoicePlan) — clamp to what exists.
                // Below MinimumCount the group cannot be satisfied at all, so the card is unplayable
                // this turn; a MinimumCount of 0 is an OPTIONAL cost, which we then decline by
                // picking nothing rather than refusing the whole card.
                int take = req.RequiredCount < refs.Count ? req.RequiredCount : refs.Count;
                if (take < req.MinimumCount) return null;
                perGroup.Add(refs);
                perGroupCount.Add(take);
            }

            var chosen = new List<TargetRef>(plan.Count);
            return TryGroups(state, me, card, slot, validator, perGroup, perGroupCount, chosen, 0);
        }

        /// <summary>
        /// Depth-first over the binding groups, taking each group's required number of DISTINCT
        /// candidates in canonical order. Returns the first combination the validator accepts.
        ///
        /// <para><b>The count is the whole point of this method.</b> It replaced a version that
        /// supplied exactly one ref per group, which left every card asking for two picks — 203
        /// Channeled Pyroclasm's "Deal 6 damage to 2 enemies" among them — permanently unplayable
        /// by this driver: drawn every game, cast never. Nothing reported it as a rejection either,
        /// because the intent was never submitted; the corpus soak's never-played row was the only
        /// trace it left.</para>
        /// </summary>
        private static PlayCardIntent? TryGroups(
            GameState state, PlayerId me, CardInstance card, int slot,
            ICommandValidator validator,
            List<List<TargetRef>> perGroup, List<int> perGroupCount,
            List<TargetRef> chosen, int depth)
        {
            if (depth == perGroup.Count)
            {
                var intent = new PlayCardIntent(me, card.Id, slot, chosen.ToArray());
                return validator.CanExecute(state, intent, out _) ? intent : null;
            }
            return TryCombination(state, me, card, slot, validator, perGroup, perGroupCount,
                                  chosen, depth, from: 0, remaining: perGroupCount[depth]);
        }

        /// <summary>One group's distinct-combination walk: take <paramref name="remaining"/> more
        /// candidates from index <paramref name="from"/> onward, then move to the next group.</summary>
        private static PlayCardIntent? TryCombination(
            GameState state, PlayerId me, CardInstance card, int slot,
            ICommandValidator validator,
            List<List<TargetRef>> perGroup, List<int> perGroupCount,
            List<TargetRef> chosen, int depth, int from, int remaining)
        {
            if (remaining == 0)
                return TryGroups(state, me, card, slot, validator, perGroup, perGroupCount, chosen, depth + 1);

            List<TargetRef> candidates = perGroup[depth];
            for (int i = from; i <= candidates.Count - remaining; i++)
            {
                chosen.Add(candidates[i]);
                PlayCardIntent? result = TryCombination(state, me, card, slot, validator, perGroup,
                                                        perGroupCount, chosen, depth, i + 1, remaining - 1);
                if (result is not null) return result;
                chosen.RemoveAt(chosen.Count - 1);
            }
            return null;
        }

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

    }
}
