using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Tests.Support;
using Verb = Emberfall.Core.Contracts.Action;

namespace Emberfall.Core.Tests.HeroPower
{
    /// <summary>
    /// L1 — HeroPowerIntent validation + execution (M1 Phase B step 3).
    /// Drives CommandProcessor directly in Main phase with a pre-constructed GameState, asserting on
    /// state mutations and the ordered D7 event stream.
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
            var hero0 = new HeroInstance(new InstanceId(0), p0, CardClass.EMBERWARD, 30)
            {
                HeroPowerId = "hp_searing_bolt"
            };
            var hero1 = new HeroInstance(new InstanceId(1), p1, CardClass.TIDECALLER, 30)
            {
                HeroPowerId = "hp_tidal_ward"
            };
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("M"), new DeterministicRandom(42UL), ps0, ps1)
            {
                NextInstanceCounter = 2,
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
            if (ci.Keywords.Contains(Keyword.DIVINE_SHIELD)) ci.HasAegisShield = true;
            ps.Board.Add(ci);
            return ci;
        }

        private static CardDefinition Minion(string id, int atk, int hp,
            IReadOnlyList<Keyword>? keywords = null)
            => new CardDefinition(id, id, CardType.MINION, 1, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? System.Array.Empty<Keyword>(), System.Array.Empty<EffectDef>(),
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static CardDefinition HeroPower(string id, CardClass cls, IReadOnlyList<EffectDef> effects)
            => new CardDefinition(id, id, CardType.HERO_POWER, 2, null, null, null,
                cls, Rarity.COMMON,
                System.Array.Empty<Keyword>(), effects,
                "", "", "art/" + id, SetId.CORE, false, null, "", false);

        private static CardDefinition WispDef()
            => new CardDefinition("token_wisp", "Wisp", CardType.MINION, 0, 0, 2, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.TAUNT }, System.Array.Empty<EffectDef>(),
                "", "", "art/token_wisp", SetId.CORE, false, null, "", false);

        private static EffectDef Eff(Trigger trigger, Verb action, int? amount = null, string? tokenId = null)
            => new EffectDef(trigger, action, amount, null, tokenId, null, null);

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
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            proc.Execute(s, UseHeroPower(s));                   // first use — should succeed
            var r = proc.Execute(s, UseHeroPower(s));           // second use — should fail

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
        public void Reject_StealthTarget()
        {
            var db = new FakeCardDatabase();
            var stealthDef = Minion("stealthy", 2, 3, new[] { Keyword.STEALTH });
            db.Register(stealthDef);
            var s = MainPhase(db);
            var enemy = Board(s, 1, stealthDef);
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
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { new EffectDef(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                                      "ANY_CHARACTER") }));
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
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
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
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
            var minionDef = Minion("dummy", 2, 3);
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
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            proc.Execute(s, UseHeroPower(s));

            Assert.That(s.Players[0].HeroPowerUsedThisTurn, Is.True);
        }

        [Test]
        public void SearingBolt_CostsMana()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
            var s = MainPhase(db, p0Mana: 5);
            var proc = new CommandProcessor(db);

            proc.Execute(s, UseHeroPower(s));

            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(3));
            Assert.That(s.Players[0].Mana.Spent, Is.EqualTo(2));
        }

        [Test]
        public void SearingBolt_Emits_onHeroPower_Then_onDamage()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(s.Players[1].Hero.Id) }));

            Assert.That(r.Events[0].Kind, Is.EqualTo(VfxEvent.onHeroPower));
            Assert.That(r.Events[0].Source, Is.EqualTo(s.Players[0].Hero.Id));
            Assert.That(r.Events[1].Kind, Is.EqualTo(VfxEvent.onDamage));
        }

        [Test]
        public void SearingBolt_KillsMinion_DeathCheckRuns()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
            var fragile = Minion("fragile", 1, 1);
            db.Register(fragile);
            var s = MainPhase(db);
            var m = Board(s, 1, fragile);
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(m.Id) }));

            Assert.That(s.Players[1].Board, Is.Empty);
            Assert.That(r.Events.Any(e => e.Kind == VfxEvent.onDeath), Is.True);
        }

        // ── Tidal Ward tests ──────────────────────────────────────────────────────

        [Test]
        public void TidalWard_Summons_Wisp_WithTaunt()
        {
            var wispDef = WispDef();
            var db = new FakeCardDatabase();
            db.Register(HeroPower("hp_tidal_ward", CardClass.TIDECALLER,
                new[] { Eff(Trigger.ON_CAST, Verb.SUMMON, 1, tokenId: "token_wisp") }));
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
            Assert.That(wisp.Keywords.Contains(Keyword.TAUNT), Is.True);
        }

        [Test]
        public void TidalWard_Emits_onHeroPower_Then_onSummon()
        {
            var wispDef = WispDef();
            var db = new FakeCardDatabase();
            db.Register(HeroPower("hp_tidal_ward", CardClass.TIDECALLER,
                new[] { Eff(Trigger.ON_CAST, Verb.SUMMON, 1, tokenId: "token_wisp") }));
            db.Register(wispDef);
            var s = MainPhase(db);
            s.ActivePlayer = s.Players[1].Id;
            var proc = new CommandProcessor(db);

            var r = proc.Execute(s, UseHeroPower(s));

            Assert.That(r.Events[0].Kind, Is.EqualTo(VfxEvent.onHeroPower));
            Assert.That(r.Events[1].Kind, Is.EqualTo(VfxEvent.onSummon));
        }

        // ── turn-reset test ───────────────────────────────────────────────────────

        [Test]
        public void HeroPowerUsed_Resets_OnStartOfNextTurn()
        {
            var db = new FakeCardDatabase();
            db.Register(HeroPower("hp_searing_bolt", CardClass.EMBERWARD,
                new[] { Eff(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1) }));
            var s = MainPhase(db);
            var proc = new CommandProcessor(db);

            proc.Execute(s, UseHeroPower(s));
            Assert.That(s.Players[0].HeroPowerUsedThisTurn, Is.True);

            proc.Execute(s, new EndTurnIntent(s.ActivePlayer));
            proc.Execute(s, new EndTurnIntent(s.ActivePlayer));  // P1 ends their turn → back to P0

            Assert.That(s.Players[0].HeroPowerUsedThisTurn, Is.False);
        }
    }
}
