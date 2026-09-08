using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// Shared, stateless rules primitives used by BOTH the turn-loop command processor and the effect
    /// interpreter (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §4 post-mutation checks, §7 mana/fatigue). Keeping draw,
    /// board-entry, reindexing and the win check here means a toll that draws or a herald that
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
                buf.Emit(GameEventKind.FatigueDamage, target: ps.Hero.Id, amount: counter, flags: EventFlags.Fatigue);
                return;
            }

            var card = ps.Deck[0];
            ps.Deck.RemoveAt(0);

            if (ps.Hand.Count >= MatchEngine.HandCap)
            {
                EnterGraveyard(state, ps, card);
                buf.Emit(GameEventKind.CardDrawn, source: card.Id, flags: EventFlags.Overdraw);
            }
            else
            {
                card.Zone = Zone.Hand;
                ps.Hand.Add(card);
                buf.Emit(GameEventKind.CardDrawn, source: card.Id);
            }
        }

        /// <summary>
        /// Move an existing card instance onto <paramref name="seat"/>'s board at <paramref name="atIndex"/>
        /// (clamped; -1 = append), initializing its live stats and keyword set from the definition. Returns
        /// false and does nothing if the board is at <see cref="MatchEngine.BoardCap"/> (CapCheck fizzle).
        /// The caller emits the entry fact (<see cref="GameEventKind.CardPlayed"/> for a hand-play,
        /// <see cref="GameEventKind.CardSummoned"/> for a token).
        /// </summary>
        public static bool EnterBoard(GameState state, CardInstance ci, CardDefinition def, int seat, int atIndex,
            ICardDatabase db)
        {
            var ps = state.Players[seat];
            if (ps.Board.Count >= MatchEngine.BoardCap) return false;

            ci.Controller = ps.Id;
            ci.Zone = Zone.Board;
            // Stats, keywords, Aegis and frost all come off the printed form (EF-017a). A card entering
            // the board arrives whole, so no damage is carried in from whatever it was doing before.
            DerivedStats.ResetToPrinted(ci, def, preserveDamage: false);
            ci.SummonedThisTurn = true;     // summoning sickness; FERVOR/SKIRMISH override in combat (step 2)
            ci.AttacksUsedThisTurn = 0;
            ci.HasUsedRekindle = false;

            int idx = (atIndex < 0 || atIndex > ps.Board.Count) ? ps.Board.Count : atIndex;
            ps.Board.Insert(idx, ci);
            Reindex(ps.Board);
            AuraEngine.Recompute(state, db);
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
        /// The one door into a graveyard: sets the zone, stamps
        /// <see cref="CardInstance.DiedAtSequence"/> off the match-wide clock
        /// (<see cref="GameState.NextDeathSequence"/>), then appends. Every other write site used to
        /// do these two lines itself, which is how a shared death clock across two independently
        /// list-ordered graveyards would have silently gone missing the next time a new one appeared.
        /// </summary>
        public static void EnterGraveyard(GameState state, PlayerState owner, CardInstance card)
        {
            card.Zone = Zone.Graveyard;
            card.DiedAtSequence = state.NextDeathSequence++;
            owner.Graveyard.Add(card);
        }

        /// <summary>
        /// Destroy the equipped weapon for <paramref name="seat"/>, emitting
        /// <see cref="GameEventKind.CardDestroyed"/> and raising the weapon's
        /// ON_DEATH toll onto <paramref name="interp"/>'s queue (EF-020). No-op if no weapon. Called
        /// before equipping a new weapon and by combat when durability reaches zero.
        ///
        /// <para>Takes the interpreter rather than (state, buf) because a weapon toll has to enqueue,
        /// and every caller already holds one — same shape as <see cref="DamagePipeline.ApplyDamage"/>. The
        /// toll ENQUEUES, it does not resolve here: it drains with everything else, so on an equip
        /// replacement the new weapon is already in the slot by the time the old one's rattle runs. That
        /// mirrors minion tolls (a board has already moved on when theirs resolve) and keeps the
        /// resolution order the single deterministic one the golden-replay gate pins.</para>
        /// </summary>
        public static void BreakWeapon(EffectInterpreter interp, int seat)
        {
            var ps = interp.State.Players[seat];
            if (ps.Weapon is null) return;

            var broken = ps.Weapon;
            interp.Events.Emit(GameEventKind.CardDestroyed, source: broken.Id);
            ps.Weapon = null;   // leave the slot BEFORE raising, so the rattle observes an unequipped hero
            EnterGraveyard(interp.State, ps, broken);

            interp.RaiseSelf(broken, Trigger.ON_DEATH, System.Array.Empty<TargetRef>());
        }

        /// <summary>
        /// A hero at ≤0 health ends the match. If both heroes die in the same resolution,
        /// the hero with the lower final health loses; equal health is a draw.
        /// Idempotent — does nothing once an outcome is set.
        /// </summary>
        public static void CheckWin(GameState state, EventBuffer buf)
        {
            if (state.Outcome != MatchOutcome.InProgress) return;

            bool dead0 = state.Players[0].Hero.CurrentHealth <= 0;
            bool dead1 = state.Players[1].Hero.CurrentHealth <= 0;
            if (!dead0 && !dead1) return;

            if (dead0 && dead1)
            {
                int health0 = state.Players[0].Hero.CurrentHealth;
                int health1 = state.Players[1].Hero.CurrentHealth;
                if (health0 == health1) state.Outcome = MatchOutcome.Draw;
                else if (health0 > health1) state.Outcome = MatchOutcome.Player0Win;
                else state.Outcome = MatchOutcome.Player1Win;
            }
            else if (dead1) state.Outcome = MatchOutcome.Player0Win;
            else state.Outcome = MatchOutcome.Player1Win;

            state.Phase = TurnPhase.GameOver;
            buf.Emit(GameEventKind.MatchEnded);
            if (state.Outcome != MatchOutcome.Draw)
            {
                int winnerSeat = state.Outcome == MatchOutcome.Player0Win ? 0 : 1;
                buf.Emit(GameEventKind.MatchWon, source: state.Players[winnerSeat].Hero.Id, flags: EventFlags.Lethal);
            }
        }
    }
}
