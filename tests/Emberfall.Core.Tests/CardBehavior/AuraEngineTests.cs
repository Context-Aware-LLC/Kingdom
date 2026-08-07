using System;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardBehavior
{
    [TestFixture]
    [Category("Unit")]
    public sealed class AuraEngineTests
    {
        private static CardDefinition Minion(string id, int attack, int health,
            params RuleDefinition[] rules)
            => new CardDefinition(id, id, CardType.MINION, 1, attack, health, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules,
                "", "art/" + id, SetId.CORE, true, null, "");

        private static RuleDefinition Aura(string id, int attack, int health, string selector = "FRIENDLY_MINIONS")
            => new RuleDefinition(new RuleId(id), AuraActivation.Instance, AlwaysCondition.Instance,
                SourceBoundLifetime.Instance,
                new ModifyStatsAction(attack, health, new TargetSpec(selector)));

        private static GameState State(out PlayerId p0, out PlayerId p1)
        {
            p0 = new PlayerId("P0");
            p1 = new PlayerId("P1");
            var state = new GameState(new MatchId("aura"), new DeterministicRandom(1UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)));
            state.NextInstanceCounter = 3;
            return state;
        }

        [Test]
        public void Board_Aura_Applies_In_Board_Order_And_Removes_Only_Its_Own_Contribution()
        {
            var sourceDef = Minion("source", 2, 3, Aura("anthem", 1, 2));
            var targetDef = Minion("target", 2, 3);
            var db = new FakeCardDatabase().Register(sourceDef).Register(targetDef);
            var state = State(out _, out _);
            var source = new CardInstance(state.NextInstanceId(), sourceDef.Id, state.Players[0].Id);
            var target = new CardInstance(state.NextInstanceId(), targetDef.Id, state.Players[0].Id);
            RulesEngine.EnterBoard(state, source, sourceDef, 0, -1, db);
            RulesEngine.EnterBoard(state, target, targetDef, 0, -1, db);

            Assert.That(target.CurrentAttack, Is.EqualTo(3));
            Assert.That(target.MaxHealth, Is.EqualTo(5));
            Assert.That(target.Enchantments, Has.One.Matches<Enchantment>(e => e.IsAura && e.Source == source.Id));

            target.Enchantments.Add(new Enchantment { Source = source.Id, AttackDelta = 4 });
            DerivedStats.Recompute(target, targetDef);
            state.Players[0].Board.Remove(source);
            source.Zone = Zone.Graveyard;
            state.Players[0].Graveyard.Add(source);
            AuraEngine.Recompute(state, db);

            Assert.That(target.CurrentAttack, Is.EqualTo(6), "the same source's ordinary buff must survive");
            Assert.That(target.MaxHealth, Is.EqualTo(3));
            Assert.That(target.Enchantments, Has.One.Matches<Enchantment>(e => !e.IsAura && e.Source == source.Id));
        }

        [Test]
        public void Aura_Recompute_Preserves_Target_Damage_When_Health_Changes()
        {
            var sourceDef = Minion("source", 1, 3, Aura("health", 0, 3));
            var targetDef = Minion("target", 2, 4);
            var db = new FakeCardDatabase().Register(sourceDef).Register(targetDef);
            var state = State(out _, out _);
            var source = new CardInstance(state.NextInstanceId(), sourceDef.Id, state.Players[0].Id);
            var target = new CardInstance(state.NextInstanceId(), targetDef.Id, state.Players[0].Id);
            RulesEngine.EnterBoard(state, source, sourceDef, 0, -1, db);
            RulesEngine.EnterBoard(state, target, targetDef, 0, -1, db);
            target.CurrentHealth = 5; // two damage on a seven-health body

            state.Players[0].Board.Remove(source);
            AuraEngine.Recompute(state, db);

            Assert.That(target.MaxHealth, Is.EqualTo(4));
            Assert.That(target.CurrentHealth, Is.EqualTo(2));
        }

        [Test]
        public void Silenced_And_Control_Transferred_Sources_Retarget_Without_Queued_Work()
        {
            var sourceDef = Minion("source", 1, 3, Aura("anthem", 2, 0));
            var targetDef = Minion("target", 1, 3);
            var db = new FakeCardDatabase().Register(sourceDef).Register(targetDef);
            var state = State(out _, out _);
            var source = new CardInstance(state.NextInstanceId(), sourceDef.Id, state.Players[0].Id);
            var friendly = new CardInstance(state.NextInstanceId(), targetDef.Id, state.Players[0].Id);
            var enemy = new CardInstance(state.NextInstanceId(), targetDef.Id, state.Players[1].Id);
            RulesEngine.EnterBoard(state, source, sourceDef, 0, -1, db);
            RulesEngine.EnterBoard(state, friendly, targetDef, 0, -1, db);
            RulesEngine.EnterBoard(state, enemy, targetDef, 1, -1, db);

            Assert.That(friendly.CurrentAttack, Is.EqualTo(3));
            Assert.That(enemy.CurrentAttack, Is.EqualTo(1));

            source.IsSilenced = true;
            AuraEngine.Recompute(state, db);
            Assert.That(friendly.CurrentAttack, Is.EqualTo(1));

            source.IsSilenced = false;
            state.Players[0].Board.Remove(source);
            state.Players[1].Board.Add(source);
            source.Controller = state.Players[1].Id;
            RulesEngine.Reindex(state.Players[0].Board);
            RulesEngine.Reindex(state.Players[1].Board);
            AuraEngine.Recompute(state, db);

            Assert.That(friendly.CurrentAttack, Is.EqualTo(1));
            Assert.That(enemy.CurrentAttack, Is.EqualTo(3));
        }

        [Test]
        public void Adjacent_And_Overlapping_Auras_Use_Board_Order_And_Ignore_Hidden_Sources()
        {
            var adjacentDef = Minion("adjacent", 1, 3, Aura("adjacent", 1, 0, "ADJACENT"));
            var anthemDef = Minion("anthem", 1, 3, Aura("anthem", 2, 0));
            var targetDef = Minion("target", 1, 3);
            var db = new FakeCardDatabase().Register(adjacentDef).Register(anthemDef).Register(targetDef);
            var state = State(out _, out _);
            var left = new CardInstance(state.NextInstanceId(), targetDef.Id, state.Players[0].Id);
            var aura = new CardInstance(state.NextInstanceId(), adjacentDef.Id, state.Players[0].Id);
            var right = new CardInstance(state.NextInstanceId(), targetDef.Id, state.Players[0].Id);
            var anthem = new CardInstance(state.NextInstanceId(), anthemDef.Id, state.Players[0].Id);
            RulesEngine.EnterBoard(state, left, targetDef, 0, -1, db);
            RulesEngine.EnterBoard(state, aura, adjacentDef, 0, -1, db);
            RulesEngine.EnterBoard(state, right, targetDef, 0, -1, db);
            RulesEngine.EnterBoard(state, anthem, anthemDef, 0, -1, db);
            var hidden = new CardInstance(state.NextInstanceId(), anthemDef.Id, state.Players[0].Id) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(hidden);
            AuraEngine.Recompute(state, db);

            Assert.That(left.CurrentAttack, Is.EqualTo(4));
            Assert.That(right.CurrentAttack, Is.EqualTo(4));
            Assert.That(aura.CurrentAttack, Is.EqualTo(3));
            Assert.That(anthem.CurrentAttack, Is.EqualTo(3));
            Assert.That(hidden.Enchantments, Is.Empty);
        }

        [Test]
        public void Aura_Validation_Fails_Closed_For_Keyword_Actions_And_Player_Targeting()
        {
            var keywordAura = new RuleDefinition(new RuleId("keyword"), AuraActivation.Instance,
                AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                new GrantKeywordAction(Keyword.BULWARK, new TargetSpec("FRIENDLY_MINIONS")));
            var choiceAura = new RuleDefinition(new RuleId("choice"), AuraActivation.Instance,
                AlwaysCondition.Instance, SourceBoundLifetime.Instance,
                new ModifyStatsAction(1, 0, new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER)));
            var keywordCard = Minion("keyword_aura", 1, 1, keywordAura);
            var choiceCard = Minion("choice_aura", 1, 1, choiceAura);
            var db = new FakeCardDatabase().Register(keywordCard).Register(choiceCard);

            Assert.That(new CardValidator().Validate(keywordCard, db).Ok, Is.False);
            Assert.That(new CardValidator().Validate(choiceCard, db).Ok, Is.False);
        }
    }
}
