using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Core.Cards;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Combat
{
    /// <summary>
    /// <see cref="GameEvent.HealthBefore"/> on a <see cref="GameEventKind.DamageApplied"/> that landed:
    /// the target's real Health the instant before the hit, which is what the King's Overkill
    /// commission is scored from. A replay of Health through the event stream cannot give it, because
    /// a stat change mid-command is not replayable from its event. Driven through
    /// <c>CommandProcessor.Execute</c>, so the value is the one the pipeline really read.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class DamagePipelineHealthBeforeTests
    {
        private static readonly int Poke = FixtureCard.Id("hb_poke");
        private static readonly int BuffThenHit = FixtureCard.Id("hb_buff_then_hit");
        private static readonly int Target = FixtureCard.Id("hb_target");
        private static readonly int Trio = FixtureCard.Id("hb_trio");
        private static readonly int Warded = FixtureCard.Id("hb_warded");

        [Test]
        public void A_Hit_On_A_Minion_Carries_Its_Health_Before_The_Hit()
        {
            var db = Db();
            var s = MainPhase();
            CardInstance minion = Board(s, 1, db.Get(Target));
            minion.CurrentHealth = 3;

            CommandResult r = Play(db, s, Poke, minion.Id);

            GameEvent hit = Hits(r, minion.Id).Single();
            Assert.That(hit.Amount, Is.EqualTo(9));
            Assert.That(hit.HealthBefore, Is.EqualTo(3));
        }

        [Test]
        public void A_Minion_Buffed_Earlier_In_The_Same_Command_Reports_Its_Buffed_Health()
        {
            var db = Db();
            var s = MainPhase();
            CardInstance minion = Board(s, 1, db.Get(Trio));

            CommandResult r = Play(db, s, BuffThenHit);

            GameEvent hit = Hits(r, minion.Id).Single();
            Assert.That(hit.HealthBefore, Is.EqualTo(5), "+2 Health landed before the 9-damage hit");
        }

        [Test]
        public void A_Hit_On_A_Hero_Carries_Its_Health_Not_Its_Armor()
        {
            var db = Db();
            var s = MainPhase();
            HeroInstance hero = s.Players[1].Hero;
            hero.CurrentHealth = 20;
            hero.Armor = 4;

            CommandResult r = Play(db, s, Poke, hero.Id);

            GameEvent hit = Hits(r, hero.Id).Single();
            Assert.That(hit.Amount, Is.EqualTo(9), "the event amount is the hit, armor included");
            Assert.That(hit.HealthBefore, Is.EqualTo(20));
        }

        [Test]
        public void An_Aegis_Absorbed_Hit_Carries_No_Health_Before()
        {
            var db = Db();
            var s = MainPhase();
            CardInstance minion = Board(s, 1, db.Get(Warded));

            CommandResult r = Play(db, s, Poke, minion.Id);

            GameEvent hit = Hits(r, minion.Id).Single();
            Assert.That(hit.Flags & EventFlags.AegisAbsorbed, Is.EqualTo(EventFlags.AegisAbsorbed));
            Assert.That(hit.HealthBefore, Is.Null);
        }

        [Test]
        public void Every_Other_Event_Carries_No_Health_Before()
        {
            var db = Db();
            var s = MainPhase();
            Board(s, 1, db.Get(Target));

            CommandResult r = Play(db, s, BuffThenHit);

            Assert.That(r.Accepted, Is.True, r.Reason?.ToString());
            Assert.That(r.Events.Any(e => e.Kind == GameEventKind.StatsModified), Is.True, "precondition: the buff emitted");
            Assert.That(r.Events.Where(e => e.Kind != GameEventKind.DamageApplied).Select(e => e.HealthBefore),
                Is.All.Null);
        }

        // ── scaffolding ───────────────────────────────────────────────────────────

        private static IEnumerable<GameEvent> Hits(CommandResult r, InstanceId target)
        {
            Assert.That(r.Accepted, Is.True, r.Reason?.ToString());
            return r.Events.Where(e => e.Kind == GameEventKind.DamageApplied && e.Target == target);
        }

        private static FakeCardDatabase Db() => new FakeCardDatabase()
            .Register(Spell(Poke, TestRules.Rule("hit", Trigger.ON_CAST,
                TestRules.Damage(Trigger.ON_CAST, 9, TargetSelection.Of(1)))))
            .Register(Spell(BuffThenHit,
                TestRules.Rule("buff", Trigger.ON_CAST,
                    TestRules.ModifyStats(Trigger.ON_CAST, 0, 2, TargetSelection.AutomaticOf(1), "ANY_ENEMY_MINION")),
                TestRules.Rule("hit", Trigger.ON_CAST,
                    TestRules.Damage(Trigger.ON_CAST, 9, TargetSelection.AutomaticOf(1), "ANY_ENEMY_MINION"))))
            .Register(Minion(Target, keywords: System.Array.Empty<Keyword>()))
            .Register(Minion(Trio, keywords: System.Array.Empty<Keyword>(), health: 3))
            .Register(Minion(Warded, keywords: new[] { Keyword.AEGIS }));

        private static CommandResult Play(FakeCardDatabase db, GameState s, int spell, params InstanceId[] targets)
        {
            var card = new CardInstance(s.NextInstanceId(), spell, s.Players[0].Id) { Zone = Zone.Hand };
            s.Players[0].Hand.Add(card);
            return new CommandProcessor(db).Execute(s, new PlayCardIntent(
                s.Players[0].Id, card.Id, 0, targets.Select(t => new TargetRef(t)).ToArray()));
        }

        private static GameState MainPhase()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var ps0 = new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30));
            var ps1 = new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30));
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

        private static CardDefinition Minion(int id, IReadOnlyList<Keyword> keywords, int health = 10)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 1, 1, health, null,
                CardClass.NEUTRAL, Rarity.COMMON, keywords, System.Array.Empty<RuleDefinition>(),
                "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");
    }
}
