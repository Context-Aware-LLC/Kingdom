using System;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Determinism
{
    /// <summary>
    /// D-P1 + D-OQ1 (dev-docs/architecture/determinism-and-projection.md:23): <c>InstanceId(0)</c> is the reserved redaction
    /// sentinel — the engine never mints it — and a publicly-known id is retired when its card
    /// enters a hidden zone (RETURN_TO_HAND re-mints on the board→hand branch, before the emit;
    /// a hand-cap burn goes to the public graveyard and keeps its id).
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SentinelReservationTests
    {
        [Test]
        public void Engine_Never_Mints_InstanceId_Zero()
        {
            var db = new FakeCardDatabase();
            var processor = new CommandProcessor(db);
            var setup = new MatchSetup(
                CardClass.EMBERWARD, FakeCardDatabase.Deck("ember", 12),
                CardClass.TIDECALLER, FakeCardDatabase.Deck("tide", 12));
            var host = new LocalMatchHost(processor, db, setup);
            var s = host.BeginMatch(new MatchId("M"), new PlayerId("P0"), new PlayerId("P1"), 7UL);

            Assert.That(s.Players[0].Hero.Id.Value, Is.EqualTo(1), "hero0 mints first, from 1 (D-P1).");
            Assert.That(s.Players[1].Hero.Id.Value, Is.EqualTo(2));

            foreach (var ps in s.Players)
            {
                foreach (var c in ps.Hand) Assert.That(c.Id.Value, Is.Not.Zero);
                foreach (var c in ps.Deck) Assert.That(c.Id.Value, Is.Not.Zero);
            }
        }

        // ── D-OQ1: bounce re-mint ────────────────────────────────────────────────

        private static readonly CardDefinition Bounce = new CardDefinition(
            "bounce", "bounce", CardType.SPELL, 2, null, null, null,
            CardClass.NEUTRAL, Rarity.COMMON,
            Array.Empty<Keyword>(),
            new[] { TestRules.Rule("bounce", Trigger.ON_CAST,
                new ReturnToHandAction(new TargetSpec("ANY_MINION"))) },
            "", "", "art/bounce", SetId.CORE, true, null, "", false);

        private static readonly CardDefinition Vanilla = new CardDefinition(
            "vanilla", "vanilla", CardType.MINION, 1, 2, 2, null,
            CardClass.NEUTRAL, Rarity.COMMON,
            Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(),
            "", "", "art/vanilla", SetId.CORE, true, null, "", false);

        private static GameState MainPhase(FakeCardDatabase db)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            var s = new GameState(new MatchId("M"), new DeterministicRandom(42UL),
                new PlayerState(p0, hero0), new PlayerState(p1, hero1))
            {
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.ActivePlayer = p0;
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = 10;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;
            return s;
        }

        private static CardInstance PutOnBoard(GameState s, int seat, CardDefinition def)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = ps.Board.Count,
            };
            ps.Board.Add(ci);
            return ci;
        }

        private static CardInstance PutInHand(GameState s, int seat, string defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(ci);
            return ci;
        }

        [Test]
        public void Bounce_To_Hand_ReMints_The_Publicly_Known_Id()
        {
            var db = new FakeCardDatabase().Register(Bounce).Register(Vanilla);
            var s = MainPhase(db);
            var minion = PutOnBoard(s, 1, Vanilla);
            var spell = PutInHand(s, 0, "bounce");
            int publicId = minion.Id.Value;
            int counterBefore = s.NextInstanceCounter;

            var r = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.Players[0].Id, spell.Id, 0, new[] { new TargetRef(minion.Id) }));

            Assert.That(r.Accepted, Is.True, $"bounce rejected: {r.Reason}");
            Assert.That(s.Players[1].Hand, Has.Count.EqualTo(1));
            var bounced = s.Players[1].Hand[0];
            Assert.That(bounced.Id.Value, Is.Not.EqualTo(publicId),
                "A board id the opponent saw must not survive into the hidden hand (D-OQ1).");
            Assert.That(bounced.Id.Value, Is.GreaterThanOrEqualTo(counterBefore), "Fresh mint.");

            bool eventCarriesNewId = false;
            foreach (var e in r.Events)
                if (e.Kind == GameEventKind.CardBounced && e.Target.HasValue && e.Target.Value.Equals(bounced.Id))
                    eventCarriesNewId = true;
            Assert.That(eventCarriesNewId, Is.True,
                "Re-mint runs BEFORE the CardBounced emit — event and snapshot must agree (§10 finding 4).");
        }

        [Test]
        public void Bounce_Into_Full_Hand_Burns_Publicly_And_Keeps_Id()
        {
            var db = new FakeCardDatabase().Register(Bounce).Register(Vanilla);
            var s = MainPhase(db);
            var minion = PutOnBoard(s, 1, Vanilla);
            for (int i = 0; i < MatchEngine.HandCap; i++) PutInHand(s, 1, "vanilla");
            var spell = PutInHand(s, 0, "bounce");
            int publicId = minion.Id.Value;

            var r = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.Players[0].Id, spell.Id, 0, new[] { new TargetRef(minion.Id) }));

            Assert.That(r.Accepted, Is.True, $"bounce rejected: {r.Reason}");
            Assert.That(s.Players[1].Graveyard, Has.Count.EqualTo(1));
            Assert.That(s.Players[1].Graveyard[0].Id.Value, Is.EqualTo(publicId),
                "The graveyard is public — no hidden-zone entry, no re-mint (D-OQ1 scope).");
        }
    }
}
