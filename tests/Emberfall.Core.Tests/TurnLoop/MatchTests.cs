using System;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.GoldenReplay;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.TurnLoop
{
    /// <summary>
    /// L1 — match-start sequence + turn loop through the real <c>Submit → Execute</c> door
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §4). These are the headless siblings of the golden fixtures.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class MatchTests
    {
        private static (LocalMatchHost host, GameState state) NewMatch(ulong seed, int deckSize = 12)
        {
            var db = new FakeCardDatabase();
            var processor = new CommandProcessor(db);
            var setup = new MatchSetup(
                CardClass.EMBERWARD, FakeCardDatabase.Deck("ember", deckSize),
                CardClass.TIDECALLER, FakeCardDatabase.Deck("tide", deckSize));
            var host = new LocalMatchHost(processor, db, setup);
            var state = host.BeginMatch(new MatchId("M"), new PlayerId("P0"), new PlayerId("P1"), seed);
            return (host, state);
        }

        [Test]
        public void BeginMatch_Deals_3_And_4_Inserts_Spark_And_Enters_Mulligan()
        {
            var (_, s) = NewMatch(seed: 0xABCDEF12345UL);

            Assert.That(s.Phase, Is.EqualTo(TurnPhase.Mulligan));
            int firstSeat = s.SeatOf(s.ActivePlayer);
            int secondSeat = firstSeat == 0 ? 1 : 0;

            Assert.That(s.Players[firstSeat].Hand.Count, Is.EqualTo(3), "First player draws 3.");
            Assert.That(s.Players[secondSeat].Hand.Count, Is.EqualTo(5), "Second player draws 4 + The Spark.");

            int sparkCount = 0;
            foreach (var c in s.Players[secondSeat].Hand)
                if (c.DefinitionId == "token_spark_coin") sparkCount++;
            Assert.That(sparkCount, Is.EqualTo(1), "Exactly one Spark, in the SECOND player's hand.");

            foreach (var c in s.Players[firstSeat].Hand)
                Assert.That(c.DefinitionId, Is.Not.EqualTo("token_spark_coin"), "First player never gets the Spark.");
        }

        [Test]
        public void Same_Seed_And_Intents_Produce_Identical_State_Hash()
        {
            string Run(ulong seed)
            {
                var (host, s) = NewMatch(seed);
                host.Submit(new MulliganIntent(s.Players[0].Id, Array.Empty<InstanceId>()));
                host.Submit(new MulliganIntent(s.Players[1].Id, Array.Empty<InstanceId>()));
                host.Submit(new EndTurnIntent(s.ActivePlayer));
                host.Submit(new EndTurnIntent(s.ActivePlayer));
                return GameStateHasher.Hash(s);
            }
            string a = Run(555UL), b = Run(555UL);
            Assert.That(a, Is.EqualTo(b));
            string c = Run(556UL);
            Assert.That(a, Is.Not.EqualTo(c), "A different seed should (almost always) diverge.");
        }

        [Test]
        public void Mulligan_Redraw_Consumes_Rng_Deterministically()
        {
            // Replacing a card reshuffles the deck (RNG). Same seed + same replacement → identical hash.
            string Run(ulong seed)
            {
                var (host, s) = NewMatch(seed);
                int firstSeat = s.SeatOf(s.ActivePlayer);
                var toReplace = new List<InstanceId> { s.Players[firstSeat].Hand[0].Id };
                host.Submit(new MulliganIntent(s.Players[firstSeat].Id, toReplace));
                host.Submit(new MulliganIntent(s.Players[firstSeat == 0 ? 1 : 0].Id, Array.Empty<InstanceId>()));
                return GameStateHasher.Hash(s);
            }
            string a = Run(1234UL), b = Run(1234UL);
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void Illegal_Intent_Causes_Zero_Mutation_And_Zero_Events()
        {
            var (host, s) = NewMatch(seed: 1UL);
            ulong versionBefore = s.StateVersion;
            string hashBefore = GameStateHasher.Hash(s);

            // EndTurn is illegal during Mulligan.
            var result = host.Submit(new EndTurnIntent(s.ActivePlayer));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(RejectReason.WrongPhase));
            Assert.That(result.Events, Is.Empty);
            Assert.That(s.StateVersion, Is.EqualTo(versionBefore), "Rejected command must not bump the version.");
            Assert.That(GameStateHasher.Hash(s), Is.EqualTo(hashBefore), "Rejected command must not mutate state.");
        }

        [Test]
        public void Mana_Ramps_By_One_Per_Turn_Capped_At_Ten()
        {
            var (host, s) = NewMatch(seed: 2UL, deckSize: 40);
            host.Submit(new MulliganIntent(s.Players[0].Id, Array.Empty<InstanceId>()));
            host.Submit(new MulliganIntent(s.Players[1].Id, Array.Empty<InstanceId>()));

            var firstPlayer = s.ActivePlayer;
            int firstSeat = s.SeatOf(firstPlayer);
            Assert.That(s.Players[firstSeat].Mana.Max, Is.EqualTo(1), "First turn = 1 crystal.");

            // Drive 24 end-turns (12 each); both should cap at 10.
            for (int i = 0; i < 24; i++)
                host.Submit(new EndTurnIntent(s.ActivePlayer));

            Assert.That(s.Players[0].Mana.Max, Is.EqualTo(MatchEngine.MaxMana));
            Assert.That(s.Players[1].Mana.Max, Is.EqualTo(MatchEngine.MaxMana));
        }

        [Test]
        public void Fatigue_Escalates_And_Can_End_The_Match()
        {
            // Tiny decks → both players exhaust quickly and fatigue races to a win.
            var (host, s) = NewMatch(seed: 3UL, deckSize: 2);
            host.Submit(new MulliganIntent(s.Players[0].Id, Array.Empty<InstanceId>()));
            host.Submit(new MulliganIntent(s.Players[1].Id, Array.Empty<InstanceId>()));

            bool sawFatigue = false;
            for (int i = 0; i < 200 && s.Outcome == MatchOutcome.InProgress; i++)
            {
                var r = host.Submit(new EndTurnIntent(s.ActivePlayer));
                foreach (var e in r.Events)
                    if (e.Kind == VfxEvent.onFatigue) sawFatigue = true;
            }

            Assert.That(sawFatigue, Is.True, "Drawing from an empty deck must emit onFatigue.");
            Assert.That(s.Outcome, Is.AnyOf(MatchOutcome.Player0Win, MatchOutcome.Player1Win, MatchOutcome.Draw));
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.GameOver));
        }

        [Test]
        public void Concede_Awards_The_Opponent_The_Win()
        {
            var (host, s) = NewMatch(seed: 4UL);
            host.Submit(new MulliganIntent(s.Players[0].Id, Array.Empty<InstanceId>()));
            host.Submit(new MulliganIntent(s.Players[1].Id, Array.Empty<InstanceId>()));

            var loser = s.Players[1].Id;
            var r = host.Submit(new ConcedeIntent(loser));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Outcome, Is.EqualTo(MatchOutcome.Player0Win));
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.GameOver));
        }
    }
}
