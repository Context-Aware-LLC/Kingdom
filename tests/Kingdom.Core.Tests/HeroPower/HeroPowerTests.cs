using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Core.Cards;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.HeroPower
{
    /// <summary>
    /// L1 — HeroPowerIntent validation + execution (M1 Phase B step 3).
    /// Drives CommandProcessor directly in Main phase with a pre-constructed GameState, asserting on
    /// state mutations and the ordered semantic event stream.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class HeroPowerTests
    {
        // ── scaffolding ───────────────────────────────────────────────────────────

        private static GameState MainPhase(FakeCardDatabase db, int p0Mana = 10)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)
            {
                HeroPowerId = FixtureCard.Id("hp_searing_bolt")
            };
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)
            {
                HeroPowerId = FixtureCard.Id("hp_tidal_ward")
            };
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
            ps0.Mana.Max = ps0.Mana.Available = p0Mana;
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
            if (ci.Keywords.Contains(Keyword.AEGIS)) ci.HasAegisShield = true;
            ps.Board.Add(ci);
            return ci;
        }

        private static CardDefinition Minion(int id, int atk, int hp,
            IReadOnlyList<Keyword>? keywords = null)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 1, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? System.Array.Empty<Keyword>(), System.Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition HeroPower(int id, CardClass cls, IReadOnlyList<RuleDefinition> rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.HERO_POWER, 2, null, null, null,
                cls, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, false, null, "");

        private static CardDefinition WispDef()
            => new CardDefinition(FixtureCard.Id("token_wisp"), "token_wisp", "Wisp", CardType.MINION, 0, 0, 2, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.BULWARK }, System.Array.Empty<RuleDefinition>(),
                "", "art/token_wisp", SetId.CORE, false, null, "");

        private static RuleDefinition Damage(int amount)
            => TestRules.Rule("rule_1", Trigger.ON_CAST,
                new DamageAction(amount, new TargetSpec("ANY_CHARACTER", TargetSelection.Of(1))));

        private static RuleDefinition Summon(int count, int cardId)
            => TestRules.Rule("rule_1", Trigger.ON_CAST, new SummonAction(cardId, count));

        private static HeroPowerIntent UseHeroPower(GameState s, IReadOnlyList<TargetRef>? targets = null)
            => new HeroPowerIntent(s.ActivePlayer, targets ?? System.Array.Empty<TargetRef>());

        // ── validation tests ──────────────────────────────────────────────────────

        [Test]
        public void Reject_WrongPhase_Mulligan()
        {
            var db = new FakeCardDatabase();
            var s = MainPhase(db);
            s.Phase = TurnPhase.Mulligan;
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, UseHeroPower(s));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.WrongPhase));
        }

        [Test]
        public void Reject_NotYourTurn()
        {
            var db = new FakeCardDatabase();
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            // P1 tries to use hero power on P0's turn
            var intent = new HeroPowerIntent(s.Players[1].Id, System.Array.Empty<TargetRef>());
            var r = proc.Execute(s, intent);

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.NotYourTurn));
        }

        [Test]
        public void Reject_AlreadyUsedThisTurn()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            var target = new[] { new TargetRef(s.Players[1].Hero.Id) };
            proc.Execute(s, UseHeroPower(s, target));           // first use — should succeed
            var r = proc.Execute(s, UseHeroPower(s, target));   // second use — should fail

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.HeroPowerAlreadyUsed));
        }

        [Test]
        public void Reject_NotEnoughMana()
        {
            var db = new FakeCardDatabase();
            var s = MainPhase(db, p0Mana: 1);   // only 1 mana — hero power costs 2
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, UseHeroPower(s));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.NotEnoughMana));
        }

        [Test]
        public void Reject_ShroudTarget()
        {
            var db = new FakeCardDatabase();
            var shroudDef = Minion(FixtureCard.Id("shroudy"), 2, 3, new[] { Keyword.SHROUD });
            db.Register(shroudDef);
            var s = MainPhase(db);
            var enemy = Board(s, 1, shroudDef);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(enemy.Id) }));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void Reject_TargetedPower_WithoutTarget()
        {
            // Searing Bolt as shipped declares target.selector ANY_CHARACTER — an explicit-choice
            // selector. Firing it with no chosen target must reject, not fall through to the
            // DEAL_DAMAGE default (enemy face) with mana spent.
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, UseHeroPower(s));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(10), "no mana may be spent on a rejected power");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30), "no damage may leak to the default target");
        }

        // ── Searing Bolt tests ────────────────────────────────────────────────────

        [Test]
        public void SearingBolt_Deals1Damage_ToEnemyHero()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(29));
        }

        [Test]
        public void SearingBolt_Deals1Damage_ToFriendlyMinion()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var minionDef = Minion(FixtureCard.Id("dummy"), 2, 3);
            db.Register(minionDef);
            var s = MainPhase(db);
            var minion = Board(s, 0, minionDef);
            var proc = new CommandProcessor(db);

            proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(minion.Id) }));

            Assert.That(minion.CurrentHealth, Is.EqualTo(2));
        }

        [Test]
        public void SearingBolt_SetsHeroPowerUsedThisTurn()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            proc.Execute(s, UseHeroPower(s,
                new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(s.Players[0].HeroPowerUsedThisTurn, Is.True);
        }

        [Test]
        public void SearingBolt_CostsMana()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db, p0Mana: 5);
            var proc = new CommandProcessor(db);

            proc.Execute(s, UseHeroPower(s,
                new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(3));
            Assert.That(s.Players[0].Mana.Spent, Is.EqualTo(2));
        }

        [Test]
        public void SearingBolt_Emits_HeroPowerUsed_Then_DamageApplied()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(r.Events[0].Kind, Is.EqualTo(GameEventKind.HeroPowerUsed));
            Assert.That(r.Events[0].Source, Is.EqualTo(s.Players[0].Hero.Id));
            Assert.That(r.Events[1].Kind, Is.EqualTo(GameEventKind.DamageApplied));
        }

        [Test]
        public void HeroPower_Uses_The_Hero_As_Its_Source_Without_Minting_A_Throwaway_Instance()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);
            int nextInstanceBefore = s.NextInstanceCounter;

            CommandResult result = proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(result.Accepted, Is.True);
            Assert.That(s.NextInstanceCounter, Is.EqualTo(nextInstanceBefore));
            Assert.That(result.Events.Single(e => e.Kind == GameEventKind.DamageApplied).Source,
                Is.EqualTo(s.Players[0].Hero.Id));
        }

        [Test]
        public void SearingBolt_KillsMinion_DeathCheckRuns()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var fragile = Minion(FixtureCard.Id("fragile"), 1, 1);
            db.Register(fragile);
            var s = MainPhase(db);
            var m = Board(s, 1, fragile);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(m.Id) }));

            Assert.That(s.Players[1].Board, Is.Empty);
            Assert.That(r.Events.Any(e => e.Kind == GameEventKind.CardDestroyed), Is.True);
        }

        // ── Tidal Ward tests ──────────────────────────────────────────────────────

        [Test]
        public void TidalWard_Summons_Wisp_WithBulwark()
        {
            var wispDef = WispDef();
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_tidal_ward"), CardClass.TIDECALLER,
                new[] { Summon(1, FixtureCard.Id("token_wisp")) }));
            db.Register(wispDef);
            var s = MainPhase(db);
            // P1 turn — swap active player so we test Tidecaller's hero power
            s.ActivePlayer = s.Players[1].Id;
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, UseHeroPower(s));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Board.Count, Is.EqualTo(1));
            var wisp = s.Players[1].Board[0];
            Assert.That(wisp.CurrentAttack, Is.EqualTo(0));
            Assert.That(wisp.CurrentHealth, Is.EqualTo(2));
            Assert.That(wisp.Keywords.Contains(Keyword.BULWARK), Is.True);
        }

        [Test]
        public void TidalWard_Emits_HeroPowerUsed_Then_CardSummoned()
        {
            var wispDef = WispDef();
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_tidal_ward"), CardClass.TIDECALLER,
                new[] { Summon(1, FixtureCard.Id("token_wisp")) }));
            db.Register(wispDef);
            var s = MainPhase(db);
            s.ActivePlayer = s.Players[1].Id;
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, UseHeroPower(s));

            Assert.That(r.Events[0].Kind, Is.EqualTo(GameEventKind.HeroPowerUsed));
            Assert.That(r.Events[1].Kind, Is.EqualTo(GameEventKind.CardSummoned));
        }

        // ── turn-reset test ───────────────────────────────────────────────────────

        [Test]
        public void HeroPowerUsed_Resets_OnStartOfNextTurn()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD,
                new[] { Damage(1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            proc.Execute(s, UseHeroPower(s,
                new[] { new TargetRef(s.Players[1].Hero.Id) }));
            Assert.That(s.Players[0].HeroPowerUsedThisTurn, Is.True);

            proc.Execute(s, new EndTurnIntent(s.ActivePlayer));
            proc.Execute(s, new EndTurnIntent(s.ActivePlayer));  // P1 ends their turn → back to P0

            Assert.That(s.Players[0].HeroPowerUsedThisTurn, Is.False);
        }

        // ── a second rule over an automatic, filtered set ─────────────────────────

        // "Deal 2 damage to the enemy hero and give all friendly Shroud minions +1/+0." A hero
        // power is REFUSED when a player-chosen target has no candidate (Targeting.CanFizzle is
        // MINION/WEAPON only, and that refusal is what killed Shadow Mark). An automatic set is
        // the other case: empty is a legal answer and the first rule still fires.
        private static CardDefinition BackstabWithShroudBuff()
            => HeroPower(FixtureCard.Id("hp_searing_bolt"), CardClass.EMBERWARD, new[]
            {
                TestRules.Rule("rule_1", Trigger.ON_CAST,
                    new DamageAction(2, new TargetSpec("ENEMY_HERO", TargetSelection.Of(1)))),
                TestRules.Rule("rule_2", Trigger.ON_CAST,
                    new ModifyStatsAction(1, 0, new TargetSpec("ANY_FRIENDLY_MINION",
                        TargetSelection.AllOf("ANY_FRIENDLY_MINION"),
                        filter: new TargetFilterDef(keyword: Keyword.SHROUD)))),
            });

        [Test]
        public void AutomaticSetRule_IsNotRefused_WhenTheSetIsEmpty()
        {
            var db = new FakeCardDatabase();
            db.Register(BackstabWithShroudBuff());
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, UseHeroPower(s));

            Assert.That(r.Accepted, Is.True, r.Reason.ToString());
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(28));
        }

        [Test]
        public void AutomaticSetRule_BuffsOnlyFriendlyShroudMinions_AndStacksPerPress()
        {
            var db = new FakeCardDatabase();
            db.Register(BackstabWithShroudBuff());
            var lurker = Minion(FixtureCard.Id("lurker"), 2, 1, new[] { Keyword.SHROUD });
            var plain = Minion(FixtureCard.Id("plain"), 2, 3);
            db.Register(lurker);
            db.Register(plain);
            var s = MainPhase(db);
            var mine = Board(s, 0, lurker);
            var mineUnshrouded = Board(s, 0, plain);
            var theirs = Board(s, 1, lurker);
            var proc = new CommandProcessor(db);

            Assert.That(proc.Execute(s, UseHeroPower(s)).Accepted, Is.True);
            Assert.That(mine.CurrentAttack, Is.EqualTo(3));
            Assert.That(mine.CurrentHealth, Is.EqualTo(1));
            Assert.That(mineUnshrouded.CurrentAttack, Is.EqualTo(2));
            Assert.That(theirs.CurrentAttack, Is.EqualTo(2));

            s.Players[0].HeroPowerUsedThisTurn = false;   // the next turn's press
            Assert.That(proc.Execute(s, UseHeroPower(s)).Accepted, Is.True);
            Assert.That(mine.CurrentAttack, Is.EqualTo(4));
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(26));
        }

        [Test]
        public void AutomaticSetRule_BuffOutlivesTheShroud()
        {
            var db = new FakeCardDatabase();
            db.Register(BackstabWithShroudBuff());
            var lurker = Minion(FixtureCard.Id("lurker"), 2, 1, new[] { Keyword.SHROUD });
            db.Register(lurker);
            var s = MainPhase(db);
            var mine = Board(s, 0, lurker);
            var proc = new CommandProcessor(db);
            proc.Execute(s, UseHeroPower(s));

            var r = proc.Execute(s, new AttackIntent(s.ActivePlayer, mine.Id,
                new TargetRef(s.Players[1].Hero.Id)));

            Assert.That(r.Accepted, Is.True, r.Reason.ToString());
            Assert.That(mine.Keywords, Does.Not.Contain(Keyword.SHROUD));
            Assert.That(mine.CurrentAttack, Is.EqualTo(3));
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30 - 2 - 3));
        }
    }
}
