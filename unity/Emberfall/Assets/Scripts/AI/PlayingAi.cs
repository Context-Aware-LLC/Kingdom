using System;
using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.AI
{
    /// <summary>
    /// Greedy AI for the vertical slice. Each turn: play the first affordable card (aiming it if it
    /// needs aiming), trade with each ready minion, swing the hero if a weapon is equipped, use the
    /// hero power, then end the turn. Every intent is gated through <see cref="ICommandValidator"/>
    /// before it is queued, so rejections are near-impossible.
    ///
    /// <para><b>Kept in step with <c>Emberfall.Tools.NetSoak.GreedyAi</c>.</b> That is the driver
    /// that grinds the `ai_soak` balance sample, and a balance bar only means something if the AI
    /// being measured is the AI that ships. Fix one, fix the other.</para>
    ///
    /// <para><b>2026-07-22 — three holes closed.</b> This could not target, could not swing a weapon,
    /// and never used the hero power. That reads as mild weakness until you notice it lands
    /// asymmetrically: EMBERWARD's removal is <c>ANY_CHARACTER</c> (ember_flame_bolt, ember_scorch) and its
    /// weapon is ember_cinder_lance, while TIDECALLER's tide_frost_ripple auto-resolves and deep_bargain
    /// just draws. Six of EMBERWARD's twenty cards were unplayable and none of TIDECALLER's were —
    /// found when the soak reported a 72.5/27.5 split that turned out to be this bug, not the cards.
    /// In the shipped single-player game it meant the AI played EMBERWARD with a third of its deck
    /// dead.</para>
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

        // ── Playing a card ────────────────────────────────────────────────────────

        private PlayCardIntent TryPlay(GameState state, CardInstance card, CardDefinition def,
                                       PlayerState ps, PlayerState foe)
        {
            int slot = ps.Board.Count;

            // Untargeted first. Covers minions, weapons, draw, and every effect whose selector the
            // interpreter auto-resolves (ENEMY_MINIONS, ENEMY_HERO, SELF). Only the explicit-choice
            // ANY_* selectors fall through — exactly the set that needs a target supplied.
            var bare = new PlayCardIntent(_playerId, card.Id, slot, Array.Empty<TargetRef>());
            if (_validator.CanExecute(state, bare, out _)) return bare;

            // Build the binding-aware choice plan. Spells activate on ON_CAST; all other types on ON_PLAY.
            Trigger playTrigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            IReadOnlyList<TargetChoiceRequirement> plan =
                Targeting.PlayerChoicePlan(state, _db, _playerId, def, playTrigger);

            if (plan.Count == 0) return null;

            // Single unnamed binding: the existing heuristic-ordered candidate loop preserves the
            // hostile/friendly priority (killable-first) for single-target cards.
            if (plan.Count == 1 && plan[0].Binding.Length == 0)
            {
                foreach (var t in PlayCandidates(def, ps, foe))
                {
                    var aimed = new PlayCardIntent(_playerId, card.Id, slot, new[] { t });
                    if (_validator.CanExecute(state, aimed, out _)) return aimed;
                }
                return null;
            }

            // Named or multi-binding: build one candidate list per group tagged with the binding name,
            // then try combinations depth-first. The validator is the truth about legality.
            var perGroup = new List<List<TargetRef>>(plan.Count);
            foreach (TargetChoiceRequirement req in plan)
            {
                if (req.Candidates.Count == 0) return null;
                var refs = new List<TargetRef>(req.Candidates.Count);
                foreach (TargetCandidate c in req.Candidates)
                    refs.Add(new TargetRef(c.Instance, req.Binding));
                perGroup.Add(refs);
            }

            var chosen = new TargetRef[plan.Count];
            return TryCombinations(state, card, slot, perGroup, chosen, 0);
        }

        private PlayCardIntent TryCombinations(GameState state, CardInstance card, int slot,
            List<List<TargetRef>> perGroup, TargetRef[] chosen, int depth)
        {
            if (depth == perGroup.Count)
            {
                var intent = new PlayCardIntent(_playerId, card.Id, slot, (TargetRef[])chosen.Clone());
                return _validator.CanExecute(state, intent, out _) ? intent : null;
            }
            foreach (TargetRef t in perGroup[depth])
            {
                chosen[depth] = t;
                PlayCardIntent result = TryCombinations(state, card, slot, perGroup, chosen, depth + 1);
                if (result != null) return result;
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
            foreach (var rule in def.Rules)
                if (rule.Action is DamageAction damage)
                    return damage.Amount;
            return 0;
        }
    }
}
