using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core.Tests.Combat
{
    /// <summary>
    /// L1 unit tests for M1 Phase B step 2: Attack command — legality checks, mutual damage,
    /// weapon combat, VANGUARD, OVERWHELM, STEALTH/TAUNT enforcement.
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
            if (ci.Keywords.Contains(Keyword.DIVINE_SHIELD)) ci.HasAegisShield = true;
            ps.Board.Add(ci);
            return ci;
        }

        private static void EquipWeapon(GameState s, int seat, int atk, int durability)
        {
            s.Players[seat].Weapon = new WeaponInstance
            {
                Id = s.NextInstanceId(),
                DefinitionId = "weapon",
                Attack = atk,
                Durability = durability,
            };
        }

        private static CardDefinition Minion(string id, int atk, int hp, params Keyword[] kws)
            => new CardDefinition(id, id, CardType.MINION, 1, atk, hp, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                kws, System.Array.Empty<EffectDef>(),
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static AttackIntent Attack(GameState s, InstanceId attacker, InstanceId defender)
            => new AttackIntent(s.ActivePlayer, attacker, new TargetRef(defender));

        private static CommandProcessor Proc() => new CommandProcessor(new Support.FakeCardDatabase());

        // ── legality ─────────────────────────────────────────────────────────────

        [Test]
        public void Summoning_Sickness_Blocks_Attack()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("vanilla", 3, 4), summonedThisTurn: true);
            var def = Board(s, 1, Minion("target", 2, 3));

            var r = Proc().CanExecute(s, Attack(s, atk.Id, def.Id), out var reason);

            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.HasSummoningSickness));
        }

        [Test]
        public void CHARGE_Bypasses_Summoning_Sickness()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("charger", 3, 4, Keyword.CHARGE), summonedThisTurn: true);
            var def = Board(s, 1, Minion("target", 2, 3));

            var r = Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(r.Accepted, Is.True);
            Assert.That(def.CurrentHealth, Is.EqualTo(0), "defender took 3 damage");
        }

        // Regression: REBORN must trigger on a COMBAT death, not only on effect/Drain death.
        // The existing ThronelessKing_Reborn_Returns_At_One_HP kills via effect damage; a live
        // networked match killed the king in combat and (reported) no 1-HP copy returned. This
        // exercises the combat path (ApplyAttack → Drain → DeathCheck → Resummon).
        [Test]
        public void REBORN_Triggers_On_Combat_Death()
        {
            var s = MainPhase();
            var attacker = Board(s, 0, Minion("bruiser", 6, 8));          // survives retaliation
            var king = Board(s, 1, Minion("core_throneless_king", 6, 6, Keyword.TAUNT, Keyword.REBORN));

            var r = Proc().Execute(s, Attack(s, attacker.Id, king.Id));

            Assert.That(r.Accepted, Is.True, "attack resolved");
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1),
                "REBORN copy should remain on the board after a combat death");
            var copy = s.Players[1].Board[0];
            Assert.That(copy.CurrentHealth, Is.EqualTo(1), "REBORN copy returns at 1 HP");
            Assert.That(copy.DefinitionId, Is.EqualTo("core_throneless_king"), "copy is still a Throneless King");
        }

        [Test]
        public void RUSH_Can_Attack_Enemy_Minion_But_Not_Hero_On_Summon_Turn()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("rusher", 3, 4, Keyword.RUSH), summonedThisTurn: true);
            var def = Board(s, 1, Minion("target", 2, 3));

            // Can attack enemy minion
            var rMinion = Proc().CanExecute(s, Attack(s, atk.Id, def.Id), out _);
            Assert.That(rMinion, Is.True);

            // Cannot attack enemy hero
            var rHero = Proc().CanExecute(s, Attack(s, atk.Id, s.Players[1].Hero.Id), out var reason);
            Assert.That(rHero, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void Frozen_Minion_Cannot_Attack()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("frozen", 3, 4));
            atk.IsFrozen = true;
            var def = Board(s, 1, Minion("target", 2, 3));

            var r = Proc().CanExecute(s, Attack(s, atk.Id, def.Id), out var reason);

            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.AttackerFrozen));
        }

        [Test]
        public void WINDFURY_Allows_Two_Attacks_Third_Rejected()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("windfury", 2, 8, Keyword.WINDFURY));
            var def = Board(s, 1, Minion("target", 1, 20));

            var p = Proc();
            Assert.That(p.Execute(s, Attack(s, atk.Id, def.Id)).Accepted, Is.True, "first attack");
            Assert.That(p.Execute(s, Attack(s, atk.Id, def.Id)).Accepted, Is.True, "second attack");

            var r = p.CanExecute(s, Attack(s, atk.Id, def.Id), out var reason);
            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.NoAttacksLeft));
        }

        [Test]
        public void TAUNT_Enforces_Must_Attack_Taunter()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("attacker", 3, 4));
            var taunter = Board(s, 1, Minion("taunter", 1, 5, Keyword.TAUNT));
            var freeTarget = Board(s, 1, Minion("free", 2, 3));

            // Must attack the taunter
            Assert.That(Proc().CanExecute(s, Attack(s, atk.Id, taunter.Id), out _), Is.True);

            // Cannot skip the taunter
            var r = Proc().CanExecute(s, Attack(s, atk.Id, freeTarget.Id), out var reason);
            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        [Test]
        public void STEALTH_Blocks_Targeting_And_Breaks_When_Attacker_Swings()
        {
            var s = MainPhase();
            var stealthy = Board(s, 1, Minion("ghost", 2, 3, Keyword.STEALTH));
            var atk = Board(s, 0, Minion("attacker", 3, 4));

            // Cannot target stealthed enemy
            var r = Proc().CanExecute(s, Attack(s, atk.Id, stealthy.Id), out var reason);
            Assert.That(r, Is.False);
            Assert.That(reason, Is.EqualTo(RejectReason.IllegalTarget));

            // But after the stealth minion itself attacks, it loses stealth
            stealthy.Keywords.Remove(Keyword.STEALTH); // manually break for this check
            Assert.That(Proc().CanExecute(s, Attack(s, atk.Id, stealthy.Id), out _), Is.True);
        }

        [Test]
        public void STEALTH_Breaks_When_Stealthy_Minion_Attacks()
        {
            var s = MainPhase();
            var ghost = Board(s, 0, Minion("ghost", 2, 5, Keyword.STEALTH));
            var target = Board(s, 1, Minion("target", 1, 10));

            Assert.That(ghost.Keywords.Contains(Keyword.STEALTH), Is.True, "has stealth before attack");
            Proc().Execute(s, Attack(s, ghost.Id, target.Id));
            Assert.That(ghost.Keywords.Contains(Keyword.STEALTH), Is.False, "stealth removed on attack");
        }

        [Test]
        public void No_Weapon_Means_Hero_Cannot_Attack()
        {
            var s = MainPhase();
            var def = Board(s, 1, Minion("target", 2, 3));
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
            var atk = Board(s, 0, Minion("a", 3, 5));
            var def = Board(s, 1, Minion("d", 2, 4));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(atk.CurrentHealth, Is.EqualTo(3), "attacker took 2 retaliation");
            Assert.That(def.CurrentHealth, Is.EqualTo(1), "defender took 3 damage");
        }

        [Test]
        public void Hero_Weapon_Attack_Deals_Damage_And_Decrements_Durability()
        {
            var s = MainPhase();
            EquipWeapon(s, 0, atk: 4, durability: 2);
            var def = Board(s, 1, Minion("target", 2, 6));

            Proc().Execute(s, Attack(s, s.Players[0].Hero.Id, def.Id));

            Assert.That(def.CurrentHealth, Is.EqualTo(2), "hero dealt 4 damage");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(28), "hero took 2 retaliation");
            Assert.That(s.Players[0].Weapon, Is.Not.Null, "weapon still alive");
            Assert.That(s.Players[0].Weapon!.Durability, Is.EqualTo(1), "durability decremented");
        }

        [Test]
        public void Weapon_Destroyed_When_Durability_Reaches_Zero()
        {
            var s = MainPhase();
            EquipWeapon(s, 0, atk: 4, durability: 1);
            var def = Board(s, 1, Minion("target", 2, 10));

            var result = Proc().Execute(s, Attack(s, s.Players[0].Hero.Id, def.Id));

            Assert.That(s.Players[0].Weapon, Is.Null, "weapon broken");
            Assert.That(result.Events.Any(e => e.Kind == VfxEvent.onDeath), Is.True, "weapon-break death event");
        }

        [Test]
        public void VANGUARD_No_Retaliation_If_Target_Dies()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("vanguard", 5, 4, Keyword.VANGUARD));
            var def = Board(s, 1, Minion("weak", 3, 3));  // 3 health, attacker hits for 5 → dead

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(atk.CurrentHealth, Is.EqualTo(4), "attacker took no retaliation — VANGUARD killed first");
        }

        [Test]
        public void VANGUARD_Retaliation_Still_Fires_If_Target_Survives()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("vanguard", 2, 6, Keyword.VANGUARD));
            var def = Board(s, 1, Minion("tank", 3, 8));  // survives 2 damage (6 health left)

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(atk.CurrentHealth, Is.EqualTo(3), "attacker took 3 retaliation — target survived");
        }

        [Test]
        public void OVERWHELM_Carries_Excess_Damage_To_Enemy_Hero()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("crusher", 7, 6, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion("fodder", 1, 3)); // 3 health; 7−3 = 4 overflow to hero

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(26), "hero took 4 overflow");
        }

        [Test]
        public void Retaliation_Can_Kill_Attacker_And_Win_Check_Fires()
        {
            var s = MainPhase();
            // Give hero 1 hp; attacker minion takes lethal retaliation → hero also dies? No, hero doesn't die here.
            // Instead: attacker minion has 1 hp and attacks a 5/5 — retaliation kills attacker. Check it goes to graveyard.
            var atk = Board(s, 0, Minion("fragile", 3, 1));
            var def = Board(s, 1, Minion("tank", 5, 10));

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
            var atk = Board(s, 0, Minion("finisher", 5, 8));

            Proc().Execute(s, Attack(s, atk.Id, s.Players[1].Hero.Id));

            Assert.That(s.Outcome, Is.EqualTo(MatchOutcome.Player0Win));
            Assert.That(s.Phase, Is.EqualTo(TurnPhase.GameOver));
        }

        [Test]
        public void REBORN_Original_Cannot_Reborn_Again_After_Resurrection()
        {
            var s = MainPhase();
            // Place a REBORN minion; kill it so it resurrects; verify original.HasUsedReborn = true
            var rebornDef = Minion("reborn", 2, 1, Keyword.REBORN);
            var rebornMinion = Board(s, 0, rebornDef);

            // Kill it via retaliation: attack a 5/5 → reborn minion dies, should spawn 1-hp copy
            var killer = Board(s, 1, Minion("big", 5, 5));
            Proc().Execute(s, Attack(s, rebornMinion.Id, killer.Id));

            // Original should be in graveyard with HasUsedReborn = true
            Assert.That(rebornMinion.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(rebornMinion.HasUsedReborn, Is.True, "original REBORN charge consumed");

            // Board should have one copy (the REBORN resurrection)
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(1));
            var copy = s.Players[0].Board[0];
            Assert.That(copy.CurrentHealth, Is.EqualTo(1), "REBORN copy at 1 hp");
            Assert.That(copy.HasUsedReborn, Is.True, "copy's charge also consumed");
        }

        // ── keyword edge interactions (M2 C2 hardening — pipeline-order behavior) ──

        [Test]
        public void OVERWHELM_Exact_Lethal_Carries_Nothing()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("crusher", 3, 6, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion("fodder", 1, 3)); // health lands exactly on 0

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Board, Has.Count.EqualTo(0), "defender died");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30), "no overflow on exact lethal");
        }

        [Test]
        public void OVERWHELM_Blocked_By_Aegis_No_Overflow()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("crusher", 7, 6, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion("shielded", 1, 3, Keyword.DIVINE_SHIELD));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(def.HasAegisShield, Is.False, "shield consumed by the hit");
            Assert.That(def.CurrentHealth, Is.EqualTo(3), "no health lost behind the shield");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30), "absorbed hit produces no overflow");
            Assert.That(atk.CurrentHealth, Is.EqualTo(5), "defender survived, so retaliation fired");
        }

        [Test]
        public void Aegis_Absorb_Skips_POISONOUS_Rider()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("viper", 1, 4, Keyword.POISONOUS));
            var def = Board(s, 1, Minion("shielded", 2, 5, Keyword.DIVINE_SHIELD));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(def.HasAegisShield, Is.False, "shield consumed");
            Assert.That(def.CurrentHealth, Is.EqualTo(5), "Venom never applied — absorb returns before riders");
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "defender alive");
        }

        [Test]
        public void POISONOUS_Kills_Regardless_Of_Health()
        {
            var s = MainPhase();
            var atk = Board(s, 0, Minion("viper", 1, 1, Keyword.POISONOUS));
            var def = Board(s, 1, Minion("giant", 10, 10));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Board, Has.Count.EqualTo(0), "Venom lethality — 1 damage killed a 10-hp minion");
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(0), "retaliation still killed the fragile attacker");
        }

        [Test]
        public void LIFESTEAL_Plus_OVERWHELM_Heals_Primary_Hit_Only()
        {
            // G0 ruling R-2(c), Sam 2026-07-14: overflow carries damage, not riders.
            var s = MainPhase();
            s.Players[0].Hero.CurrentHealth = 10;
            var atk = Board(s, 0, Minion("drain-crusher", 7, 6, Keyword.LIFESTEAL, Keyword.OVERWHELM));
            var def = Board(s, 1, Minion("fodder", 1, 3)); // 7−3 = 4 overflow to enemy hero

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(26), "overflow damage still carries");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(17), "healed 7 (primary hit only), not 11");
        }

        [Test]
        public void LIFESTEAL_Attack_Heals_Controller_Hero()
        {
            var s = MainPhase();
            s.Players[0].Hero.CurrentHealth = 20;
            var atk = Board(s, 0, Minion("drainer", 4, 6, Keyword.LIFESTEAL));
            var def = Board(s, 1, Minion("tank", 1, 10));

            Proc().Execute(s, Attack(s, atk.Id, def.Id));

            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(24), "healed for damage dealt");
        }

        [Test]
        public void Armor_Soaks_Before_Health()
        {
            var s = MainPhase();
            s.Players[1].Hero.Armor = 3;
            var atk = Board(s, 0, Minion("basher", 5, 5));

            Proc().Execute(s, Attack(s, atk.Id, s.Players[1].Hero.Id));

            Assert.That(s.Players[1].Hero.Armor, Is.EqualTo(0), "armor consumed first");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(28), "only the remainder reached health");
        }

        [Test]
        public void Rejected_Attack_Spends_No_Durability_And_No_Attack_Budget()
        {
            var s = MainPhase();
            EquipWeapon(s, 0, atk: 4, durability: 2);
            var stealthy = Board(s, 1, Minion("ghost", 2, 3, Keyword.STEALTH));

            var r = Proc().Execute(s, Attack(s, s.Players[0].Hero.Id, stealthy.Id));

            Assert.That(r.Accepted, Is.False, "stealth target is illegal");
            Assert.That(s.Players[0].Weapon!.Durability, Is.EqualTo(2), "durability untouched");
            Assert.That(s.Players[0].HeroAttacksUsedThisTurn, Is.EqualTo(0), "attack budget untouched");
        }
    }
}
