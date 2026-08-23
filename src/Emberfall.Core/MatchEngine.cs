using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// Core-driven match-start sequence (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §2.1). The coin flip, opening deal, "The Spark"
    /// catch-up card and mulligan entry are <b>rules</b> owned entirely by the core; the host only
    /// triggers them. Every random decision draws from the single seeded <see cref="DeterministicRandom"/>
    /// in a fixed call order, so the same seed produces the identical opening on any machine — the
    /// RNG-dense moment the <c>opening_pyra_maren</c> golden fixture covers (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §7).
    /// </summary>
    public static class MatchEngine
    {
        public const int FirstPlayerHandSize = 3;
        public const int SecondPlayerHandSize = 4;
        // Forwarded, never restated: Emberfall.Core.Cards derives selector cardinality from the
        // same caps and cannot see this class. See ZoneCaps for why they live in Contracts.
        public const int HandCap = ZoneCaps.Hand;
        public const int BoardCap = ZoneCaps.Board;
        public const int MaxMana = 10;

        /// <summary>
        /// Loop backstop for the effect/trigger resolution queue per command (balance-guardrails §4,
        /// hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §4). A drain exceeding this halts the chain rather than hanging —
        /// a safety net against accidental infinite cascades, never a value any legitimate card reaches.
        /// </summary>
        public const int ActionResolutionCap = 100;

        /// <summary>
        /// Build the seeded, ready-to-mulligan <see cref="GameState"/>. RNG call order (fixed):
        /// (1) coin flip → first player; (2) shuffle P0 deck; (3) shuffle P1 deck; (4) deal hands;
        /// (5) insert the Spark into the second player. Then enter <see cref="TurnPhase.Mulligan"/>.
        /// </summary>
        public static GameState BeginMatch(MatchSetup setup, ICardDatabase db, MatchId id, PlayerId p0, PlayerId p1, ulong seed)
        {
            var rng = new DeterministicRandom(seed);

            var hero0 = new HeroInstance(default, p0, setup.P0Class, setup.HeroMaxHealth);
            var hero1 = new HeroInstance(default, p1, setup.P1Class, setup.HeroMaxHealth);
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);

            var state = new GameState(id, rng, ps0, ps1);

            // Hero instance ids first (deterministic creation order, seat order).
            hero0.Id = state.NextInstanceId();
            hero1.Id = state.NextInstanceId();

            hero0.HeroPowerId = HeroPowers.ForClass(setup.P0Class);
            hero1.HeroPowerId = HeroPowers.ForClass(setup.P1Class);

            // Build decks in listed order, assigning ids p0-deck then p1-deck.
            BuildDeck(state, ps0, setup.P0Deck);
            BuildDeck(state, ps1, setup.P1Deck);

            // (1) coin flip → first player.
            int firstSeat = rng.NextInt(2);
            state.ActivePlayer = state.Players[firstSeat].Id;

            // (2)(3) shuffle decks, seat order, AFTER the coin flip — a fixed RNG call order.
            // DeckOps.Shuffle re-mints deck ids in post-shuffle order (OBL-7): the BuildDeck ids
            // above are decklist ordinals and must never survive into a dealt/drawn card.
            DeckOps.Shuffle(state, ps0);
            DeckOps.Shuffle(state, ps1);

            // (4) deal opening hands: first player 3, second player 4 (GDD §1b).
            PlayerState first = state.Players[firstSeat];
            PlayerState second = state.Players[firstSeat == 0 ? 1 : 0];
            DealOpening(first, FirstPlayerHandSize);
            DealOpening(second, SecondPlayerHandSize);

            // (5) insert "The Spark" into the SECOND player's hand (NOT shuffled into the deck).
            var spark = new CardInstance(state.NextInstanceId(), setup.SparkTokenId, second.Id) { Zone = Zone.Hand };
            second.Hand.Add(spark);

            state.Phase = TurnPhase.Mulligan;
            state.TurnNumber = 0;
            return state;
        }

        private static void BuildDeck(GameState state, PlayerState ps, IReadOnlyList<string> deckList)
        {
            foreach (var cardId in deckList)
            {
                var ci = new CardInstance(state.NextInstanceId(), cardId, ps.Id) { Zone = Zone.Deck };
                ps.Deck.Add(ci);
            }
        }

        private static void DealOpening(PlayerState ps, int count)
        {
            for (int i = 0; i < count && ps.Deck.Count > 0; i++)
            {
                var card = ps.Deck[0];
                ps.Deck.RemoveAt(0);
                card.Zone = Zone.Hand;
                ps.Hand.Add(card);
            }
        }
    }
}
