using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// Shared, stateless rules primitives used by BOTH the turn-loop command processor and the effect
    /// interpreter (game-state-and-actions.md §4 post-mutation checks, §7 mana/fatigue). Keeping draw,
    /// board-entry, reindexing and the win check here means a deathrattle that draws or a battlecry that
    /// kills uses the EXACT same code path as the turn loop — no second implementation to drift (R1).
    /// </summary>
    internal static class RulesEngine
    {
        /// <summary>
        /// Draw one card for <paramref name="seat"/> off the top of the deck — with escalating fatigue on an
        /// empty deck and overdraw burn at the hand cap (game-state-and-actions.md §4 CapCheck, §7 fatigue).
        /// </summary>
        public static void DrawOne(GameState state, int seat, EventBuffer buf)
        {
            var ps = state.Players[seat];

            if (ps.Deck.Count == 0)
            {
                int counter = seat == 0 ? ++state.FatigueCounter0 : ++state.FatigueCounter1;
                ps.Hero.CurrentHealth -= counter;
                buf.Emit(VfxEvent.onFatigue, target: ps.Hero.Id, amount: counter, flags: EventFlags.Fatigue);
                return;
            }

            var card = ps.Deck[0];
            ps.Deck.RemoveAt(0);

            if (ps.Hand.Count >= MatchEngine.HandCap)
            {
                card.Zone = Zone.Graveyard;
                ps.Graveyard.Add(card);
                buf.Emit(VfxEvent.onDraw, source: card.Id, flags: EventFlags.Overdraw);
            }
            else
            {
                card.Zone = Zone.Hand;
                ps.Hand.Add(card);
                buf.Emit(VfxEvent.onDraw, source: card.Id);
            }
        }

        /// <summary>
        /// Move an existing card instance onto <paramref name="seat"/>'s board at <paramref name="atIndex"/>
        /// (clamped; -1 = append), initializing its live stats and keyword set from the definition. Returns
        /// false and does nothing if the board is at <see cref="MatchEngine.BoardCap"/> (CapCheck fizzle).
        /// The caller emits the entry VFX (<c>onPlay</c> for a hand-play, <c>onSummon</c> for a token).
        /// </summary>
        public static bool EnterBoard(GameState state, CardInstance ci, CardDefinition def, int seat, int atIndex)
        {
            var ps = state.Players[seat];
            if (ps.Board.Count >= MatchEngine.BoardCap) return false;

            ci.Controller = ps.Id;
            ci.Zone = Zone.Board;
            ci.CurrentAttack = def.Attack ?? 0;
            ci.CurrentHealth = def.Health ?? 1;
            ci.MaxHealth = def.Health ?? 1;
            ci.Enchantments.Clear();
            ci.Keywords.Clear();
            foreach (var k in def.Keywords) ci.Keywords.Add(k);
            ci.HasAegisShield = ci.Keywords.Contains(Keyword.DIVINE_SHIELD);
            ci.IsFrozen = false;
            ci.SummonedThisTurn = true;     // summoning sickness; CHARGE/RUSH override in combat (step 2)
            ci.AttacksUsedThisTurn = 0;
            ci.HasUsedReborn = false;

            int idx = (atIndex < 0 || atIndex > ps.Board.Count) ? ps.Board.Count : atIndex;
            ps.Board.Insert(idx, ci);
            Reindex(ps.Board);
            return true;
        }

        /// <summary>Re-stamp every minion's <see cref="CardInstance.Position"/> to its slot index after a board edit.</summary>
        public static void Reindex(List<CardInstance> board)
        {
            for (int i = 0; i < board.Count; i++)
                board[i].Position = i;
        }

        /// <summary>
        /// Destroy the equipped weapon for <paramref name="seat"/>, emitting onDeath. No-op if no weapon.
        /// Called before equipping a new weapon and by combat when durability reaches zero.
        /// </summary>
        public static void BreakWeapon(GameState state, int seat, EventBuffer buf)
        {
            var ps = state.Players[seat];
            if (ps.Weapon is null) return;
            buf.Emit(VfxEvent.onDeath, source: ps.Weapon.Id);
            ps.Weapon = null;
        }

        /// <summary>
        /// WinCheck (game-state-and-actions.md §4): a hero at ≤0 health ends the match; both at once is a Draw.
        /// Idempotent — does nothing once an outcome is set.
        /// </summary>
        public static void CheckWin(GameState state, EventBuffer buf)
        {
            if (state.Outcome != MatchOutcome.InProgress) return;

            bool dead0 = state.Players[0].Hero.CurrentHealth <= 0;
            bool dead1 = state.Players[1].Hero.CurrentHealth <= 0;
            if (!dead0 && !dead1) return;

            if (dead0 && dead1) state.Outcome = MatchOutcome.Draw;
            else if (dead1) state.Outcome = MatchOutcome.Player0Win;
            else state.Outcome = MatchOutcome.Player1Win;

            state.Phase = TurnPhase.GameOver;
            buf.Emit(VfxEvent.onGameOver);
            if (state.Outcome != MatchOutcome.Draw)
            {
                int winnerSeat = state.Outcome == MatchOutcome.Player0Win ? 0 : 1;
                buf.Emit(VfxEvent.onGameWin, source: state.Players[winnerSeat].Hero.Id, flags: EventFlags.Lethal);
            }
        }
    }
}
