using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class ObserverSourceExclusionTests
    {
        private static CardDefinition Minion(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 0, 0, 1, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var state = new GameState(new MatchId("hidden-observers"), new DeterministicRandom(23UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)))
            {
                ActivePlayer = p0,
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            state.Players[0].Mana.Max = state.Players[0].Mana.Available = 10;
            state.Players[1].Mana.Max = state.Players[1].Mana.Available = 10;
            return state;
        }

        private static CardInstance PutIn(GameState state, Zone zone, int definitionId)
        {
            var card = new CardInstance(state.NextInstanceId(), definitionId, state.Players[0].Id) { Zone = zone };
            if (zone == Zone.Hand) state.Players[0].Hand.Add(card);
            else if (zone == Zone.Deck) state.Players[0].Deck.Add(card);
            else state.Players[0].Graveyard.Add(card);
            return card;
        }

        [Test]
        public void Hidden_Zone_Cards_Never_Observe_Turn_Start_Spell_Cast_Or_Friendly_Summon()
        {
            RuleDefinition turn = TestRules.Rule("turn", Trigger.ON_TURN_START,
                new TurnOwnerCondition(TurnOwner.ANY), new DamageAction(1, new TargetSpec("ENEMY_HERO", TargetSelection.Of(1))));
            RuleDefinition spellCast = TestRules.Rule("spell_cast", Trigger.ON_SPELL_CAST,
                new DamageAction(1, new TargetSpec("ENEMY_HERO", TargetSelection.Of(1))));
            RuleDefinition summon = TestRules.Rule("summon", Trigger.ON_FRIENDLY_SUMMON,
                new DamageAction(1, new TargetSpec("ENEMY_HERO", TargetSelection.Of(1))));
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("hidden_observer"), turn, spellCast, summon))
                .Register(Minion(FixtureCard.Id("token")))
                .Register(Spell(FixtureCard.Id("summoning_spell"), TestRules.Rule("summon_token", Trigger.ON_CAST,
                    new SummonAction(FixtureCard.Id("token"), 1))));
            var state = State();
            var handObserver = PutIn(state, Zone.Hand, FixtureCard.Id("hidden_observer"));
            var deckObserver = PutIn(state, Zone.Deck, FixtureCard.Id("hidden_observer"));
            var graveObserver = PutIn(state, Zone.Graveyard, FixtureCard.Id("hidden_observer"));
            var spell = PutIn(state, Zone.Hand, FixtureCard.Id("summoning_spell"));

            var cast = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, spell.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(cast.Accepted, Is.True);
            Assert.That(state.Players[1].Hero.CurrentHealth, Is.EqualTo(30),
                "hidden cards cannot observe either the cast or the summoned token.");
            Assert.That(cast.Events.Any(e => e.Kind == GameEventKind.DamageApplied &&
                (e.Source == handObserver.Id || e.Source == deckObserver.Id || e.Source == graveObserver.Id)), Is.False);

            // The opponent has a draw card, so the turn-start assertion cannot pass merely because fatigue
            // obscured the expected hero health. The hidden P0 sources target P1 and must remain inert.
            state.Players[1].Deck.Add(new CardInstance(state.NextInstanceId(), FixtureCard.Id("token"), state.Players[1].Id)
            {
                Zone = Zone.Deck,
            });
            var turnStart = new CommandProcessor(db).Execute(state, new EndTurnIntent(state.Players[0].Id));

            Assert.That(turnStart.Accepted, Is.True);
            Assert.That(state.Players[1].Hero.CurrentHealth, Is.EqualTo(30));
            Assert.That(turnStart.Events.Any(e => e.Kind == GameEventKind.DamageApplied &&
                (e.Source == handObserver.Id || e.Source == deckObserver.Id || e.Source == graveObserver.Id)), Is.False);
        }
    }
}
