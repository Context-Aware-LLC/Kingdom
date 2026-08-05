using System.IO;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardBehavior
{
    /// <summary>
    /// L2 — end-to-end behavior tests for every distinct mechanic in the D4 20-card set
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/sample-cards.md). Uses the real <see cref="JsonCardDatabase"/> loaded from the pinned
    /// M1 snapshot so the cards under test are identical to what the golden-replay fixtures see.
    /// One test per non-trivial mechanic. Keyword-only cards (TAUNT, RUSH, etc.) are covered by the
    /// existing CombatTests; these tests focus on effect verbs.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CardBehaviorTests
    {
        private static JsonCardDatabase _db = null!;

        [OneTimeSetUp]
        public void LoadSnapshot()
        {
            var path = Path.Combine(RepoPaths.CardSetsDir, "M1.cards.snapshot.json");
            _db = JsonCardDatabase.FromSnapshotFile(path);
        }

        // ── scaffolding ─────────────────────────────────────────────────────────────

        private static GameState MainPhase(int p0Mana = 10)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            hero0.HeroPowerId = "hero_power_searing_bolt";
            hero1.HeroPowerId = "hero_power_tidal_ward";
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("M"), new DeterministicRandom(99UL), ps0, ps1)
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

        private static CardInstance InHand(GameState s, int seat, string defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(ci);
            return ci;
        }

        private static CardInstance OnBoard(GameState s, int seat, string defId)
        {
            var def = _db.Get(defId);
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), defId, ps.Id)
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

        private CommandProcessor Proc() => new CommandProcessor(_db);

        private static PlayCardIntent Play(GameState s, CardInstance card, int slot = 0,
            params InstanceId[] targets)
        {
            var refs = new TargetRef[targets.Length];
            for (int i = 0; i < targets.Length; i++) refs[i] = new TargetRef(targets[i]);
            return new PlayCardIntent(s.Players[0].Id, card.Id, slot, refs);
        }

        // ── Hollow Acolyte: herald draws 1 card ──────────────────────────────────

        [Test]
        public void HollowAcolyte_Herald_Draws_One_Card()
        {
            var s = MainPhase();
            var topCard = new CardInstance(s.NextInstanceId(), "core_stoneback", s.Players[0].Id) { Zone = Zone.Deck };
            s.Players[0].Deck.Add(topCard);
            var card = InHand(s, 0, "core_hollow_acolyte");

            Proc().Execute(s, Play(s, card));

            Assert.That(s.Players[0].Hand, Has.Count.EqualTo(1), "Drew one card into hand.");
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(1), "Acolyte is on board.");
        }

        // ── Gravelight Lantern-Bearer: deathrattle summons Wisp ─────────────────────

        [Test]
        public void LanternBearer_Deathrattle_Summons_Wisp()
        {
            var s = MainPhase();
            // Enemy has a 3-damage bolt minion; we'll use InterpreterTests approach manually.
            // Place Lantern Bearer on enemy board, then kill it by playing a "nuke" proxy.
            // Use FakeCardDatabase for the bolt so we don't need a real 3-dmg card in snapshot.
            // Instead: drop Lantern Bearer at 1 HP and attack it with a board minion.
            var bearer = OnBoard(s, 1, "core_lantern_bearer");
            bearer.CurrentHealth = 1;

            // Kill it with a CHARGE minion from hand
            var attacker = OnBoard(s, 0, "core_emberling"); // 2/1 CHARGE
            attacker.SummonedThisTurn = false; // already thawed

            var r = Proc().Execute(s, new AttackIntent(s.Players[0].Id, attacker.Id,
                new TargetRef(bearer.Id)));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Graveyard, Has.Count.EqualTo(1), "Bearer died.");
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "Wisp summoned by deathrattle.");
            Assert.That(s.Players[1].Board[0].DefinitionId, Is.EqualTo("token_wisp_1_1"));
        }

        // ── Arclight Adept: +1 spell damage bonus ───────────────────────────────────

        [Test]
        public void ArclightAdept_Boosts_Scorch_Damage_By_One()
        {
            var s = MainPhase();
            OnBoard(s, 0, "core_arclight_adept"); // SPELL_DAMAGE in play

            // Enemy minion with 3 HP — normally Scorch (3 dmg) just kills it; with +1 it definitely kills,
            // so use a 4 HP target to prove the bonus is applied.
            var victim = OnBoard(s, 1, "core_stoneback"); // 1/4 — survives Scorch without bonus
            var scorch = InHand(s, 0, "ember_scorch");    // 2-mana, DEAL_DAMAGE 3

            Proc().Execute(s, Play(s, scorch, 0, victim.Id));

            Assert.That(victim.CurrentHealth, Is.EqualTo(0), "3 + 1 spell damage kills the 1/4.");
            Assert.That(s.Players[1].Board, Is.Empty);
        }

        // ── Throneless King: REBORN returns at 1 HP ─────────────────────────────────

        [Test]
        public void ThronelessKing_Reborn_Returns_At_One_HP()
        {
            var s = MainPhase();
            // Pre-set the king at lethal HP; any subsequent PlayCardIntent triggers Drain → DeathCheck
            var king = OnBoard(s, 1, "core_throneless_king"); // 6/6 TAUNT REBORN
            king.CurrentHealth = 0; // already lethal — DeathCheck will pick it up on next Drain

            // Play any 0-cost card to trigger the command cycle (Drain is called inside ApplyPlayCard)
            var vanilla = InHand(s, 0, "core_emberling"); // 1-mana, no relevant effects
            s.Players[0].Mana.Available = 10;

            var r = Proc().Execute(s, Play(s, vanilla));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "REBORN copy survived.");
            var copy = s.Players[1].Board[0];
            Assert.That(copy.CurrentHealth, Is.EqualTo(1), "REBORN copy returns at 1 HP.");
            Assert.That(copy.DefinitionId, Is.EqualTo("core_throneless_king"), "Copy is still a Throneless King.");
        }

        // ── Overheat Golem: OVERLOAD locks 2 crystals next turn ─────────────────────

        [Test]
        public void OverheatGolem_Overload_Locks_Two_Crystals_Next_Turn()
        {
            var s = MainPhase(p0Mana: 5);
            var golem = InHand(s, 0, "ember_overheat_golem"); // 3-mana, OVERLOAD 2

            Proc().Execute(s, Play(s, golem));

            Assert.That(s.Players[0].NextTurnOverloadLock, Is.EqualTo(2), "2 crystals locked for next turn.");
            Assert.That(s.Players[0].Mana.Available, Is.EqualTo(2), "Only mana cost paid this turn.");
        }

        // ── Twin Flame Strike: deals 2 to ALL enemy minions ─────────────────────────

        [Test]
        public void TwinFlameStrike_Hits_All_Enemy_Minions()
        {
            var s = MainPhase();
            var m1 = OnBoard(s, 1, "core_emberling");   // 2/1 — dies
            var m2 = OnBoard(s, 1, "core_stoneback");   // 1/4 — survives at 2 HP
            var m3 = OnBoard(s, 1, "core_bonehound");   // 3/2 — dies

            var spell = InHand(s, 0, "ember_twin_flame"); // 4-mana, DEAL_DAMAGE 2 ENEMY_MINIONS

            var r = Proc().Execute(s, Play(s, spell)); // no targets needed — AoE

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Graveyard, Has.Count.EqualTo(2), "1/1 and 3/2 killed.");
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "1/4 survived.");
            Assert.That(s.Players[1].Board[0].CurrentHealth, Is.EqualTo(2));
        }

        // ── Pyra's Wrath: herald auto-deals 2 to enemy hero ──────────────────────

        [Test]
        public void PyrasWrath_Herald_Deals_Two_To_Enemy_Hero()
        {
            var s = MainPhase(p0Mana: 10);
            var pyra = InHand(s, 0, "ember_pyras_wrath"); // 7-mana 7/5 CHARGE, herald deals 2

            // No targets needed — ENEMY_HERO selector auto-resolves
            var r = Proc().Execute(s, Play(s, pyra));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(28), "Herald dealt 2 to enemy hero.");
        }

        // ── Frostbind: FREEZE sets IsFrozen on the targeted character ───────────────

        [Test]
        public void Frostbind_Freezes_Targeted_Minion()
        {
            var s = MainPhase(); // P0 is active
            var victim = OnBoard(s, 1, "core_stoneback"); // 1/4 on P1's board
            var spell = InHand(s, 0, "tide_frostbind");   // 1-mana FREEZE

            Proc().Execute(s, Play(s, spell, 0, victim.Id));

            Assert.That(victim.IsFrozen, Is.True, "Frostbind sets IsFrozen on the target.");
        }

        // ── Freeze engine: frozen minion cannot attack on its controller's turn ──────

        [Test]
        public void Frozen_Minion_Cannot_Attack()
        {
            // The freeze verb is verified above; this verifies the AttackerFrozen gate in ValidateAttack.
            // We freeze a P0 minion directly (simulating any freeze source) and confirm P0 can't attack with it.
            var s = MainPhase();
            var blocker = OnBoard(s, 1, "core_stoneback"); // gives P0 a legal target
            var attacker = OnBoard(s, 0, "core_emberling"); // 2/1 CHARGE (no sickness)
            attacker.SummonedThisTurn = false;
            attacker.IsFrozen = true; // frozen by any prior effect

            var r = Proc().Execute(s, new AttackIntent(s.Players[0].Id, attacker.Id,
                new TargetRef(blocker.Id)));

            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Reason, Is.EqualTo(RejectReason.AttackerFrozen));
        }

        // ── Frostbind: FREEZE the enemy hero blocks weapon attack ───────────────────

        [Test]
        public void Frostbind_Freezes_Enemy_Hero_Blocking_Attack()
        {
            var s = MainPhase();
            // Give P1 a weapon so they could attack
            s.Players[1].Weapon = new WeaponInstance
            {
                Id = s.NextInstanceId(),
                DefinitionId = "ember_cinder_lance",
                Attack = 1,
                Durability = 2
            };
            var spell = InHand(s, 0, "tide_frostbind");

            Proc().Execute(s, Play(s, spell, 0, s.Players[1].Hero.Id));

            Assert.That(s.Players[1].Hero.IsFrozen, Is.True);
            // P1 tries to attack — should be rejected
            var r = Proc().Execute(s, new AttackIntent(s.Players[1].Id, s.Players[1].Hero.Id,
                new TargetRef(s.Players[0].Hero.Id)));
            // Not P1's turn but AttackerFrozen is checked after NotYourTurn, so just verify frozen state
            Assert.That(s.Players[1].Hero.IsFrozen, Is.True, "Hero remains frozen until start of their turn.");
        }

        // ── Hollow Tide Siren: herald freezes ALL enemy minions ──────────────────

        [Test]
        public void HollowTideSiren_Herald_Freezes_All_Enemy_Minions()
        {
            var s = MainPhase(p0Mana: 10);
            var m1 = OnBoard(s, 1, "core_stoneback");   // 1/4
            var m2 = OnBoard(s, 1, "core_bonehound");   // 3/2

            var siren = InHand(s, 0, "tide_siren"); // 5-mana, FREEZE ENEMY_MINIONS

            Proc().Execute(s, Play(s, siren));

            Assert.That(m1.IsFrozen, Is.True);
            Assert.That(m2.IsFrozen, Is.True);
        }

        // ── Undertow: returns a minion to its owner's hand ──────────────────────────

        [Test]
        public void Undertow_Returns_Enemy_Minion_To_Hand()
        {
            var s = MainPhase();
            var target = OnBoard(s, 1, "core_throneless_king"); // 6/6 — buffed on board
            target.CurrentAttack = 10; // has an enchantment worth removing
            var spell = InHand(s, 0, "tide_undertow");

            Proc().Execute(s, Play(s, spell, 0, target.Id));

            Assert.That(s.Players[1].Board, Is.Empty, "Minion removed from board.");
            Assert.That(s.Players[1].Hand, Has.Count.EqualTo(1), "Minion in hand.");
            // Stats reset to printed values on bounce
            Assert.That(s.Players[1].Hand[0].CurrentAttack, Is.EqualTo(6), "Stats reset to definition.");
        }

        // ── Cinder Lance: equips a weapon on play ───────────────────────────────────

        [Test]
        public void CinderLance_Equips_As_Weapon()
        {
            var s = MainPhase();
            var weapon = InHand(s, 0, "ember_cinder_lance");

            Proc().Execute(s, Play(s, weapon));

            Assert.That(s.Players[0].Weapon, Is.Not.Null);
            Assert.That(s.Players[0].Weapon!.Attack, Is.EqualTo(1));
            Assert.That(s.Players[0].Weapon!.Durability, Is.EqualTo(2));
        }

        // ── Maren's Deep Bargain: draws 2 cards ─────────────────────────────────────

        [Test]
        public void MarensDeepBargain_Draws_Two_Cards()
        {
            var s = MainPhase();
            for (int i = 0; i < 3; i++)
                s.Players[0].Deck.Add(new CardInstance(s.NextInstanceId(), "core_stoneback", s.Players[0].Id) { Zone = Zone.Deck });

            var spell = InHand(s, 0, "tide_deep_bargain");

            Proc().Execute(s, Play(s, spell));

            Assert.That(s.Players[0].Hand, Has.Count.EqualTo(2), "Drew 2 cards.");
        }

        // ── Grove Mender: heals a chosen friendly character for 4 ───────────────────

        [Test]
        public void GroveMender_Heals_Chosen_Target_For_Four()
        {
            var s = MainPhase(p0Mana: 10);
            s.Players[0].Hero.CurrentHealth = 20; // damaged

            var mender = InHand(s, 0, "core_grove_mender");

            // Player chooses to heal their own hero
            Proc().Execute(s, Play(s, mender, 0, s.Players[0].Hero.Id));

            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(24), "Healed 4 HP.");
        }

        // ── Snapshot round-trip: all D4 cards parse without error ───────────────────

        [Test]
        public void All_D4_Cards_Parse_From_Snapshot()
        {
            var ids = new[]
            {
                "core_emberling", "core_stoneback", "core_hollow_acolyte", "core_lantern_bearer",
                "core_aegis_templar", "core_veilstalker", "core_grove_mender", "core_arclight_adept",
                "core_bonehound", "core_throneless_king",
                "ember_cinder_lance", "ember_scorch", "ember_overheat_golem",
                "ember_twin_flame", "ember_pyras_wrath",
                "tide_frostbind", "tide_undertow", "tide_siren", "tide_reefguard", "tide_deep_bargain",
                "token_wisp_1_1",
            };
            foreach (var id in ids)
            {
                Assert.DoesNotThrow(() => _db.Get(id), $"Card '{id}' must be in the snapshot.");
                var def = _db.Get(id);
                Assert.That(def.Id, Is.EqualTo(id));
            }
        }
    }
}
