using System;
using System.Collections.Generic;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.AI
{
    /// <summary>
    /// Greedy AI for the vertical slice. Each turn: play the first affordable card (aiming it if it
    /// needs aiming), trade with each ready minion, swing the hero if a weapon is equipped, use the
    /// hero power, then end the turn. Every intent is gated through <see cref="ICommandValidator"/>
    /// before it is queued, so rejections are near-impossible.
    ///
    /// <para><b>Kept in step with <c>Kingdom.Tools.NetSoak.GreedyAi</c>.</b> That is the driver
    /// that grinds the `ai_soak` balance sample, and a balance bar only means something if the AI
    /// being measured is the AI that ships. Fix one, fix the other.</para>
    ///
    /// <para><b>2026-07-22 — three holes closed.</b> This could not target, could not swing a weapon,
    /// and never used the hero power. That reads as mild weakness until you notice it lands
    /// asymmetrically: EMBERWARD's removal is <c>ANY_CHARACTER</c> (ember_flame_bolt, ember_scorch) and its
    /// weapon is ember_cinder_lance, while TIDECALLER's frost_ripple auto-resolves and deep_bargain
    /// just draws. Six of EMBERWARD's twenty cards were unplayable and none of TIDECALLER's were —
    /// found when the soak reported a 72.5/27.5 split that turned out to be this bug, not the cards.
    /// It meant the AI played EMBERWARD with a third of its deck dead.</para>
    ///
    /// <para><b>This is not a single-player opponent, and there is no offline mode.</b> It fills OUR
    /// OWN seat, from the one site that constructs it — <c>NetworkedGameBootstrap</c>, when no input
    /// router is present — inside a live networked match. Every opponent bot lives server-side in
    /// <c>Kingdom.Net.Server/Bots/</c>. An earlier version of this remark said "the shipped
    /// single-player game", which is why more than one session read offline play as supported.</para>
    ///
    /// Replace with a search-based AI in M2.
    /// </summary>
    public sealed class PlayingAi : IIntentSource
    {
        private readonly PlayerId _playerId;
        private readonly ICommandValidator _validator;
        private readonly ICardDatabase _db;

        private GameState _state;
        private PlayerIntent _pending;

        public PlayingAi(PlayerId playerId, ICardDatabase db)
        {
            _playerId  = playerId;
            _db        = db;
            _validator = new CommandProcessor(db);
        }

        public void NotifyStateUpdate(GameState state)
        {
            _state   = state;
            _pending = ComputeIntent(state);
        }

        public bool TryGetIntent(out PlayerIntent intent)
        {
            intent   = _pending;
            _pending = null;
            return intent != null;
        }

        public void OnRejected(RejectReason reason)
        {
            Debug.LogWarning($"[PlayingAi:{_playerId}] intent rejected — {reason}. Falling back to EndTurn.");
            if (_state != null && _state.ActivePlayer == _playerId && _state.Phase == TurnPhase.Main)
                _pending = new EndTurnIntent(_playerId);
        }

        private PlayerIntent ComputeIntent(GameState state)
        {
            if (state == null || state.Outcome != MatchOutcome.InProgress)
                return null;

            // A pending choice comes FIRST, ahead of the phase checks — CommandProcessor.CanExecute
            // rejects every other intent with ChoicePending while one is open, END TURN INCLUDED,
            // which is also why OnRejected's EndTurn fallback cannot break the deadlock. Unearth is
            // the only kind (Q1 ruling, 2026-07-15) and card 230 Gear Cache is the only collectible
            // that raises it. Kept in step with Kingdom.Net.Server.Bots.GreedyAi.ChooseUnearth.
            var pending = state.PendingChoice;
            if (pending != null && pending.Chooser == _playerId)
            {
                int chosen = ChooseUnearth(pending, state.PlayerOf(_playerId));
                if (chosen != 0) return new UnearthChoiceIntent(_playerId, chosen, pending.ChoiceSequence);
            }

            // Mulligan: keep entire opening hand.
            if (state.Phase == TurnPhase.Mulligan)
            {
                int mulSeat = state.SeatOf(_playerId);
                bool done = mulSeat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
                return done ? null : new MulliganIntent(_playerId, Array.Empty<InstanceId>());
            }

            if (state.Phase != TurnPhase.Main || state.ActivePlayer != _playerId)
                return null;

            int seat    = state.SeatOf(_playerId);
            int foeSeat = seat == 0 ? 1 : 0;
            var ps      = state.Players[seat];
            var foe     = state.Players[foeSeat];

            // 1. First card in hand we can legally play, aimed if it needs aiming.
            foreach (var card in ps.Hand)
            {
                var def = _db.Get(card.DefinitionId);
                if (ps.Mana.Available < def.Cost) continue;

                var play = TryPlay(state, card, def, ps, foe);
                if (play != null) return play;
            }

            // 2. Attack. Minions trade first — the validator, not this loop, enforces bulwark and
            //    shroud — then the hero swings if a weapon is equipped.
            foreach (var attacker in ps.Board)
            {
                var atk = TryAttack(state, attacker.Id, foe, preferFace: false);
                if (atk != null) return atk;
            }

            if (ps.Weapon != null)
            {
                // Face first for the hero: swinging into a minion means taking its attack back, and
                // the weapon spends durability either way. Bulwark still forces the trade.
                var swing = TryAttack(state, ps.Hero.Id, foe, preferFace: true);
                if (swing != null) return swing;
            }

            // 3. Hero power with whatever mana is left.
            var power = new HeroPowerIntent(_playerId, Array.Empty<TargetRef>());
            if (_validator.CanExecute(state, power, out _)) return power;

            foreach (var t in HeroPowerCandidates(ps, foe))
            {
                var aimed = new HeroPowerIntent(_playerId, new[] { t });
                if (_validator.CanExecute(state, aimed, out _)) return aimed;
            }

            return new EndTurnIntent(_playerId);
        }

        /// <summary>
        /// Which Unearth option to take: the dearest one this seat could still afford to cast at its
        /// current crystal ceiling, else the cheapest on offer. Ties break on the lower card id so the
        /// pick is reproducible. Returns 0 when nothing offered resolves against the database — the
        /// caller then leaves the choice alone rather than submitting an InvalidChoice.
        /// </summary>
        private int ChooseUnearth(PendingChoice pending, PlayerState chooser)
        {
            int best = 0, bestCost = -1;
            int cheapest = 0, cheapestCost = int.MaxValue;
            int ceiling = chooser.Mana.Max;

            foreach (int cardId in pending.Options)
            {
                if (!_db.TryGet(cardId, out var def)) continue;

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

        private PlayCardIntent TryPlay(GameState state, CardInstance card, CardDefinition def,
                                       PlayerState ps, PlayerState foe)
        {
            int slot = ps.Board.Count;

            // Untargeted first. Covers minions, weapons, draw, and every effect whose selector the
            // interpreter auto-resolves (ANY_ENEMY_MINION, ENEMY_HERO, SELF). Only the explicit-choice
            // ANY_* selectors fall through — exactly the set that needs a target supplied.
            var bare = new PlayCardIntent(_playerId, card.Id, slot, Array.Empty<TargetRef>());
            if (_validator.CanExecute(state, bare, out _)) return bare;

            // Build the binding-aware choice plan. Spells activate on ON_CAST; all other types on ON_PLAY.
            Trigger playTrigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            IReadOnlyList<TargetChoiceRequirement> plan =
                Targeting.PlayerChoicePlan(state, _db, _playerId, def, playTrigger);

            if (plan.Count == 0) return null;

            // Single unnamed binding needing ONE pick: the heuristic-ordered candidate loop preserves
            // the hostile/friendly priority (killable-first) for single-target cards. A group wanting
            // more than one pick falls through to the combination search below.
            if (plan.Count == 1 && plan[0].Binding.Length == 0 && plan[0].RequiredCount <= 1)
            {
                foreach (var t in PlayCandidates(def, ps, foe))
                {
                    var aimed = new PlayCardIntent(_playerId, card.Id, slot, new[] { t });
                    if (_validator.CanExecute(state, aimed, out _)) return aimed;
                }
                return null;
            }

            // Everything else: one candidate list per group tagged with the binding name, searched
            // depth-first for the first combination the validator accepts.
            var perGroup = new List<List<TargetRef>>(plan.Count);
            var perGroupCount = new List<int>(plan.Count);
            foreach (TargetChoiceRequirement req in plan)
            {
                var refs = new List<TargetRef>(req.Candidates.Count);
                foreach (TargetCandidate c in req.Candidates)
                    refs.Add(new TargetRef(c.Instance, req.Binding));

                // RequiredCount can exceed what the board offers, and carries int.MaxValue as the
                // conflicting-binding sentinel (Targeting.PlayerChoicePlan) — clamp to what exists.
                int take = req.RequiredCount < refs.Count ? req.RequiredCount : refs.Count;
                if (take < req.MinimumCount) return null;
                perGroup.Add(refs);
                perGroupCount.Add(take);
            }

            var chosen = new List<TargetRef>(plan.Count);
            return TryGroups(state, card, slot, perGroup, perGroupCount, chosen, 0);
        }

        /// <summary>
        /// Depth-first over the binding groups, taking each group's required number of DISTINCT
        /// candidates in canonical order.
        ///
        /// <para><b>The count is the whole point of this method.</b> It replaced a version that
        /// supplied exactly one ref per group, which left every card asking for two picks — 203
        /// Channeled Pyroclasm's "Deal 6 damage to 2 enemies" among them — permanently unplayable by
        /// this driver, with no rejection to see because the intent was never submitted.</para>
        ///
        /// <para>Kept in step with Kingdom.Net.Server.Bots.GreedyAi.TryGroups.</para>
        /// </summary>
        private PlayCardIntent TryGroups(GameState state, CardInstance card, int slot,
            List<List<TargetRef>> perGroup, List<int> perGroupCount, List<TargetRef> chosen, int depth)
        {
            if (depth == perGroup.Count)
            {
                var intent = new PlayCardIntent(_playerId, card.Id, slot, chosen.ToArray());
                return _validator.CanExecute(state, intent, out _) ? intent : null;
            }
            return TryCombination(state, card, slot, perGroup, perGroupCount, chosen,
                                  depth, 0, perGroupCount[depth]);
        }

        /// <summary>One group's distinct-combination walk: take <paramref name="remaining"/> more
        /// candidates from index <paramref name="from"/> onward, then move to the next group.</summary>
        private PlayCardIntent TryCombination(GameState state, CardInstance card, int slot,
            List<List<TargetRef>> perGroup, List<int> perGroupCount, List<TargetRef> chosen,
            int depth, int from, int remaining)
        {
            if (remaining == 0)
                return TryGroups(state, card, slot, perGroup, perGroupCount, chosen, depth + 1);

            List<TargetRef> candidates = perGroup[depth];
            for (int i = from; i <= candidates.Count - remaining; i++)
            {
                chosen.Add(candidates[i]);
                PlayCardIntent result = TryCombination(state, card, slot, perGroup, perGroupCount,
                                                       chosen, depth, i + 1, remaining - 1);
                if (result != null) return result;
                chosen.RemoveAt(chosen.Count - 1);
            }
            return null;
        }

        /// <summary>
        /// Ordered target candidates for an explicit-choice card. Hostile cards want enemies, the rest
        /// want friends; either way the tail covers the other side so a legal play is never skipped
        /// for want of a guess. Within each group the board is walked in slot order — killable-first
        /// is the whole heuristic, and it is the one that matters.
        /// </summary>
        private static List<TargetRef> PlayCandidates(CardDefinition def, PlayerState ps, PlayerState foe)
        {
            var list = new List<TargetRef>();

            if (IsHostile(def))
            {
                int dmg = DamageAmount(def);
                foreach (var m in foe.Board)
                    if (dmg > 0 && m.CurrentHealth <= dmg) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(foe.Hero.Id));
                foreach (var m in foe.Board) list.Add(new TargetRef(m.Id));
                foreach (var m in ps.Board) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(ps.Hero.Id));
            }
            else
            {
                if (ps.Hero.CurrentHealth < ps.Hero.MaxHealth) list.Add(new TargetRef(ps.Hero.Id));
                foreach (var m in ps.Board) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(ps.Hero.Id));
                foreach (var m in foe.Board) list.Add(new TargetRef(m.Id));
                list.Add(new TargetRef(foe.Hero.Id));
            }

            return list;
        }

        // ── Attacking ─────────────────────────────────────────────────────────────

        private AttackIntent TryAttack(GameState state, InstanceId attacker, PlayerState foe, bool preferFace)
        {
            if (preferFace)
            {
                var face = new AttackIntent(_playerId, attacker, new TargetRef(foe.Hero.Id));
                if (_validator.CanExecute(state, face, out _)) return face;
            }

            foreach (var defender in foe.Board)
            {
                var trade = new AttackIntent(_playerId, attacker, new TargetRef(defender.Id));
                if (_validator.CanExecute(state, trade, out _)) return trade;
            }

            if (!preferFace)
            {
                var face = new AttackIntent(_playerId, attacker, new TargetRef(foe.Hero.Id));
                if (_validator.CanExecute(state, face, out _)) return face;
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
            foreach (var m in foe.Board) list.Add(new TargetRef(m.Id));
            list.Add(new TargetRef(ps.Hero.Id));
            foreach (var m in ps.Board) list.Add(new TargetRef(m.Id));
            return list;
        }

        // ── Reading the card ──────────────────────────────────────────────────────

        private static bool IsHostile(CardDefinition def)
        {
            foreach (var rule in def.Rules)
            {
                switch (rule.Action)
                {
                    case DamageAction _:
                    case DestroyAction _:
                    // The negated form thaws, which is a favour, not an attack.
                    case FrostAction frost when !frost.Negate:
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
            foreach (var rule in def.Rules)
                if (rule.Action is DamageAction damage)
                    return damage.Amount;
            return 0;
        }
    }
}
