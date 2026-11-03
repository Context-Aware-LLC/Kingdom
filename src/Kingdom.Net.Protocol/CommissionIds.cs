using System.Collections.Generic;

namespace Kingdom.Net.Protocol
{
    /// <summary>
    /// The King's commissions, by permanent id. An id keys a stored score and a Ledger page, so
    /// one is retired, never renamed or reused. Shared because the server picks the week's
    /// commission and the client words it.
    /// </summary>
    public static class CommissionIds
    {
        public const string Overkill = "overkill";
        public const string Technically = "technically";
        public const string SelfStarter = "self_starter";
        public const string WasteNot = "waste_not";
        public const string OutOfIdeas = "out_of_ideas";
        public const string TooManyIdeas = "too_many_ideas";
        public const string Posthumous = "posthumous";
        public const string ReturnToSender = "return_to_sender";
        public const string SignatureMove = "signature_move";
        public const string Comeback = "comeback";
        public const string TheLongGame = "the_long_game";
        public const string Overgrown = "overgrown";

        /// <summary>The commissions the first epoch rotates through, in rotation order. Reign 1
        /// reigned under it, so it never changes.</summary>
        public static readonly IReadOnlyList<string> Launch = new[]
        {
            Overkill, Technically, SelfStarter, WasteNot, OutOfIdeas,
            TooManyIdeas, Posthumous, ReturnToSender, SignatureMove,
        };

        /// <summary>The commissions the second epoch rotates through from Reign 2 (#443): the launch
        /// nine and Comeback, The Long Game and Overgrown, in the design's table order.</summary>
        public static readonly IReadOnlyList<string> FromReignTwo = new[]
        {
            Overkill, Technically, SelfStarter, Comeback, TheLongGame, WasteNot,
            OutOfIdeas, TooManyIdeas, Posthumous, ReturnToSender, SignatureMove, Overgrown,
        };

        /// <summary>Every commission any epoch names, each once. The client's words and art are
        /// checked against this list (<c>KingVerify</c>), because Unity cannot see the server's
        /// schedule; a server test holds every epoch to it.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Overkill, Technically, SelfStarter, WasteNot, OutOfIdeas, TooManyIdeas,
            Posthumous, ReturnToSender, SignatureMove, Comeback, TheLongGame, Overgrown,
        };

        /// <summary>Epithets in the first epoch's pool.</summary>
        public const int EpithetCount = 30;

        /// <summary>Fates in the first epoch's pool.</summary>
        public const int FateCount = 30;
    }
}
