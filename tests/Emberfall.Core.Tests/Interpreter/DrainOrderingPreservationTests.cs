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
    public sealed class DrainOrderingPreservationTests
    {
        private static CardDefinition Minion(string id, int health, params EffectDef[] effects)
            => new CardDefinition(id, id, CardType.MINION, 0, 0, health, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), effects,
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static CardDefinition Spell(string id, params EffectDef[] effects)
            => new CardDefinition(id, id, CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), effects,
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static CardInstance Board(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = state.Players[seat].Board.Count,
            };
            state.Players[seat].Board.Add(card);
            return card;
        }

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var state = new GameState(new MatchId("drain-order"), new DeterministicRandom(19UL),
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

        [Test]
        public void Drain_DeathChecks_Between_Dequeued_Effects_And_Uses_InstanceId_To_Break_Death_Ties()
        {
            // Initial DeathCheck removes A/B together. They have the same forced board position, so id is
            // the deciding tie-breaker. A kills Cascade; the fresh DeathCheck must enqueue Cascade behind
            // already-queued B. FIFO then resolves B, then Cascade, whose lethal damage must CheckWin before
            // a later queued effect could run.
            var deathA = new EffectDef(Trigger.ON_DEATH, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                selector: "FRIENDLY_MINIONS");
            var deathB = new EffectDef(Trigger.ON_DEATH, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                selector: "ENEMY_HERO");
            var deathCascade = new EffectDef(Trigger.ON_DEATH, Verb.DEAL_DAMAGE, 2, null, null, null, null,
                selector: "ENEMY_HERO");
            var db = new FakeCardDatabase()
                .Register(Minion("advance", 1))
                .Register(Minion("a", 1, deathA))
                .Register(Minion("b", 1, deathB))
                .Register(Minion("cascade", 1, deathCascade));
            var state = State();
            state.Players[0].Hero.CurrentHealth = 3;
            var a = Board(state, 1, db.Get("a"));
            var b = Board(state, 1, db.Get("b"));
            var cascade = Board(state, 1, db.Get("cascade"));
            a.Position = b.Position = 0; // Force the documented ascending-instance-id tie-break.
            a.CurrentHealth = b.CurrentHealth = 0;
            var advance = new CardInstance(state.NextInstanceId(), "advance", state.Players[0].Id) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(advance);

            var result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, advance.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Events.Select(e => (e.Kind, e.Source)), Is.EqualTo(new (VfxEvent, InstanceId?)[]
            {
                (VfxEvent.onPlay, advance.Id),
                (VfxEvent.onDeath, a.Id),
                (VfxEvent.onDeath, b.Id),
                (VfxEvent.onDamage, a.Id),
                (VfxEvent.onDeath, cascade.Id),
                (VfxEvent.onDamage, b.Id),
                (VfxEvent.onDamage, cascade.Id),
                (VfxEvent.onGameOver, null),
                (VfxEvent.onGameWin, state.Players[1].Hero.Id),
            }));
            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.Player1Win));
            Assert.That(state.ActionResolutionsThisTurn, Is.EqualTo(3),
                "A then B then the newly enqueued Cascade rattle: one bounded dequeue per effect.");
        }

        [Test]
        public void Drain_Stops_Only_When_The_Next_Dequeued_Effect_Exceeds_The_Resolution_Cap()
        {
            var retaliate = new EffectDef(Trigger.ON_DAMAGED, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                selector: "ENEMY_MINIONS");
            var db = new FakeCardDatabase()
                .Register(Spell("poke", new EffectDef(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1, null, null, null, null)))
                .Register(Minion("echo", 200, retaliate));
            var state = State();
            var left = Board(state, 0, db.Get("echo"));
            var right = Board(state, 1, db.Get("echo"));
            var poke = new CardInstance(state.NextInstanceId(), "poke", state.Players[0].Id) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(poke);

            var result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, poke.Id, 0, new[] { new TargetRef(right.Id) }));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.ActionResolutionsThisTurn, Is.EqualTo(MatchEngine.ActionResolutionCap + 1),
                "the cap is checked after dequeue, so the first over-cap effect records its attempted resolution.");
            Assert.That(left.CurrentHealth, Is.GreaterThan(0));
            Assert.That(right.CurrentHealth, Is.GreaterThan(0));
            Assert.That(state.Outcome, Is.EqualTo(MatchOutcome.InProgress));
        }
    }
}
