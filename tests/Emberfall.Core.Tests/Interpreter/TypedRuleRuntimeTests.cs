using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class TypedRuleRuntimeTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void HeroPower_May_Execute_Typed_GainArmor_Without_An_Engine_Policy_Ban()
        {
            RuleDefinition rule = Rule("gain_armor", new GainArmorAction(4));
            CardDefinition power = Card("armor_power", CardType.HERO_POWER, new[] { rule });
            var db = new FakeCardDatabase().Register(power);
            GameState state = State();
            state.Players[0].Hero.HeroPowerId = power.Id;

            CommandResult result = new CommandProcessor(db).Execute(state,
                new HeroPowerIntent(P0, System.Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Hero.Armor, Is.EqualTo(4));
            Assert.That(state.Players[0].Mana.Available, Is.EqualTo(8));
            Assert.That(result.Events, Has.Some.Matches<GameEvent>(e =>
                e.Kind == GameEventKind.ArmorGained &&
                e.Source == state.Players[0].Hero.Id &&
                e.Target == state.Players[0].Hero.Id &&
                e.Amount == 4));
        }

        [Test]
        public void Typed_TakeControl_Transfers_An_Enemy_Weapon_With_Its_Identity_Intact()
        {
            var take = Rule("seize_weapon",
                new TakeControlAction(new TargetSpec("ENEMY_WEAPON", TargetChosenBy.PLAYER)));
            CardDefinition spell = Card("seize", CardType.SPELL, new[] { take });
            CardDefinition weaponDef = new CardDefinition("blade", "blade", CardType.WEAPON, 1,
                2, null, 2, CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                System.Array.Empty<RuleDefinition>(), "", "", "art/blade", SetId.CORE,
                true, null, "", false);
            var db = new FakeCardDatabase().Register(spell).Register(weaponDef);
            GameState state = State();
            var spellCard = new CardInstance(state.NextInstanceId(), spell.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(spellCard);
            var weapon = new CardInstance(state.NextInstanceId(), weaponDef.Id, P1)
            {
                Zone = Zone.Weapon,
                CurrentAttack = 2,
                CurrentDurability = 2,
            };
            state.Players[1].Weapon = weapon;

            CommandResult result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(P0, spellCard.Id, 0, new[] { new TargetRef(weapon.Id) }));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[1].Weapon, Is.Null);
            Assert.That(state.Players[0].Weapon, Is.SameAs(weapon));
            Assert.That(weapon.Controller, Is.EqualTo(P0));
            Assert.That(result.Events, Has.Some.Matches<GameEvent>(e =>
                e.Kind == GameEventKind.ControlChanged &&
                e.Source == spellCard.Id && e.Target == weapon.Id));
        }

        [Test]
        public void Typed_TakeControl_Transfers_An_Enemy_Minion_And_Emits_The_Fact()
        {
            var take = Rule("seize_minion",
                new TakeControlAction(new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER)));
            CardDefinition spell = Card("seize", CardType.SPELL, new[] { take });
            CardDefinition minionDef = new CardDefinition("guard", "guard", CardType.MINION, 1,
                2, 3, null, CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                System.Array.Empty<RuleDefinition>(), "", "", "art/guard", SetId.CORE,
                true, null, "", false);
            var db = new FakeCardDatabase().Register(spell).Register(minionDef);
            GameState state = State();
            var spellCard = new CardInstance(state.NextInstanceId(), spell.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(spellCard);
            var minion = new CardInstance(state.NextInstanceId(), minionDef.Id, P1)
            {
                Zone = Zone.Board,
                CurrentAttack = 2,
                CurrentHealth = 3,
                MaxHealth = 3,
            };
            state.Players[1].Board.Add(minion);

            CommandResult result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(P0, spellCard.Id, 0, new[] { new TargetRef(minion.Id) }));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[1].Board, Does.Not.Contain(minion));
            Assert.That(state.Players[0].Board, Does.Contain(minion));
            Assert.That(minion.Controller, Is.EqualTo(P0));
            Assert.That(result.Events, Has.Some.Matches<GameEvent>(e =>
                e.Kind == GameEventKind.ControlChanged &&
                e.Source == spellCard.Id && e.Target == minion.Id));
        }

        [Test]
        public void TakeControl_Capability_Is_Exactly_Minion_And_Weapon()
        {
            var action = new TakeControlAction(new TargetSpec("ANY_MINION"));

            Assert.That(RuleActionCapabilities.Allows(action, TargetCandidateKind.Minion), Is.True);
            Assert.That(RuleActionCapabilities.Allows(action, TargetCandidateKind.Weapon), Is.True);
            Assert.That(RuleActionCapabilities.Allows(action, TargetCandidateKind.Hero), Is.False);
            Assert.That(RuleActionCapabilities.Allows(action, TargetCandidateKind.HandCard), Is.False);
        }

        private static RuleDefinition Rule(string id, RuleAction action)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(Trigger.ON_CAST),
                AlwaysCondition.Instance, OneShotLifetime.Instance, action);

        private static CardDefinition Card(string id, CardType type, RuleDefinition[] rules)
            => new CardDefinition(id, id, type, type == CardType.HERO_POWER ? 2 : 1,
                null, null, null, CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), rules, "", "", "art/" + id, SetId.CORE,
                true, null, "", false);

        private static GameState State()
        {
            var state = new GameState(new MatchId("typed-runtime"), new DeterministicRandom(19),
                new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.STONEWARDEN, 30)),
                new PlayerState(P1, new HeroInstance(new InstanceId(2), P1, CardClass.TEMPEST, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = P0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            state.Players[0].Mana.Max = state.Players[0].Mana.Available = 10;
            return state;
        }
    }
}
