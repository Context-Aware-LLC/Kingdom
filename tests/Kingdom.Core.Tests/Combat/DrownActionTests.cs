using System.Collections.Generic;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Core.Cards;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Combat
{
    /// <summary>
    /// DROWN — a one-shot action (not a persistent carrier keyword, same shape as SILENCE/BACKDRAFT)
    /// that deals half a minion's CURRENT health rounded up, or a flat 2 to a hero. Unlike every other
    /// damage source it is not stopped by Aegis (Sam, 2026-09-05: "divine shield can't protect you
    /// against drowning").
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class DrownActionTests
    {
        private static GameState MainPhase()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("M"), new DeterministicRandom(42UL), ps0, ps1)
            {
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.ActivePlayer = p0;
            ps0.Mana.Max = ps0.Mana.Available = 10;
            ps1.Mana.Max = ps1.Mana.Available = 10;
            return s;
        }

        private static CardInstance Hand(GameState s, int seat, int defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(ci);
            return ci;
        }

        private static CardInstance Board(GameState s, int seat, CardDefinition def)
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
            foreach (var k in def.Keywords) ci.Keywords.Add(k);
            ci.HasAegisShield = ci.Keywords.Contains(Keyword.AEGIS);
            ps.Board.Add(ci);
            return ci;
        }

        private static CardDefinition Minion(int id, int atk, int hp, IReadOnlyList<Keyword>? keywords = null)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 1, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? System.Array.Empty<Keyword>(), System.Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, RuleDefinition rule)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), new[] { rule }, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static RuleDefinition DrownRule(string selector)
            => TestRules.Rule("drown", Trigger.ON_CAST, new DrownAction(new TargetSpec(selector, TargetSelection.Of(1))));

        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(3, 2)]
        [TestCase(4, 2)]
        [TestCase(5, 3)]
        [TestCase(6, 3)]
        [TestCase(7, 4)]
        public void Drown_Deals_Half_Current_Health_Rounded_Up_To_A_Minion(int currentHealth, int expectedDamage)
        {
            var db = new FakeCardDatabase().Register(Spell(FixtureCard.Id("tide"), DrownRule("ANY_MINION")));
            var s = MainPhase();
            var target = Board(s, 1, Minion(FixtureCard.Id("target"), atk: 1, hp: 20));
            target.CurrentHealth = currentHealth;
            var card = Hand(s, 0, FixtureCard.Id("tide"));

            var r = new CommandProcessor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(target.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(target.CurrentHealth, Is.EqualTo(currentHealth - expectedDamage));
        }

        [Test]
        public void Drown_Deals_A_Flat_Two_To_A_Hero_Regardless_Of_Current_Health()
        {
            var db = new FakeCardDatabase().Register(Spell(FixtureCard.Id("tide"), DrownRule("ENEMY_HERO")));
            var s = MainPhase();
            s.Players[1].Hero.CurrentHealth = 30;
            var card = Hand(s, 0, FixtureCard.Id("tide"));

            var r = new CommandProcessor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new TargetRef[0]));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(28), "flat 2 regardless of the hero's 30 current health.");
        }

        [Test]
        public void Drown_Is_Not_Absorbed_By_Aegis()
        {
            var db = new FakeCardDatabase().Register(Spell(FixtureCard.Id("tide"), DrownRule("ANY_MINION")));
            var s = MainPhase();
            var warded = Board(s, 1, Minion(FixtureCard.Id("warded"), atk: 1, hp: 10, keywords: new[] { Keyword.AEGIS }));
            var card = Hand(s, 0, FixtureCard.Id("tide"));

            Assert.That(warded.HasAegisShield, Is.True, "precondition: it boarded with its shield up.");

            var r = new CommandProcessor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, new[] { new TargetRef(warded.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(warded.CurrentHealth, Is.EqualTo(5), "half of 10 current health landed despite Aegis.");
            Assert.That(warded.HasAegisShield, Is.True, "the shield never saw this hit — still up for the next one.");
        }
    }
}
