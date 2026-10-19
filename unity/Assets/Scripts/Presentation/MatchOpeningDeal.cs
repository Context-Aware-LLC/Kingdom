#nullable enable
using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
{
    /// <summary>The public result of the server's opening toss, including a redacted opponent
    /// hand. Never rolls RNG, creates a card, or changes a snapshot.</summary>
    internal sealed class MatchOpeningDeal
    {
        public const int SparkCardId = 158; // stable token_spark_coin id
        public PlayerId SecondPlayer { get; }
        public int HandSlot { get; }

        private MatchOpeningDeal(PlayerId secondPlayer, int handSlot)
        {
            SecondPlayer = secondPlayer;
            HandSlot = handSlot;
        }

        public static bool IsPending(GameState? state, PlayerId local)
        {
            if (state == null || state.Phase != TurnPhase.Mulligan || state.TurnNumber != 0
                || state.Outcome != MatchOutcome.InProgress) return false;
            if (state.Players[0].Id == local) return !state.Mulligan0Done;
            if (state.Players[1].Id == local) return !state.Mulligan1Done;
            return false;
        }

        public static MatchOpeningDeal? From(GameState? state, PlayerId local)
        {
            if (state == null || !IsPending(state, local)) return null;
            int first = state.ActivePlayer == state.Players[0].Id ? 0
                : state.ActivePlayer == state.Players[1].Id ? 1 : -1;
            if (first < 0) return null;
            PlayerState second = state.Players[1 - first];
            if (second.Hand.Count == 0) return null;
            // The core appends the protected Spark after the opening deal. Opponent identities
            // stay redacted; the public toss tells us who receives it without inspecting them.
            int slot = second.Hand.Count - 1;
            if (second.Id == local)
            {
                slot = second.Hand.FindIndex(card => card.DefinitionId == SparkCardId);
                if (slot < 0) return null;
            }
            return new MatchOpeningDeal(second.Id, slot);
        }
    }
}
