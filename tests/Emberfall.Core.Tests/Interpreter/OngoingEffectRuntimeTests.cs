using System;
using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class OngoingEffectRuntimeTests
    {
        private const string OngoingSpell = "ongoing_spell";
        private const string PlainSpell = "plain_spell";
        private const string Vanilla = "vanilla";

        [Test]
        public void Resolved_Spell_Creates_OneShot_Observer_And_Remains_In_Graveyard()
        {
            var future = FutureRule("future", Trigger.ON_TURN_START, OneShotLifetime.Instance,
                TurnOwner.ANY);
            var immediate = new RuleDefinition(new RuleId("immediate"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(2, new TargetSpec("ENEMY_HERO")));
            var (state, processor, spell) = Match(Spell(OngoingSpell, immediate, future));

            CommandResult cast = Play(state, processor, spell);

            Assert.That(state.Players[0].Graveyard, Does.Contain(spell));
            Assert.That(state.OngoingEffects, Has.Count.EqualTo(1));
            OngoingEffectInstance ongoing = state.OngoingEffects[0];
            Assert.Multiple(() =>
            {
                Assert.That(ongoing.Id, Is.Not.EqualTo(spell.Id));
                Assert.That(ongoing.Controller, Is.EqualTo(state.Players[0].Id));
                Assert.That(ongoing.SourceDefinitionId, Is.EqualTo(OngoingSpell));
                Assert.That(ongoing.Lifetime, Is.EqualTo(OngoingEffectLifetime.ONE_SHOT));
                Assert.That(ongoing.RemainingUses, Is.EqualTo(1));
                Assert.That(ongoing.IsExpired, Is.False);
                Assert.That(cast.Events.Select(e => e.Kind), Does.Contain(GameEventKind.OngoingEffectCreated));
                Assert.That(cast.Events.Where(e => e.Kind == GameEventKind.DamageApplied).Single().Source,
                    Is.EqualTo(spell.Id), "ON_CAST stays immediate and keeps the spell identity.");
            });

            CommandResult turn = Accept(processor.Execute(state,
                new EndTurnIntent(state.Players[0].Id)));

            Assert.That(state.OngoingEffects, Is.Empty);
            Assert.That(turn.Events.Where(e => e.Kind == GameEventKind.DamageApplied).Single().Source,
                Is.EqualTo(ongoing.Id));
            Assert.That(turn.Events.Where(e => e.Kind == GameEventKind.OngoingEffectTriggered)
                .Single().Source, Is.EqualTo(ongoing.Id));
            Assert.That(turn.Events.Where(e => e.Kind == GameEventKind.OngoingEffectExpired)
                .Single().Source, Is.EqualTo(ongoing.Id));
            foreach (int viewerSeat in new[] { 0, 1 })
            {
                var projected = GameStateProjector.ProjectEvents(turn.Events,
                    processor.LastEventVisibility, viewerSeat);
                Assert.That(projected.Where(e => e.Kind == GameEventKind.OngoingEffectTriggered)
                    .Single().Source, Is.EqualTo(ongoing.Id));
                Assert.That(projected.Where(e => e.Kind == GameEventKind.OngoingEffectExpired)
                    .Single().Source, Is.EqualTo(ongoing.Id),
                    "Expiry identity is public even though the instance is removed after the event.");
            }
        }

        [Test]
        public void Finite_Observer_Expires_After_Exactly_N_Qualified_Resolutions()
        {
            var future = FutureRule("finite", Trigger.ON_TURN_START, new FiniteLifetime(2),
                TurnOwner.ANY);
            var (state, processor, spell) = Match(Spell(OngoingSpell, future));
            _ = Play(state, processor, spell);
            InstanceId ongoingId = state.OngoingEffects.Single().Id;

            _ = Accept(processor.Execute(state, new EndTurnIntent(state.Players[0].Id)));
            Assert.That(state.OngoingEffects.Single().RemainingUses, Is.EqualTo(1));

            CommandResult second = Accept(processor.Execute(state,
                new EndTurnIntent(state.Players[1].Id)));

            Assert.That(state.OngoingEffects, Is.Empty);
            Assert.That(second.Events.Count(e => e.Kind == GameEventKind.OngoingEffectTriggered &&
                e.Source == ongoingId), Is.EqualTo(1));
            Assert.That(second.Events.Count(e => e.Kind == GameEventKind.OngoingEffectExpired &&
                e.Source == ongoingId), Is.EqualTo(1));
        }

        [Test]
        public void NonQualifying_Condition_Does_Not_Decrement_Finite_Lifetime()
        {
            var future = FutureRule("finite", Trigger.ON_TURN_START, new FiniteLifetime(2),
                TurnOwner.CONTROLLER);
            var (state, processor, spell) = Match(Spell(OngoingSpell, future));
            _ = Play(state, processor, spell);

            CommandResult opponentTurn = Accept(processor.Execute(state,
                new EndTurnIntent(state.Players[0].Id)));

            Assert.That(state.OngoingEffects.Single().RemainingUses, Is.EqualTo(2));
            Assert.That(opponentTurn.Events.Any(e => e.Kind == GameEventKind.OngoingEffectTriggered),
                Is.False);
        }

        [Test]
        public void Permanent_Observer_Never_Decrements_Or_Expires()
        {
            var future = FutureRule("permanent", Trigger.ON_TURN_START,
                PermanentLifetime.Instance, TurnOwner.ANY);
            var (state, processor, spell) = Match(Spell(OngoingSpell, future));
            _ = Play(state, processor, spell);

            _ = Accept(processor.Execute(state, new EndTurnIntent(state.Players[0].Id)));
            _ = Accept(processor.Execute(state, new EndTurnIntent(state.Players[1].Id)));

            Assert.That(state.OngoingEffects, Has.Count.EqualTo(1));
            Assert.That(state.OngoingEffects[0].Lifetime, Is.EqualTo(OngoingEffectLifetime.PERMANENT));
            Assert.That(state.OngoingEffects[0].RemainingUses, Is.Null);
            Assert.That(state.OngoingEffects[0].IsExpired, Is.False);
        }

        [TestCase(TurnOwner.CONTROLLER)]
        [TestCase(TurnOwner.OPPONENT)]
        [TestCase(TurnOwner.ANY)]
        public void Turn_Ownership_Is_EndToEnd_For_Ongoing_Effects_From_Both_Seats(
            TurnOwner owner)
        {
            RuleDefinition future = FutureRule("owned_turn", Trigger.ON_TURN_START,
                PermanentLifetime.Instance, owner);
            CardDefinition definition = Spell(OngoingSpell, future);
            var db = new FakeCardDatabase().Register(definition).Register(Minion(Vanilla));
            GameState state = EmptyState();
            var p0Spell = new CardInstance(new InstanceId(3), OngoingSpell, state.Players[0].Id)
                { Zone = Zone.Hand };
            var p1Spell = new CardInstance(new InstanceId(4), OngoingSpell, state.Players[1].Id)
                { Zone = Zone.Hand };
            state.Players[0].Hand.Add(p0Spell);
            state.Players[1].Hand.Add(p1Spell);
            state.NextInstanceCounter = 5;
            var processor = new CommandProcessor(db);

            _ = Play(state, processor, p0Spell);
            InstanceId p0Ongoing = state.OngoingEffects.Single().Id;
            CommandResult firstP1Turn = Accept(processor.Execute(state,
                new EndTurnIntent(state.Players[0].Id)));
            _ = Play(state, processor, p1Spell);
            InstanceId p1Ongoing = state.OngoingEffects.Single(e => e.Id != p0Ongoing).Id;
            CommandResult firstP0Turn = Accept(processor.Execute(state,
                new EndTurnIntent(state.Players[1].Id)));
            CommandResult secondP1Turn = Accept(processor.Execute(state,
                new EndTurnIntent(state.Players[0].Id)));

            InstanceId[] Triggered(CommandResult result) => result.Events
                .Where(e => e.Kind == GameEventKind.OngoingEffectTriggered)
                .Select(e => e.Source!.Value).ToArray();

            switch (owner)
            {
                case TurnOwner.CONTROLLER:
                    Assert.That(Triggered(firstP1Turn), Is.Empty);
                    Assert.That(Triggered(firstP0Turn), Is.EqualTo(new[] { p0Ongoing }));
                    Assert.That(Triggered(secondP1Turn), Is.EqualTo(new[] { p1Ongoing }));
                    break;
                case TurnOwner.OPPONENT:
                    Assert.That(Triggered(firstP1Turn), Is.EqualTo(new[] { p0Ongoing }));
                    Assert.That(Triggered(firstP0Turn), Is.EqualTo(new[] { p1Ongoing }));
                    Assert.That(Triggered(secondP1Turn), Is.EqualTo(new[] { p0Ongoing }));
                    break;
                case TurnOwner.ANY:
                    Assert.That(Triggered(firstP1Turn), Is.EqualTo(new[] { p0Ongoing }));
                    Assert.That(Triggered(firstP0Turn),
                        Is.EqualTo(new[] { p0Ongoing, p1Ongoing }),
                        "Incoming p0 sources resolve before the other side.");
                    Assert.That(Triggered(secondP1Turn),
                        Is.EqualTo(new[] { p1Ongoing, p0Ongoing }),
                        "Incoming p1 sources resolve before the other side.");
                    break;
            }
        }

        [Test]
        public void SpellCast_Observer_Does_Not_Observe_Its_Own_Creation_Cast()
        {
            var future = new RuleDefinition(new RuleId("next_spell"),
                new TriggeredActivation(Trigger.ON_SPELL_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new DamageAction(1, new TargetSpec("ENEMY_HERO")));
            var (state, processor, spell) = Match(Spell(OngoingSpell, future), Spell(PlainSpell));

            CommandResult first = Play(state, processor, spell);

            Assert.That(state.OngoingEffects.Single().RemainingUses, Is.EqualTo(1));
            Assert.That(first.Events.Any(e => e.Kind == GameEventKind.OngoingEffectTriggered), Is.False);

            CardInstance plain = state.Players[0].Hand.Single(c => c.DefinitionId == PlainSpell);
            CommandResult second = Play(state, processor, plain);

            Assert.That(state.OngoingEffects, Is.Empty);
            Assert.That(second.Events.Any(e => e.Kind == GameEventKind.OngoingEffectTriggered), Is.True);
        }

        [Test]
        public void Observer_Order_Is_Board_Then_Weapon_Then_Ongoing_For_Each_Side()
        {
            RuleDefinition observer = FutureRule("observe", Trigger.ON_TURN_END,
                SourceBoundLifetime.Instance, TurnOwner.ANY);
            RuleDefinition ongoingRule = FutureRule("ongoing", Trigger.ON_TURN_END,
                PermanentLifetime.Instance, TurnOwner.ANY);
            var boardDef = Minion("board", observer);
            var weaponDef = Weapon("weapon", observer);
            var ongoingDef = Spell(OngoingSpell, ongoingRule);
            var db = new FakeCardDatabase().Register(boardDef).Register(weaponDef).Register(ongoingDef);
            GameState state = EmptyState();
            PutPermanent(state.Players[0], boardDef, new InstanceId(10), Zone.Board);
            PutPermanent(state.Players[0], weaponDef, new InstanceId(11), Zone.Weapon);
            state.OngoingEffects.Add(OngoingEffectInstance.CreatePermanent(
                new InstanceId(12), state.Players[0].Id, OngoingSpell, "ongoing"));
            PutPermanent(state.Players[1], boardDef, new InstanceId(20), Zone.Board);
            PutPermanent(state.Players[1], weaponDef, new InstanceId(21), Zone.Weapon);
            state.OngoingEffects.Add(OngoingEffectInstance.CreatePermanent(
                new InstanceId(22), state.Players[1].Id, OngoingSpell, "ongoing"));

            var ids = OrderedTriggerSources.Snapshot(state, db, state.Players[0].Id,
                Trigger.ON_TURN_END).Select(s => s.Identity).ToArray();

            Assert.That(ids, Is.EqualTo(new[]
            {
                new InstanceId(10), new InstanceId(11), new InstanceId(12),
                new InstanceId(20), new InstanceId(21), new InstanceId(22),
            }));

            var events = new EventBuffer(state);
            var interpreter = new EffectInterpreter(state, db, events);
            interpreter.RaiseObservers(Trigger.ON_TURN_END, excluded: null);
            interpreter.Drain();

            Assert.That(events.Events.Where(e => e.Kind == GameEventKind.DamageApplied)
                .Select(e => e.Source), Is.EqualTo(ids.Select(id => (InstanceId?)id)),
                "Resolved event sources must preserve board → weapon → ongoing for each side.");
        }

        [Test]
        public void Ordinary_Zones_Are_Not_Trigger_Sources_When_Ongoing_Instance_Exists()
        {
            RuleDefinition future = FutureRule("future", Trigger.ON_TURN_END,
                PermanentLifetime.Instance, TurnOwner.ANY);
            CardDefinition def = Spell(OngoingSpell, future);
            var db = new FakeCardDatabase().Register(def);
            GameState state = EmptyState();
            var resolvedSpell = new CardInstance(new InstanceId(10), OngoingSpell, state.Players[0].Id)
                { Zone = Zone.Graveyard };
            state.Players[0].Graveyard.Add(resolvedSpell);
            state.OngoingEffects.Add(OngoingEffectInstance.CreatePermanent(
                new InstanceId(11), state.Players[0].Id, OngoingSpell, "future"));

            var sources = OrderedTriggerSources.Snapshot(state, db, state.Players[0].Id,
                Trigger.ON_TURN_END);

            Assert.That(sources.Select(s => s.Identity), Is.EqualTo(new[] { new InstanceId(11) }));
            Assert.That(sources.Single().CurrentCarrier, Is.Null);
        }

        private static RuleDefinition FutureRule(string id, Trigger trigger, RuleLifetime lifetime,
            TurnOwner owner)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(trigger),
                trigger == Trigger.ON_TURN_START ? new TurnOwnerCondition(owner) : AlwaysCondition.Instance,
                lifetime, new DamageAction(1, new TargetSpec("ENEMY_HERO")));

        private static CardDefinition Spell(string id, params RuleDefinition[] rules)
            => Definition(id, CardType.SPELL, null, null, rules);

        private static CardDefinition Minion(string id, params RuleDefinition[] rules)
            => Definition(id, CardType.MINION, 1, 3, rules);

        private static CardDefinition Weapon(string id, params RuleDefinition[] rules)
            => new CardDefinition(id, id, CardType.WEAPON, 0, 1, null, 2,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + id, SetId.CORE, true, null, "", false);

        private static CardDefinition Definition(string id, CardType type, int? attack, int? health,
            RuleDefinition[] rules)
            => new CardDefinition(id, id, type, 0, attack, health, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + id, SetId.CORE, true, null, "", false);

        private static (GameState state, CommandProcessor processor, CardInstance spell) Match(
            CardDefinition spellDefinition, params CardDefinition[] additional)
        {
            var db = new FakeCardDatabase().Register(spellDefinition)
                .Register(Minion(Vanilla));
            foreach (CardDefinition definition in additional) db.Register(definition);
            GameState state = EmptyState();
            var spell = new CardInstance(new InstanceId(3), spellDefinition.Id, state.Players[0].Id)
                { Zone = Zone.Hand };
            state.Players[0].Hand.Add(spell);
            int nextId = 4;
            foreach (CardDefinition definition in additional)
            {
                state.Players[0].Hand.Add(new CardInstance(new InstanceId(nextId++), definition.Id,
                    state.Players[0].Id) { Zone = Zone.Hand });
            }
            state.NextInstanceCounter = nextId;
            return (state, new CommandProcessor(db), spell);
        }

        private static GameState EmptyState()
        {
            var p0 = new PlayerId("p0");
            var p1 = new PlayerId("p1");
            var state = new GameState(new MatchId("ongoing"), new DeterministicRandom(1),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)))
            {
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
                NextInstanceCounter = 3,
            };
            state.Players[0].Mana = new ManaPool { Max = 10, Available = 10 };
            state.Players[1].Mana = new ManaPool { Max = 10, Available = 10 };
            return state;
        }

        private static CommandResult Play(GameState state, CommandProcessor processor, CardInstance card)
            => Accept(processor.Execute(state, new PlayCardIntent(state.ActivePlayer, card.Id, 0,
                Array.Empty<TargetRef>())));

        private static CommandResult Accept(CommandResult result)
        {
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return result;
        }

        private static void PutPermanent(PlayerState player, CardDefinition definition,
            InstanceId id, Zone zone)
        {
            var card = new CardInstance(id, definition.Id, player.Id)
            {
                Zone = zone,
                CurrentAttack = definition.Attack ?? 0,
                CurrentHealth = definition.Health ?? 0,
                MaxHealth = definition.Health ?? 0,
                CurrentDurability = definition.Durability,
            };
            if (zone == Zone.Board)
                player.Board.Add(card);
            else
                player.Weapon = card;
        }
    }
}
