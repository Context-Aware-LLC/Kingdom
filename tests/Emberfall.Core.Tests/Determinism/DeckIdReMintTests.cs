using System;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Determinism
{
    /// <summary>
    /// OBL-7 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/multiplayer/state-sync-and-security.md §5.2.1, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a3-kickoff-review-2026-07-15.md Q1): deck-resident
    /// <see cref="InstanceId"/>s are re-minted in deck order after EVERY shuffle, so an id on the wire
    /// can only encode current post-shuffle deck position — never the decklist ordinal (which is
    /// cost-sorted and would bracket card identity). Visible-zone cards are never re-minted.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class DeckIdReMintTests
    {
        private const int DeckSize = 12;

        private static (LocalMatchHost host, GameState state) NewMatch(ulong seed)
        {
            var db = new FakeCardDatabase();
            var processor = new CommandProcessor(db);
            var setup = new MatchSetup(
                CardClass.EMBERWARD, FakeCardDatabase.Deck("ember", DeckSize),
                CardClass.TIDECALLER, FakeCardDatabase.Deck("tide", DeckSize));
            var host = new LocalMatchHost(processor, db, setup);
            var state = host.BeginMatch(new MatchId("M"), new PlayerId("P0"), new PlayerId("P1"), seed);
            return (host, state);
        }

        private static void AssertDeckIdsAscending(PlayerState ps, string because)
        {
            for (int i = 1; i < ps.Deck.Count; i++)
                Assert.That(ps.Deck[i].Id.Value, Is.GreaterThan(ps.Deck[i - 1].Id.Value), because);
        }

        [Test]
        public void BeginMatch_No_Card_Keeps_A_Decklist_Ordinal_Id()
        {
            var (_, s) = NewMatch(seed: 0xD00DUL);

            // BuildDeck mints 2 hero ids then one id per deck slot in decklist order; the post-shuffle
            // re-mint must retire ALL of those, so every card id (any zone) sits above the build range.
            int buildRangeEnd = 2 + DeckSize * 2;   // first id minted by the re-mint pass
            foreach (var ps in s.Players)
            {
                foreach (var c in ps.Hand)
                    Assert.That(c.Id.Value, Is.GreaterThanOrEqualTo(buildRangeEnd),
                        "A dealt card carrying a decklist-ordinal id leaks identity (OBL-7).");
                foreach (var c in ps.Deck)
                    Assert.That(c.Id.Value, Is.GreaterThanOrEqualTo(buildRangeEnd),
                        "A deck card carrying a decklist-ordinal id leaks identity (OBL-7).");
                AssertDeckIdsAscending(ps, "Post-shuffle deck ids must equal current deck order.");
            }
        }

        [Test]
        public void Mulligan_Reshuffle_ReMints_Deck_But_Never_Hand()
        {
            var (host, s) = NewMatch(seed: 0xBEEFUL);
            int seat = s.SeatOf(s.ActivePlayer);
            var ps = s.Players[seat];

            var keptHandIds = new List<int>();
            for (int i = 1; i < ps.Hand.Count; i++) keptHandIds.Add(ps.Hand[i].Id.Value);
            int maxIdBefore = s.NextInstanceCounter - 1;

            host.Submit(new MulliganIntent(ps.Id, new List<InstanceId> { ps.Hand[0].Id }));

            foreach (var c in ps.Deck)
                Assert.That(c.Id.Value, Is.GreaterThan(maxIdBefore),
                    "The mulligan reshuffle must re-mint every deck-resident id (mint-once misses this).");
            AssertDeckIdsAscending(ps, "Post-mulligan deck ids must equal current deck order.");

            for (int i = 0; i < keptHandIds.Count; i++)
                Assert.That(ps.Hand[i].Id.Value, Is.EqualTo(keptHandIds[i]),
                    "Visible-zone cards are never re-minted (stable ids for presentation/audit).");
        }

        [Test]
        public void Keep_All_Mulligan_Does_Not_Shuffle_Or_ReMint()
        {
            var (host, s) = NewMatch(seed: 0xCAFEUL);
            int seat = s.SeatOf(s.ActivePlayer);
            var ps = s.Players[seat];

            var deckIdsBefore = new List<int>();
            foreach (var c in ps.Deck) deckIdsBefore.Add(c.Id.Value);

            host.Submit(new MulliganIntent(ps.Id, Array.Empty<InstanceId>()));

            for (int i = 0; i < deckIdsBefore.Count; i++)
                Assert.That(ps.Deck[i].Id.Value, Is.EqualTo(deckIdsBefore[i]),
                    "An empty mulligan keeps all cards, consumes no RNG, and re-mints nothing.");
        }
    }
}
