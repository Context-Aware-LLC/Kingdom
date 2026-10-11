using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Core
{
    /// <summary>
    /// The two fixed prebuilt decks + hero classes for an M1 slice match (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M1-KICKOFF.md §3 Phase D:
    /// "2 fixed prebuilt decks"). Deckbuilding is out of M1 scope, so the host is constructed with the
    /// decklists; the <see cref="IMatchHost.BeginMatch"/> contract still owns the seed and all RNG.
    /// Each decklist is an ordered list of card definition ids; order is the pre-shuffle order, so it
    /// is part of the deterministic input.
    /// </summary>
    public sealed class MatchSetup
    {
        public CardClass P0Class { get; }
        public CardClass P1Class { get; }
        public IReadOnlyList<int> P0Deck { get; }
        public IReadOnlyList<int> P1Deck { get; }
        public int HeroMaxHealth { get; }

        /// <summary>The free catch-up card inserted into the second player's hand (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §2.1).</summary>
        public int SparkTokenId { get; }

        public MatchSetup(
            CardClass p0Class, IReadOnlyList<int> p0Deck,
            CardClass p1Class, IReadOnlyList<int> p1Deck,
            int heroMaxHealth = 30, int sparkTokenId = 158)   // token_spark_coin
        {
            P0Class = p0Class;
            P1Class = p1Class;
            P0Deck = p0Deck;
            P1Deck = p1Deck;
            HeroMaxHealth = heroMaxHealth;
            SparkTokenId = sparkTokenId;
        }
    }
}
