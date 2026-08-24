using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Combat
{
    /// <summary>
    /// L1 unit tests for M1 Phase B step 2: Attack command — legality checks, mutual damage,
    /// weapon combat, VANGUARD, OVERWHELM, SHROUD/BULWARK enforcement.
    /// Driven through the public <c>CommandProcessor.Execute</c> door; no golden fixtures.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CombatTests
    {
        // ── scenario scaffolding ─────────────────────────────────────────────────

        private static GameState MainPhase(int p0Mana = 10)
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
            ps0.Mana.Max = ps0.Mana.Available = p0Mana;
            ps1.Mana.Max = ps1.Mana.Available = 10;
            return s;
        }

        private static CardInstance Board(GameState s, int seat, CardDefinition def, bool summonedThisTurn = false)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = ps.Board.Count,
                SummonedThisTurn = summonedThisTurn,
            };
            foreach (var k in def.Keywords) ci.Keywords.Add(k);
            if (ci.Keywords.Contains(Keyword.AEGIS)) ci.HasAegisShield = true;
            ps.Board.Add(ci);
            return ci;
        }

        private static void EquipWeapon(GameState s, int seat, int atk, int durability)
        {
            s.Players[seat].Weapon = new CardInstance(s.NextInstanceId(), FixtureCard.Id("weapon"), s.Players[seat].Id)
            {
                Zone = Zone.Weapon,
                CurrentAttack = atk,
                CurrentDurability = durability,
            };
        }

        private static CardDefinition Minion(int id, int atk, int hp, params Keyword[] kws)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 1, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                kws, System.Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static AttackIntent Attack(GameState s, InstanceId attacker, InstanceId defender)
            => new AttackIntent(s.ActivePlayer, attacker, new TargetRef(defender));

        private static CommandProcessor Proc() => new CommandProcessor(new Support.FakeCardDatabase());

        // ── legality ─────────────────────────────────────────────────────────────

        [Test]
        public void Summoning_Sickness_Blocks_Attack()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("vanilla"), 3, 4), summonedThisTurn: true);
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 2, 3));

            var r = Proc().CanExecute(s, Attack(s, atk.Id, def.Id), out var reason);

            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.HasSummoningSickness));
        }

        [Test]
        public void FERVOR_Bypasses_Summoning_Sickness()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("fervorr"), 3, 4, Keyword.FERVOR), summonedThisTurn: true);
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 2, 3));

            var r = Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(r.Accepted, Is.True);
            Assert.That(def.CurrentHealth, Is.EqualTo(0), "defender took 3 damage");
        }

        // Regression: REKINDLE must trigger on a COMBAT death, not only on effect/Drain death.
        // The existing ThronelessKing_Rekindle_Returns_At_One_HP kills via effect damage; a live
        // networked match killed the king in combat and (reported) no 1-HP copy returned. This
        // exercises the combat path (ApplyAttack → Drain → DeathCheck → Resummon).
        [Test]
        public void REKINDLE_Triggers_On_Combat_Death()
        {
            var s = MainPhase();
            var attacker = Board(s, 0, Minion(FixtureCard.Id("bruiser"), 6, 8));          // survives retaliation
            var king = Board(s, 1, Minion(FixtureCard.Id("neutral_throneless_king"), 6, 6, Keyword.BULWARK, Keyword.REKINDLE));

            var r = Proc().Execute(s, Attack(s, attacker.Id, king.Id));

            Assert.That(r.Accepted, Is.True, "attack resolved");
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1),
                "REKINDLE copy should remain on the board after a combat death");
            var copy = s.Players[1].Board[0];
            Assert.That(copy.CurrentHealth, Is.EqualTo(1), "REKINDLE copy returns at 1 HP");
            Assert.That(copy.DefinitionId, Is.EqualTo(FixtureCard.Id("neutral_throneless_king")), "copy is still a Throneless King");
        }

        [Test]
        public void SKIRMISH_Can_Attack_Enemy_Minion_But_Not_Hero_On_Summon_Turn()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("skirmisher"), 3, 4, Keyword.SKIRMISH), summonedThisTurn: true);
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 2, 3));

            // Can attack enemy minion
            var rMinion = Proc().CanExecute(s, Attack(s, atk.Id, def.Id), out _);
            Assert.That(rMinion, Is.True);

            // Cannot attack enemy hero
            var rHero = Proc().CanExecute(s, Attack(s, atk.Id, s.Players[1].Hero.Id), out var reason);
            Assert.That(rHero, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void Frosted_Minion_Cannot_Attack()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("frosted"), 3, 4));
            TestFrost.Apply(atk);
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 2, 3));

            var r = Proc().CanExecute(s, Attack(s, atk.Id, def.Id), out var reason);

            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.AttackerFrosted));
        }

        [Test]
        public void DOUBLESTRIKE_Allows_Two_Attacks_Third_Rejected()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("doublestrike"), 2, 8, Keyword.DOUBLESTRIKE));
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 1, 20));

            var p = Proc();
            Assert.That(p.Execute(s, Attack(s, atk.Id, def.Id)).Accepted, Is.True, "first attack");
            Assert.That(p.Execute(s, Attack(s, atk.Id, def.Id)).Accepted, Is.True, "second attack");

            var r = p.CanExecute(s, Attack(s, atk.Id, def.Id), out var reason);
            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.NoAttacksLeft));
        }

        [Test]
        public void BULWARK_Enforces_Must_Attack_Bulwarker()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("attacker"), 3, 4));
            var bulwarker = Board(s, 1, Minion(FixtureCard.Id("bulwarker"), 1, 5, Keyword.BULWARK));
            var freeTarget = Board(s, 1, Minion(FixtureCard.Id("free"), 2, 3));

            // Must attack the bulwarker
            Assert.That(Proc().CanExecute(s, Attack(s, atk.Id, bulwarker.Id), out _), Is.True);

            // Cannot skip the bulwarker
            var r = Proc().CanExecute(s, Attack(s, atk.Id, freeTarget.Id), out var reason);
            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void SHROUD_Blocks_Targeting_And_Breaks_When_Attacker_Swings()
        {
            var s = MainPhase();
            var shroudy = Board(s, 1, Minion(FixtureCard.Id("ghost"), 2, 3, Keyword.SHROUD));
            var atk = Board(s, 0, Minion(FixtureCard.Id("attacker"), 3, 4));

            // Cannot target shrouded enemy
            var r = Proc().CanExecute(s, Attack(s, atk.Id, shroudy.Id), out var reason);
            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.IllegalTarget));

            // But after the shroud minion itself attacks, it loses shroud
            shroudy.Keywords.Remove(Keyword.SHROUD); // manually break for this check
            Assert.That(Proc().CanExecute(s, Attack(s, atk.Id, shroudy.Id), out _), Is.True);
        }

        [Test]
        public void SHROUD_Breaks_When_Shroudy_Minion_Attacks()
        {
            var s = MainPhase();
            var ghost = Board(s, 0, Minion(FixtureCard.Id("ghost"), 2, 5, Keyword.SHROUD));
            var target = Board(s, 1, Minion(FixtureCard.Id("target"), 1, 10));

            Assert.That(ghost.Keywords.Contains(Keyword.SHROUD), Is.True, "has shroud before attack");
            Proc().Execute(s, Attack(s, ghost.Id, target.Id));
            Assert.That(ghost.Keywords.Contains(Keyword.SHROUD), Is.False, "shroud removed on attack");
        }

        [Test]
        public void No_Weapon_Means_Hero_Cannot_Attack()
        {
            var s = MainPhase();
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 2, 3));
            Assert.That(s.Players[0].Weapon, Is.Null);

            var r = Proc().CanExecute(s, Attack(s, s.Players[0].Hero.Id, def.Id), out var reason);
            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.WeaponMissing));
        }

        // ── combat resolution ────────────────────────────────────────────────────

        [Test]
        public void Basic_Mutual_Damage()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("a"), 3, 5));
            var def = Board(s, 1, Minion(FixtureCard.Id("d"), 2, 4));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(atk.CurrentHealth, Is.EqualTo(3), "attacker took 2 retaliation");
            Assert.That(def.CurrentHealth, Is.EqualTo(1), "defender took 3 damage");
        }

        [Test]
        public void Hero_Weapon_Attack_Deals_Damage_And_Decrements_Durability()
        {
            var s = MainPhase();
            EquipWeapon(s, 0, atk: 4, durability: 2);
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 2, 6));

            Proc().Execute(s, Attack(s, s.Players[0].Hero.Id, def.Id));

            Assert.That(def.CurrentHealth, Is.EqualTo(2), "hero dealt 4 damage");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(28), "hero took 2 retaliation");
            Assert.That(s.Players[0].Weapon, Is.Not.Null, "weapon still alive");
            Assert.That(s.Players[0].Weapon!.CurrentDurability, Is.EqualTo(1), "durability decremented");
        }

        // Regression: a weapon's ON_ATTACK effect resolves from the equipped CardInstance.
        [Test]
        public void Weapon_ON_ATTACK_Effect_Fires_On_Hero_Attack()
        {
            var weaponDef = new CardDefinition(
                id: FixtureCard.Id("verdant_grasping_thornvine"), handle: "verdant_grasping_thornvine", name: "Grasping Thornvine", type: CardType.WEAPON, cost: 3,
                attack: 2, health: null, durability: 3,
                @class: CardClass.VERDANT, rarity: Rarity.RARE,
                keywords: System.Array.Empty<Keyword>(),
                rules: new[] { TestRules.Rule("rule_1", Trigger.ON_ATTACK,
                    new HealAction(2, new TargetSpec("FRIENDLY_HERO", TargetSelection.Of(1)))) },
                flavorText: "", artId: "art/x", setId: SetId.EMBERFALL,
                collectible: true, tribe: null, factionId: "");

            var db = new Support.FakeCardDatabase().Register(weaponDef);
            var proc = new CommandProcessor(db);

            var s = MainPhase();
            s.Players[0].Weapon = new CardInstance(s.NextInstanceId(), weaponDef.Id, s.Players[0].Id)
            {
                Zone = Zone.Weapon,
                CurrentAttack = 2,
                CurrentDurability = 3,
            };
            s.Players[0].Hero.CurrentHealth = 20; // below max so the heal is observable
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 0, 10)); // 0 attack: no retaliation to muddy the heal

            var r = proc.Execute(s, Attack(s, s.Players[0].Hero.Id, def.Id));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(22), "weapon's on-attack heal restored 2");
        }

        [Test]
        public void Weapon_Destroyed_When_Durability_Reaches_Zero()
        {
            var s = MainPhase();
            EquipWeapon(s, 0, atk: 4, durability: 1);
            var def = Board(s, 1, Minion(FixtureCard.Id("target"), 2, 10));

            var result = Proc().Execute(s, Attack(s, s.Players[0].Hero.Id, def.Id));

            Assert.That(s.Players[0].Weapon, Is.Null, "weapon broken");
            Assert.That(result.Events.Any(e => e.Kind == GameEventKind.CardDestroyed), Is.True, "weapon-break death event");
        }

        [Test]
        public void VANGUARD_No_Retaliation_If_Target_Dies()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("vanguard"), 5, 4, Keyword.VANGUARD));
            var def = Board(s, 1, Minion(FixtureCard.Id("weak"), 3, 3));  // 3 health, attacker hits for 5 → dead

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(atk.CurrentHealth, Is.EqualTo(4), "attacker took no retaliation — VANGUARD killed first");
        }

        [Test]
        public void VANGUARD_Retaliation_Still_Fires_If_Target_Survives()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("vanguard"), 2, 6, Keyword.VANGUARD));
            var def = Board(s, 1, Minion(FixtureCard.Id("tank"), 3, 8));  // survives 2 damage (6 health left)

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(atk.CurrentHealth, Is.EqualTo(3), "attacker took 3 retaliation — target survived");
        }

        [Test]
        public void OVERWHELM_Carries_Excess_Damage_To_Enemy_Hero()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("crusher"), 7, 6, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion(FixtureCard.Id("fodder"), 1, 3)); // 3 health; 7−3 = 4 overflow to hero

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(26), "hero took 4 overflow");
        }

        [Test]
        public void Retaliation_Can_Kill_Attacker_And_Win_Check_Fires()
        {
            var s = MainPhase();
            // Give hero 1 hp; attacker minion takes lethal retaliation → hero also dies? No, hero doesn't die here.
            // Instead: attacker minion has 1 hp and attacks a 5/5 — retaliation kills attacker. Check it goes to graveyard.
            var atk = Board(s, 0, Minion(FixtureCard.Id("fragile"), 3, 1));
            var def = Board(s, 1, Minion(FixtureCard.Id("tank"), 5, 10));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[0].Board, Has.Count.EqualTo(0), "attacker died from retaliation");
            Assert.That(s.Players[0].Graveyard, Has.Count.EqualTo(1));
        }

        [Test]
        public void Attack_On_Hero_Kills_Hero_And_Sets_Outcome()
        {
            var s = MainPhase();
            // Reduce enemy hero to low health first
            s.Players[1].Hero.CurrentHealth = 3;
            var atk = Board(s, 0, Minion(FixtureCard.Id("finisher"), 5, 8));

            Proc().Execute(s, Attack(s, atk.Id, s.Players[1].Hero.Id));

            Assert.That(s.Outcome, Is.EqualTo(MatchOutcome.Player0Win));
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.GameOver));
        }

        [Test]
        public void REKINDLE_Original_Cannot_Rekindle_Again_After_Resurrection()
        {
            var s = MainPhase();
            // Place a REKINDLE minion; kill it so it resurrects; verify original.HasUsedRekindle = true
            var rekindleDef = Minion(FixtureCard.Id("rekindle"), 2, 1, Keyword.REKINDLE);
            var rekindleMinion = Board(s, 0, rekindleDef);

            // Kill it via retaliation: attack a 5/5 → rekindle minion dies, should spawn 1-hp copy
            var killer = Board(s, 1, Minion(FixtureCard.Id("big"), 5, 5));
            Proc().Execute(s, Attack(s, rekindleMinion.Id, killer.Id));

            // Original should be in graveyard with HasUsedRekindle = true
            Assert.That(rekindleMinion.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(rekindleMinion.HasUsedRekindle, Is.True, "original REKINDLE fervor consumed");

            // Board should have one copy (the REKINDLE resurrection)
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(1));
            var copy = s.Players[0].Board[0];
            Assert.That(copy.CurrentHealth, Is.EqualTo(1), "REKINDLE copy at 1 hp");
            Assert.That(copy.HasUsedRekindle, Is.True, "copy's fervor also consumed");
        }

        // ── keyword edge interactions (M2 C2 hardening — pipeline-order behavior) ──

        [Test]
        public void OVERWHELM_Exact_Lethal_Carries_Nothing()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("crusher"), 3, 6, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion(FixtureCard.Id("fodder"), 1, 3)); // health lands exactly on 0

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Board, Has.Count.EqualTo(0), "defender died");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30), "no overflow on exact lethal");
        }

        [Test]
        public void OVERWHELM_Blocked_By_Aegis_No_Overflow()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("crusher"), 7, 6, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion(FixtureCard.Id("shielded"), 1, 3, Keyword.AEGIS));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(def.HasAegisShield, Is.False, "shield consumed by the hit");
            Assert.That(def.CurrentHealth, Is.EqualTo(3), "no health lost behind the shield");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30), "absorbed hit produces no overflow");
            Assert.That(atk.CurrentHealth, Is.EqualTo(5), "defender survived, so retaliation fired");
        }

        [Test]
        public void Aegis_Absorb_Skips_VENOM_Rider()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("viper"), 1, 4, Keyword.VENOM));
            var def = Board(s, 1, Minion(FixtureCard.Id("shielded"), 2, 5, Keyword.AEGIS));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(def.HasAegisShield, Is.False, "shield consumed");
            Assert.That(def.CurrentHealth, Is.EqualTo(5), "Venom never applied — absorb returns before riders");
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "defender alive");
        }

        [Test]
        public void VENOM_Kills_Regardless_Of_Health()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("viper"), 1, 1, Keyword.VENOM));
            var def = Board(s, 1, Minion(FixtureCard.Id("giant"), 10, 10));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Board, Has.Count.EqualTo(0), "Venom lethality — 1 damage killed a 10-hp minion");
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(0), "retaliation still killed the fragile attacker");
        }

        [Test]
        public void LIFEDRAIN_Plus_OVERWHELM_Heals_Primary_Hit_Only()
        {
            // G0 ruling R-2(c), Sam 2026-07-14: overflow carries damage, not riders.
            var s = MainPhase();
            s.Players[0].Hero.CurrentHealth = 10;
            var atk = Board(s, 0, Minion(FixtureCard.Id("drain-crusher"), 7, 6, Keyword.LIFEDRAIN, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion(FixtureCard.Id("fodder"), 1, 3)); // 7−3 = 4 overflow to enemy hero

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(26), "overflow damage still carries");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(17), "healed 7 (primary hit only), not 11");
        }

        [Test]
        public void FROST_Rider_Frosts_The_Minion_It_Damages()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("rimefang"), 3, 6, Keyword.FROST));
            var def = Board(s, 1, Minion(FixtureCard.Id("tank"), 1, 10));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(def.CurrentHealth, Is.EqualTo(7), "damage still applied normally");
            Assert.That(def.IsFrosted(), Is.True, "Frost rider froze what it damaged");
            Assert.That(atk.IsFrosted(), Is.False, "the attacker does not frost itself");
        }

        [Test]
        public void FROST_Rider_Frosts_A_Hero_Too()
        {
            // Unlike Venom (minion-only), Frost applies to heroes — it denies a weapon swing,
            // and the FROST action already treats hero and minion identically.
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("rimefang"), 3, 6, Keyword.FROST));

            Proc().Execute(s, Attack(s, atk.Id, s.Players[1].Hero.Id));

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27), "damage still applied");
            Assert.That(s.Players[1].Hero.IsFrosted(), Is.True, "Frost rider froze the hero it damaged");
        }

        [Test]
        public void Aegis_Absorb_Skips_FROST_Rider()
        {
            // Same precedent as Aegis_Absorb_Skips_VENOM_Rider — G0 R-2(c), no on-hit riders.
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("rimefang"), 3, 6, Keyword.FROST));
            var def = Board(s, 1, Minion(FixtureCard.Id("shielded"), 2, 5, Keyword.AEGIS));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(def.HasAegisShield, Is.False, "shield consumed");
            Assert.That(def.CurrentHealth, Is.EqualTo(5), "no health lost behind the shield");
            Assert.That(def.IsFrosted(), Is.False, "Frost never applied — absorb returns before riders");
        }

        [Test]
        public void FROST_Plus_OVERWHELM_Does_Not_Frost_The_Face()
        {
            // G0 ruling R-2(c), Sam 2026-07-14: overflow carries damage, not riders.
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("rime-crusher"), 7, 6, Keyword.FROST, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion(FixtureCard.Id("fodder"), 1, 3)); // 7−3 = 4 overflow to enemy hero

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(26), "overflow damage still carries");
            Assert.That(s.Players[1].Hero.IsFrosted(), Is.False, "overflow must not carry the Frost rider");
        }

        [Test]
        public void No_FROST_Keyword_Means_No_Frost()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion(FixtureCard.Id("plain"), 3, 6));
            var def = Board(s, 1, Minion(FixtureCard.Id("tank"), 1, 10));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(def.IsFrosted(), Is.False, "ordinary damage does not frost");
        }

        [Test]
        public void LIFEDRAIN_Attack_Heals_Controller_Hero()
        {
            var s = MainPhase();
            s.Players[0].Hero.CurrentHealth = 20;
            var atk = Board(s, 0, Minion(FixtureCard.Id("drainer"), 4, 6, Keyword.LIFEDRAIN));
            var def = Board(s, 1, Minion(FixtureCard.Id("tank"), 1, 10));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(24), "healed for damage dealt");
        }

        [Test]
        public void Armor_Soaks_Before_Health()
        {
            var s = MainPhase();
            s.Players[1].Hero.Armor = 3;
            var atk = Board(s, 0, Minion(FixtureCard.Id("basher"), 5, 5));

            Proc().Execute(s, Attack(s, atk.Id, s.Players[1].Hero.Id));

            Assert.That(s.Players[1].Hero.Armor, Is.EqualTo(0), "armor consumed first");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(28), "only the remainder reached health");
        }

        [Test]
        public void Rejected_Attack_Spends_No_Durability_And_No_Attack_Budget()
        {
            var s = MainPhase();
            EquipWeapon(s, 0, atk: 4, durability: 2);
            var shroudy = Board(s, 1, Minion(FixtureCard.Id("ghost"), 2, 3, Keyword.SHROUD));

            var r = Proc().Execute(s, Attack(s, s.Players[0].Hero.Id, shroudy.Id));

            Assert.That(r.Accepted, Is.False, "shroud target is illegal");
            Assert.That(s.Players[0].Weapon!.CurrentDurability, Is.EqualTo(2), "durability untouched");
            Assert.That(s.Players[0].HeroAttacksUsedThisTurn, Is.EqualTo(0), "attack budget untouched");
        }
    }
}
