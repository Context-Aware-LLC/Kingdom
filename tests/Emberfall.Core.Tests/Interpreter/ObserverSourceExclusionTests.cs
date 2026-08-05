using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using Verb = Emberfall.Core.Contracts.Action;

namespace Emberfall.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class ObserverSourceExclusionTests
    {
        private static CardDefinition Minion(string id, params EffectDef[] effects)
            => new CardDefinition(id, id, CardType.MINION, 0, 0, 1, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), effects,
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static CardDefinition Spell(string id, params EffectDef[] effects)
            => new CardDefinition(id, id, CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), effects,
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

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

        private static CardInstance PutIn(GameState state, Zone zone, string definitionId)
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
            var turn = new EffectDef(Trigger.ON_TURN_START, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                selector: "ENEMY_HERO", condition: new EffectConditionDef(ConditionType.TURN_OWNER, TurnOwner.ANY));
            var spellCast = new EffectDef(Trigger.ON_SPELL_CAST, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                selector: "ENEMY_HERO");
            var summon = new EffectDef(Trigger.ON_FRIENDLY_SUMMON, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                selector: "ENEMY_HERO");
            var db = new FakeCardDatabase()
                .Register(Minion("hidden_observer", turn, spellCast, summon))
                .Register(Minion("token"))
                .Register(Spell("summoning_spell", new EffectDef(Trigger.ON_CAST, Verb.SUMMON, 1,
                    null, "token", null, null)));
            var state = State();
            var handObserver = PutIn(state, Zone.Hand, "hidden_observer");
            var deckObserver = PutIn(state, Zone.Deck, "hidden_observer");
            var graveObserver = PutIn(state, Zone.Graveyard, "hidden_observer");
            var spell = PutIn(state, Zone.Hand, "summoning_spell");

            var cast = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, spell.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(cast.Accepted, Is.True);
            Assert.That(state.Players[1].Hero.CurrentHealth, Is.EqualTo(30),
                "hidden cards cannot observe either the cast or the summoned token.");
            Assert.That(cast.Events.Any(e => e.Kind == VfxEvent.onDamage &&
                (e.Source == handObserver.Id || e.Source == deckObserver.Id || e.Source == graveObserver.Id)), Is.False);

            // The opponent has a draw card, so the turn-start assertion cannot pass merely because fatigue
            // obscured the expected hero health. The hidden P0 sources target P1 and must remain inert.
            state.Players[1].Deck.Add(new CardInstance(state.NextInstanceId(), "token", state.Players[1].Id)
            {
                Zone = Zone.Deck,
            });
            var turnStart = new CommandProcessor(db).Execute(state, new EndTurnIntent(state.Players[0].Id));

            Assert.That(turnStart.Accepted, Is.True);
            Assert.That(state.Players[1].Hero.CurrentHealth, Is.EqualTo(30));
            Assert.That(turnStart.Events.Any(e => e.Kind == VfxEvent.onDamage &&
                (e.Source == handObserver.Id || e.Source == deckObserver.Id || e.Source == graveObserver.Id)), Is.False);
        }
    }
}
