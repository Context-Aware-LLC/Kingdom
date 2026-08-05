using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Combat
{
    [TestFixture]
    [Category("Unit")]
    public sealed class WeaponIdentityTests
    {
        private const string Plain = "weapon_identity_plain";
        private const string Drainer = "weapon_identity_drainer";
        private const string Venomed = "weapon_identity_venomed";
        private const string Froster = "weapon_identity_froster";
        private const string Doublestrike = "weapon_identity_doublestrike";
        private const string RiderStack = "weapon_identity_rider_stack";
        private const string Swapper = "weapon_identity_swapper";
        private const string Toll = "weapon_identity_toll";
        private const string Dummy = "weapon_identity_dummy";
        private const string Shielded = "weapon_identity_shielded";

        private static CardDefinition Weapon(string id, int attack, int durability,
            Keyword[]? keywords = null, params RuleDefinition[] rules)
            => new CardDefinition(
                id, id, CardType.WEAPON, 1, attack, null, durability,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? Array.Empty<Keyword>(), rules,
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static CardDefinition Minion(string id, int attack, int health,
            params Keyword[] keywords)
            => new CardDefinition(
                id, id, CardType.MINION, 1, attack, health, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                keywords, Array.Empty<RuleDefinition>(),
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static FakeCardDatabase Db() => new FakeCardDatabase()
            .Register(Weapon(Plain, 2, 3))
            .Register(Weapon(Drainer, 3, 3, new[] { Keyword.LIFEDRAIN }))
            .Register(Weapon(Venomed, 1, 3, new[] { Keyword.VENOM }))
            .Register(Weapon(Froster, 2, 3, new[] { Keyword.FROST, Keyword.DOUBLESTRIKE }))
            .Register(Weapon(Doublestrike, 1, 3, new[] { Keyword.DOUBLESTRIKE }))
            .Register(Weapon(RiderStack, 3, 4, new[]
            {
                Keyword.LIFEDRAIN, Keyword.VENOM, Keyword.FROST, Keyword.DOUBLESTRIKE,
            }))
            .Register(Weapon(Swapper, 2, 3, new[] { Keyword.DOUBLESTRIKE },
                TestRules.Rule("equip_froster", Trigger.ON_ATTACK, new EquipAction(Froster))))
            .Register(Weapon(Toll, 2, 1, new[] { Keyword.TOLL },
                TestRules.Rule("draw_one", Trigger.ON_DEATH, new DrawAction(1))))
            .Register(Minion(Dummy, 0, 12))
            .Register(Minion(Shielded, 0, 12, Keyword.AEGIS));

        private static GameState MainPhase()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var p0State = new PlayerState(p0,
                new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30));
            var p1State = new PlayerState(p1,
                new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30));
            var state = new GameState(new MatchId("weapon-identity"),
                new DeterministicRandom(31UL), p0State, p1State)
            {
                ActivePlayer = p0,
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            p0State.Mana.Max = p0State.Mana.Available = 10;
            p1State.Mana.Max = p1State.Mana.Available = 10;
            return state;
        }

        private static CardInstance ToHand(GameState state, string definitionId)
        {
            var card = new CardInstance(state.NextInstanceId(), definitionId, state.Players[0].Id)
            {
                Zone = Zone.Hand,
            };
            state.Players[0].Hand.Add(card);
            return card;
        }

        private static CardInstance OnBoard(GameState state, int seat, CardDefinition def)
        {
            var player = state.Players[seat];
            var card = new CardInstance(state.NextInstanceId(), def.Id, player.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = player.Board.Count,
            };
            foreach (var keyword in def.Keywords) card.Keywords.Add(keyword);
            card.HasAegisShield = card.Keywords.Contains(Keyword.AEGIS);
            player.Board.Add(card);
            return card;
        }

        private static CardInstance PlayWeapon(GameState state, FakeCardDatabase db, string definitionId)
        {
            var card = ToHand(state, definitionId);
            var result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.ActivePlayer, card.Id, 0, Array.Empty<TargetRef>()));
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return card;
        }

        private static CommandResult SwingAt(GameState state, FakeCardDatabase db, InstanceId target)
            => new CommandProcessor(db).Execute(state,
                new AttackIntent(state.ActivePlayer, state.Players[0].Hero.Id, new TargetRef(target)));

        [Test]
        public void Playing_A_Weapon_Equips_The_Original_Card_Without_Reminting()
        {
            var db = Db();
            var state = MainPhase();
            var card = ToHand(state, Drainer);
            var enchantment = new Enchantment
            {
                Source = new InstanceId(91),
                AttackDelta = 2,
                GrantedKeyword = Keyword.FROST,
            };
            card.Enchantments.Add(enchantment);
            card.Keywords.Add(Keyword.FROST);
            card.CurrentDurability = 2;
            int nextIdBeforePlay = state.NextInstanceCounter;

            var result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.ActivePlayer, card.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Weapon, Is.SameAs(card));
            Assert.That(state.NextInstanceCounter, Is.EqualTo(nextIdBeforePlay),
                "equipping an existing card must not mint a replacement identity");
            Assert.That(card.Zone, Is.EqualTo(Zone.Weapon));
            Assert.That(card.CurrentAttack, Is.EqualTo(5), "printed attack plus the retained enchantment");
            Assert.That(card.CurrentDurability, Is.EqualTo(2), "existing runtime durability is retained");
            Assert.That(card.Keywords, Does.Contain(Keyword.LIFEDRAIN), "printed keywords initialize on equip");
            Assert.That(card.Keywords, Does.Contain(Keyword.FROST), "granted keywords survive the transition");
            Assert.That(card.Enchantments.Single(), Is.SameAs(enchantment));
            Assert.That(state.Players[0].Graveyard, Does.Not.Contain(card));
        }

        [Test]
        public void Broken_Weapon_Moves_The_Same_Card_To_Graveyard_And_Fires_Its_Marker_Trigger()
        {
            var db = Db();
            var state = MainPhase();
            var weapon = PlayWeapon(state, db, Toll);
            var drawn = new CardInstance(state.NextInstanceId(), Plain, state.Players[0].Id)
            {
                Zone = Zone.Deck,
            };
            state.Players[0].Deck.Add(drawn);

            var result = SwingAt(state, db, state.Players[1].Hero.Id);

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Weapon, Is.Null);
            Assert.That(state.Players[0].Graveyard, Does.Contain(weapon));
            Assert.That(state.Players[0].Graveyard.Single(c => c.Id == weapon.Id), Is.SameAs(weapon));
            Assert.That(weapon.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(weapon.Keywords, Does.Contain(Keyword.TOLL),
                "the Toll presentation marker stays on the same card instance");
            Assert.That(state.Players[0].Hand, Does.Contain(drawn),
                "the marker is backed by the ON_DEATH rule, which still fires from that instance");
        }

        [Test]
        public void Lifedrain_Weapon_Heals_The_Wielding_Hero()
        {
            var db = Db();
            var state = MainPhase();
            state.Players[0].Hero.CurrentHealth = 20;
            PlayWeapon(state, db, Drainer);
            var target = OnBoard(state, 1, db.Get(Dummy));

            var result = SwingAt(state, db, target.Id);

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Hero.CurrentHealth, Is.EqualTo(23));
        }

        [Test]
        public void Aegis_Absorbs_A_Weapon_Hit_Before_Any_Lifedrain_Rider()
        {
            var db = Db();
            var state = MainPhase();
            state.Players[0].Hero.CurrentHealth = 20;
            PlayWeapon(state, db, Drainer);
            var target = OnBoard(state, 1, db.Get(Shielded));

            var result = SwingAt(state, db, target.Id);

            Assert.That(result.Accepted, Is.True);
            Assert.That(target.HasAegisShield, Is.False);
            Assert.That(target.CurrentHealth, Is.EqualTo(12));
            Assert.That(state.Players[0].Hero.CurrentHealth, Is.EqualTo(20),
                "Aegis returns before the Lifedrain/Venom/Frost rider block");
        }

        [Test]
        public void Unified_Weapon_Pins_The_Full_Aegis_Soak_Lifedrain_Venom_Frost_Rider_Sequence()
        {
            // One equipped CardInstance carries every live attack rider. The three swings deliberately
            // exercise the pipeline in its fixed order: Aegis returns before riders; armor soaks before
            // Lifedrain; then a minion hit receives Venom before Frost and the subsequent DeathCheck.
            var db = Db();
            var state = MainPhase();
            state.Players[0].Hero.CurrentHealth = 20;
            var weapon = PlayWeapon(state, db, RiderStack);
            var warded = OnBoard(state, 1, db.Get(Shielded));

            var absorbed = SwingAt(state, db, warded.Id);

            Assert.That(absorbed.Accepted, Is.True);
            Assert.That(weapon.Zone, Is.EqualTo(Zone.Weapon));
            Assert.That(state.Players[0].Weapon, Is.SameAs(weapon));
            Assert.That(warded.CurrentHealth, Is.EqualTo(12));
            Assert.That(warded.HasAegisShield, Is.False);
            Assert.That(state.Players[0].Hero.CurrentHealth, Is.EqualTo(20),
                "Aegis is the first gate: no Lifedrain, Venom, or Frost rider may run.");
            Assert.That(absorbed.Events.Select(e => e.Kind),
                Is.EqualTo(new[] { GameEventKind.AttackDeclared, GameEventKind.DamageApplied }));
            Assert.That(absorbed.Events[1].Flags & EventFlags.AegisAbsorbed, Is.Not.EqualTo(EventFlags.None));

            state.Players[1].Hero.Armor = 2;
            var soaked = SwingAt(state, db, state.Players[1].Hero.Id);

            Assert.That(soaked.Accepted, Is.True);
            Assert.That(state.Players[1].Hero.Armor, Is.EqualTo(0));
            Assert.That(state.Players[1].Hero.CurrentHealth, Is.EqualTo(29),
                "two armor soaks before the remaining point reaches health.");
            Assert.That(state.Players[0].Hero.CurrentHealth, Is.EqualTo(23),
                "Lifedrain follows the damage/soak mutation and restores the full dealt amount.");
            Assert.That(state.Players[1].Hero.IsFrosted, Is.True,
                "Frost is the final rider, after Lifedrain.");
            Assert.That(soaked.Events.Select(e => e.Kind),
                Is.EqualTo(new[] { GameEventKind.AttackDeclared, GameEventKind.DamageApplied, GameEventKind.HealthRestored, GameEventKind.FrostApplied }));

            // WIND FURY has spent the two attacks for this turn. Use the actual turn boundary rather than
            // mutating the attack budget, so the third swing remains a normal unified-weapon command path.
            var processor = new CommandProcessor(db);
            Assert.That(processor.Execute(state, new EndTurnIntent(state.Players[0].Id)).Accepted, Is.True);
            state.Players[1].Deck.Add(new CardInstance(state.NextInstanceId(), Plain, state.Players[1].Id)
            {
                Zone = Zone.Deck,
            });
            Assert.That(processor.Execute(state, new EndTurnIntent(state.Players[1].Id)).Accepted, Is.True);

            var poisoned = OnBoard(state, 1, db.Get(Dummy));
            var venomAndFrost = SwingAt(state, db, poisoned.Id);

            Assert.That(venomAndFrost.Accepted, Is.True);
            Assert.That(poisoned.CurrentHealth, Is.EqualTo(0), "Venom makes the otherwise surviving 3-damage hit lethal.");
            Assert.That(poisoned.IsFrosted, Is.True, "Frost still runs after Venom marks the minion dead.");
            Assert.That(state.Players[1].Board, Does.Not.Contain(poisoned), "the following DeathCheck settles Venom.");
            Assert.That(venomAndFrost.Events.Select(e => e.Kind), Is.EqualTo(new[]
            {
                GameEventKind.AttackDeclared, GameEventKind.DamageApplied, GameEventKind.HealthRestored, GameEventKind.FrostApplied, GameEventKind.CardDestroyed,
            }));
        }

        [Test]
        public void Venom_Weapon_Kills_A_Minion_That_Survives_Its_Damage()
        {
            var db = Db();
            var state = MainPhase();
            PlayWeapon(state, db, Venomed);
            var target = OnBoard(state, 1, db.Get(Dummy));

            var result = SwingAt(state, db, target.Id);

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[1].Board, Is.Empty);
        }

        [Test]
        public void Frost_Weapon_Frosts_What_It_Damages()
        {
            var db = Db();
            var state = MainPhase();
            PlayWeapon(state, db, Froster);
            var target = OnBoard(state, 1, db.Get(Dummy));

            var result = SwingAt(state, db, target.Id);

            Assert.That(result.Accepted, Is.True);
            Assert.That(target.IsFrosted, Is.True);
        }

        [Test]
        public void Doublestrike_Weapon_Allows_Two_Hero_Attacks_But_Not_Three()
        {
            var db = Db();
            var state = MainPhase();
            PlayWeapon(state, db, Doublestrike);
            var processor = new CommandProcessor(db);
            var attack = new AttackIntent(state.ActivePlayer, state.Players[0].Hero.Id,
                new TargetRef(state.Players[1].Hero.Id));

            Assert.That(processor.Execute(state, attack).Accepted, Is.True, "first attack");
            Assert.That(processor.Execute(state, attack).Accepted, Is.True, "Doublestrike attack");
            Assert.That(processor.Execute(state, attack).Reason, Is.EqualTo(RejectReason.NoAttacksLeft));
            Assert.That(state.Players[0].Weapon!.CurrentDurability, Is.EqualTo(1));
        }

        [Test]
        public void Attack_Riders_Are_Snapshotted_Before_The_Queued_OnAttack_Replaces_The_Weapon()
        {
            var db = Db();
            var state = MainPhase();
            PlayWeapon(state, db, Swapper); // Doublestrike, no Frost; ON_ATTACK equips Froster
            var firstTarget = OnBoard(state, 1, db.Get(Dummy));

            var first = SwingAt(state, db, firstTarget.Id);

            Assert.That(first.Accepted, Is.True);
            Assert.That(firstTarget.IsFrosted, Is.False,
                "the first hit uses the declaring weapon's rider snapshot, not its queued replacement");
            Assert.That(state.Players[0].Weapon!.DefinitionId, Is.EqualTo(Froster));

            var secondTarget = OnBoard(state, 1, db.Get(Dummy));
            var second = SwingAt(state, db, secondTarget.Id);

            Assert.That(second.Accepted, Is.True, "the replacement also carries Doublestrike");
            Assert.That(secondTarget.IsFrosted, Is.True,
                "the next hit uses the replacement weapon's own rider set");
        }

        [TestCase(Keyword.LIFEDRAIN)]
        [TestCase(Keyword.VENOM)]
        [TestCase(Keyword.FROST)]
        [TestCase(Keyword.DOUBLESTRIKE)]
        [TestCase(Keyword.HERALD)]
        [TestCase(Keyword.TOLL)]
        [TestCase(Keyword.SILENCE)]
        [TestCase(Keyword.BACKDRAFT)]
        [TestCase(Keyword.REINFORCE)]
        public void Live_Riders_And_Trigger_Markers_Are_Legal_On_Weapons(Keyword keyword)
        {
            var def = Weapon("weapon_keyword_legal", 1, 1, new[] { keyword });
            var report = new CardValidator().Validate(def, new FakeCardDatabase().Register(def));

            Assert.That(report.Issues.Any(i => i.Code == "C6-WEAPON-KEYWORD-DEAD"), Is.False);
        }

        [TestCase(Keyword.BULWARK)]
        [TestCase(Keyword.FERVOR)]
        [TestCase(Keyword.SKIRMISH)]
        [TestCase(Keyword.AEGIS)]
        [TestCase(Keyword.SHROUD)]
        [TestCase(Keyword.ARCANE_SURGE)]
        [TestCase(Keyword.REKINDLE)]
        [TestCase(Keyword.OVERWHELM)]
        [TestCase(Keyword.VANGUARD)]
        public void Unsupported_Weapon_Keywords_Fail_Closed(Keyword keyword)
        {
            var def = Weapon("weapon_keyword_dead", 1, 1, new[] { keyword });
            var report = new CardValidator().Validate(def, new FakeCardDatabase().Register(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C6-WEAPON-KEYWORD-DEAD"), Is.True);
        }

        [Test]
        public void Every_Keyword_Has_An_Explicit_Weapon_Reachability_Decision()
        {
            var supported = new[]
            {
                Keyword.LIFEDRAIN, Keyword.VENOM, Keyword.FROST, Keyword.DOUBLESTRIKE,
                Keyword.HERALD, Keyword.TOLL, Keyword.SILENCE, Keyword.BACKDRAFT,
                Keyword.REINFORCE,
            };
            var unsupported = new[]
            {
                Keyword.BULWARK, Keyword.FERVOR, Keyword.SKIRMISH, Keyword.AEGIS,
                Keyword.SHROUD, Keyword.ARCANE_SURGE, Keyword.REKINDLE, Keyword.OVERWHELM,
                Keyword.VANGUARD,
            };

            Assert.That(supported.Concat(unsupported).Distinct(),
                Is.EquivalentTo(Enum.GetValues(typeof(Keyword)).Cast<Keyword>()));
        }
    }
}
