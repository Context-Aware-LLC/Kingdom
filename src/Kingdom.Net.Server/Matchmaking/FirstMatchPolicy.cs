using Kingdom.Net.Server.Bots;

namespace Kingdom.Net.Server.Matchmaking
{
    /// <summary>
    /// Who a newcomer plays (launch item 5, Sam 2026-10-26): their first match against Greedy
    /// (<c>greedy-ai.v3</c>), their second against H1 (checkpoint <c>978b89297989</c>), and from
    /// the third on, ordinary matchmaking. Never the moving champion or S1 for the second.
    ///
    /// <para>"Completed" is a match that reached a result, conceded and timed-out ones included:
    /// see <see cref="CompletedMatchLedger"/>. A match that never started, or a server that died
    /// mid-match, records nothing, so the player is owed the same bot again.</para>
    /// </summary>
    public static class FirstMatchPolicy
    {
        /// <summary>How many matches a newcomer plays against a fixed bot before the queue.</summary>
        public const int BotOnlyMatches = 2;

        /// <summary>The bot an account with this many completed matches is owed, or null for the
        /// ordinary queue.</summary>
        public static BotOpponent? For(int completedMatches) => completedMatches switch
        {
            <= 0 => BotOpponent.Greedy,
            1 => BotOpponent.SecondMatch,
            _ => null,
        };
    }
}
