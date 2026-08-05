using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using Verb = Emberfall.Core.Contracts.Action;

namespace Emberfall.Core.Tests.Combat
{
    /// <summary>
    /// L1 — EF-020. <c>RulesEngine.BreakWeapon</c> emitted an <c>onDeath</c> VFX event and raised no trigger
    /// at all, so a Toll on a WEAPON was structurally impossible: it parsed, validated, loaded and
    /// then did nothing forever, silently. (<c>CardValidator</c> rule C21 rejected the card outright to stop
    /// anyone authoring one — that gate is retired by the same change these tests cover.)
    ///
    /// <para>A weapon leaves play by TWO doors and the fix is only real if it covers both:
    /// <b>durability exhaustion</b> in combat (<c>CommandProcessor.ApplyAttack</c>) and
    /// <b>equip-replacement</b> (<c>CommandProcessor.ApplyPlayCard</c>, and its effect-driven twin
    /// <c>EquipHandler</c>). One raise site in <c>BreakWeapon</c> serves all three because every caller
    /// already holds the interpreter.</para>
    ///
    /// <para>The third test pins the ORDERING decision, which is the part of this that is not obvious: a
    /// weapon rattle ENQUEUES like a minion's rather than resolving inline, so on a replacement the NEW
    /// weapon is already in the slot when the OLD one's rattle runs.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class WeaponTollTests
    {
        private const string Rattler = "ef020_rattler";   // 2/1 weapon, Toll: 3 damage to the enemy hero
        private const string Plain = "ef020_plain";       // 3/2 weapon, no effects — the replacement
        private const string Chainer = "ef020_chainer";   // 2/1 weapon, Toll: equip ef020_plain
        private const string Successor = "ef020_success"; // 4/2 weapon, no effects — what Chainer equips

        private static CardDefinition Weapon(string id, int atk, int durability, params EffectDef[] effects)
            => new CardDefinition(
                id: id, name: id, type: CardType.WEAPON, cost: 1,
                attack: atk, health: null, durability: durability,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: effects,
                cardText: "", flavorText: "", artId: "art/" + id, setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        private static CardDefinition Minion(string id, int atk, int hp)
            => new CardDefinition(
                id: id, name: id, type: CardType.MINION, cost: 1,
                attack: atk, health: hp, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: Array.Empty<EffectDef>(),
                cardText: "", flavorText: "", artId: "art/" + id, setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        /// <summary>Toll: deal <paramref name="dmg"/> to the enemy hero — observable from either door.</summary>
        private static EffectDef Rattle(int dmg)
            => new EffectDef(Trigger.ON_DEATH, Verb.DEAL_DAMAGE, amount: dmg, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null, selector: "ENEMY_HERO");

        private static FakeCardDatabase Db() => new FakeCardDatabase()
            .Register(Weapon(Rattler, atk: 2, durability: 1, Rattle(3)))
            .Register(Weapon(Plain, atk: 3, durability: 2))
            .Register(Weapon(Successor, atk: 4, durability: 2))
            .Register(Weapon(Chainer, atk: 2, durability: 1,
                new EffectDef(Trigger.ON_DEATH, Verb.EQUIP, amount: null, amount2: null,
                    tokenId: Successor, grantedKeyword: null, vfxOverride: null)))
            .Register(Minion("ef020_dummy", 0, 10));

        private static GameState MainPhase()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("EF020"), new DeterministicRandom(20UL), ps0, ps1)
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

        private static void Equip(GameState s, int seat, CardDefinition def)
            => s.Players[seat].Weapon = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id)
            {
                Zone = Zone.Weapon,
                CurrentAttack = def.Attack ?? 0,
                CurrentDurability = def.Durability ?? 1,
            };

        private static CardInstance ToHand(GameState s, int seat, CardDefinition def)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id) { Zone = Zone.Hand };
            ps.Hand.Add(ci);
            return ci;
        }

        private static CardInstance OnBoard(GameState s, int seat, CardDefinition def)
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

        [Test]
        public void Equipped_Effect_Source_Uses_Granted_Live_Keywords_Not_Only_Printed_Keywords()
        {
            const string empowered = "ef020_empowered";
            var def = Weapon(empowered, atk: 1, durability: 2,
                new EffectDef(Trigger.ON_ATTACK, Verb.DEAL_DAMAGE, amount: 2, amount2: null,
                    tokenId: null, grantedKeyword: null, vfxOverride: null, selector: "ENEMY_HERO"));
            var db = new FakeCardDatabase().Register(def);
            GameState state = MainPhase();
            Equip(state, 0, def);
            CardInstance weapon = state.Players[0].Weapon!;
            weapon.Keywords.Add(Keyword.LIFEDRAIN);
            weapon.Enchantments.Add(new Enchantment
            { Source = new InstanceId(99), GrantedKeyword = Keyword.LIFEDRAIN });
            state.Players[0].Hero.CurrentHealth = 20;

            CommandResult result = new CommandProcessor(db).Execute(state, new AttackIntent(
                state.Players[0].Id, state.Players[0].Hero.Id,
                new TargetRef(state.Players[1].Hero.Id)));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Hero.CurrentHealth, Is.EqualTo(23),
                "The combat hit heals 1 and the queued weapon effect heals 2 from the live granted keyword.");
        }

        // ── Door 1: durability exhaustion ────────────────────────────────────────

        [Test]
        public void Weapon_Toll_Fires_On_Durability_Exhaustion()
        {
            var db = Db();
            var s = MainPhase();
            Equip(s, 0, db.Get(Rattler));                              // 2/1 — one swing and it breaks
            var dummy = OnBoard(s, 1, db.Get("ef020_dummy"));          // 0 attack: no retaliation to muddy the read

            var r = new CommandProcessor(db).Execute(s,
                new AttackIntent(s.ActivePlayer, s.Players[0].Hero.Id, new TargetRef(dummy.Id)));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Weapon, Is.Null, "durability hit zero — the weapon broke");
            Assert.That(dummy.CurrentHealth, Is.EqualTo(8), "the swing itself landed for 2");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27),
                "the weapon's Toll dealt 3 to the enemy hero — before EF-020 BreakWeapon raised nothing "
                + "and this stayed at 30.");
        }

        // ── Door 2: equip-replacement ────────────────────────────────────────────

        [Test]
        public void Weapon_Toll_Fires_On_Equip_Replacement()
        {
            var db = Db();
            var s = MainPhase();
            Equip(s, 0, db.Get(Rattler));                             // the outgoing weapon, still at full durability
            var replacement = ToHand(s, 0, db.Get(Plain));

            var r = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.ActivePlayer, replacement.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27),
                "replacing a weapon destroys it, so its Toll must fire — the second of the two doors "
                + "a weapon leaves play by.");
            Assert.That(s.Players[0].Weapon, Is.Not.Null);
            Assert.That(s.Players[0].Weapon!.DefinitionId, Is.EqualTo(Plain), "the new weapon is equipped");
            Assert.That(s.Players[0].Weapon!.CurrentDurability, Is.EqualTo(2),
                "the replacement is untouched — the outgoing rattle must not chip it");
        }

        // ── The ordering decision ────────────────────────────────────────────────

        [Test]
        public void Replacement_Is_Already_Equipped_When_The_Outgoing_Rattle_Resolves()
        {
            // Chainer's Toll EQUIPs a successor. Because the rattle ENQUEUES rather than resolving
            // inline, the sequence is: Chainer breaks → rattle queued → Plain equipped → drain → rattle
            // EQUIPs Successor, which breaks *Plain*. If the rattle resolved inline instead, it would break
            // Chainer's own already-empty slot and Plain would survive as the final weapon. This test is the
            // one that has to change, deliberately, if that ordering is ever revisited.
            var db = Db();
            var s = MainPhase();
            Equip(s, 0, db.Get(Chainer));
            var replacement = ToHand(s, 0, db.Get(Plain));

            var r = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.ActivePlayer, replacement.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Weapon, Is.Not.Null);
            Assert.That(s.Players[0].Weapon!.DefinitionId, Is.EqualTo(Successor),
                "the queued rattle equipped its successor OVER the replacement that was already in the slot.");
        }

        [Test]
        public void Weapon_Toll_Does_Not_Break_Its_Own_Empty_Slot_Twice()
        {
            // Guard against the obvious regression in a naive fix: raising BEFORE clearing ps.Weapon would
            // let an EQUIP rattle re-enter BreakWeapon on the corpse and fire the SAME rattle again, and a
            // damage rattle would land twice. One break, one rattle.
            var db = Db();
            var s = MainPhase();
            Equip(s, 0, db.Get(Rattler));
            var dummy = OnBoard(s, 1, db.Get("ef020_dummy"));

            var r = new CommandProcessor(db).Execute(s,
                new AttackIntent(s.ActivePlayer, s.Players[0].Hero.Id, new TargetRef(dummy.Id)));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27), "3 damage exactly once, not 6");
            Assert.That(r.Events.Count(e => e.Kind == GameEventKind.CardDestroyed), Is.EqualTo(1),
                "exactly one weapon-death event for one break");
        }

        [Test]
        public void Weapon_Without_A_Toll_Breaks_Silently_As_Before()
        {
            // The control: EF-020 must not invent a trigger for a weapon that has no ON_DEATH effect.
            var db = Db();
            var s = MainPhase();
            Equip(s, 0, new CardDefinition(
                id: Plain, name: Plain, type: CardType.WEAPON, cost: 1,
                attack: 3, health: null, durability: 1,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), effects: Array.Empty<EffectDef>(),
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false));
            var dummy = OnBoard(s, 1, db.Get("ef020_dummy"));

            var r = new CommandProcessor(db).Execute(s,
                new AttackIntent(s.ActivePlayer, s.Players[0].Hero.Id, new TargetRef(dummy.Id)));

            Assert.That(r.Accepted, Is.True);
            Assert.That(s.Players[0].Weapon, Is.Null, "still breaks");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(30), "and nothing fires");
        }
    }
}
