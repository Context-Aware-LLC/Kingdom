using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// Shared, stateless rules primitives used by BOTH the turn-loop command processor and the effect
    /// interpreter (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §4 post-mutation checks, §7 mana/fatigue). Keeping draw,
    /// board-entry, reindexing and the win check here means a deathrattle that draws or a battlecry that
    /// kills uses the EXACT same code path as the turn loop — no second implementation to drift (R1).
    /// </summary>
    internal static class RulesEngine
    {
        /// <summary>
        /// Draw one card for <paramref name="seat"/> off the top of the deck — with escalating fatigue on an
        /// empty deck and overdraw burn at the hand cap (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §4 CapCheck, §7 fatigue).
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
            // Stats, keywords, Aegis and freeze all come off the printed form (EF-017a). A card entering
            // the board arrives whole, so no damage is carried in from whatever it was doing before.
            DerivedStats.ResetToPrinted(ci, def, preserveDamage: false);
            ci.SummonedThisTurn = true;     // summoning sickness; CHARGE/RUSH override in combat (step 2)
            ci.AttacksUsedThisTurn = 0;
            ci.HasUsedReborn = false;

            int idx = (atIndex < 0 || atIndex > ps.Board.Count) ? ps.Board.Count : atIndex;
            ps.Board.Insert(idx, ci);
            Reindex(ps.Board);
            return true;
        }

        /// <summary>
        /// Move <paramref name="card"/> into the weapon slot as the same runtime instance. The caller
        /// breaks any outgoing weapon first so its queued trigger observes the incoming card already
        /// equipped when the queue later drains.
        /// </summary>
        public static void EnterWeapon(GameState state, CardInstance card, CardDefinition def, int seat)
        {
            var ps = state.Players[seat];
            card.Controller = ps.Id;
            card.Zone = Zone.Weapon;
            DerivedStats.EnterWeapon(card, def);
            ps.Weapon = card;
        }

        /// <summary>Re-stamp every minion's <see cref="CardInstance.Position"/> to its slot index after a board edit.</summary>
        public static void Reindex(List<CardInstance> board)
        {
            for (int i = 0; i < board.Count; i++)
                board[i].Position = i;
        }

        /// <summary>
        /// Destroy the equipped weapon for <paramref name="seat"/>, emitting onDeath and raising the weapon's
        /// ON_DEATH deathrattle onto <paramref name="interp"/>'s queue (EF-020). No-op if no weapon. Called
        /// before equipping a new weapon and by combat when durability reaches zero.
        ///
        /// <para>Takes the interpreter rather than (state, buf) because a weapon deathrattle has to enqueue,
        /// and every caller already holds one — same shape as <see cref="DamagePipeline.ApplyDamage"/>. The
        /// deathrattle ENQUEUES, it does not resolve here: it drains with everything else, so on an equip
        /// replacement the new weapon is already in the slot by the time the old one's rattle runs. That
        /// mirrors minion deathrattles (a board has already moved on when theirs resolve) and keeps the
        /// resolution order the single deterministic one the golden-replay gate pins.</para>
        /// </summary>
        public static void BreakWeapon(EffectInterpreter interp, int seat)
        {
            var ps = interp.State.Players[seat];
            if (ps.Weapon is null) return;

            var broken = ps.Weapon;
            interp.Events.Emit(VfxEvent.onDeath, source: broken.Id);
            ps.Weapon = null;   // leave the slot BEFORE raising, so the rattle observes an unequipped hero
            broken.Zone = Zone.Graveyard;
            ps.Graveyard.Add(broken);

            interp.RaiseSelf(broken, Trigger.ON_DEATH, System.Array.Empty<TargetRef>());
        }

        /// <summary>
        /// WinCheck (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §4): a hero at ≤0 health ends the match; both at once is a Draw.
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
