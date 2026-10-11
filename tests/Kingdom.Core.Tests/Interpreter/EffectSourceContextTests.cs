using System;
using System.Linq;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class EffectSourceContextTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void Source_Context_Copies_Stable_Facts_While_Retaining_The_Optional_Carrier()
        {
            RuleDefinition rule = Rule("original_rule", Trigger.ON_TURN_END);
            CardDefinition definition = Card(FixtureCard.Id("original_definition"), CardType.MINION, rule);
            var carrier = new CardInstance(new InstanceId(17), definition.Id, P0)
            {
                Zone = Zone.Board,
            };
            carrier.Keywords.Add(Keyword.LIFEDRAIN);

            var context = new EffectSourceContext(carrier.Id, carrier.Controller, definition, rule,
                EffectSourceKind.BoardMinion, carrier);

            carrier.Controller = P1;
            carrier.DefinitionId = FixtureCard.Id("replacement_definition");
            carrier.Zone = Zone.Graveyard;
            carrier.Keywords.Clear();
            carrier.Keywords.Add(Keyword.VENOM);

            Assert.Multiple(() =>
            {
                Assert.That(context.Identity, Is.EqualTo(new InstanceId(17)));
                Assert.That(context.Controller, Is.EqualTo(P0));
                Assert.That(context.Definition, Is.SameAs(definition));
                Assert.That(context.Rule, Is.SameAs(rule));
                Assert.That(context.RuleReference,
                    Is.EqualTo(new RuleRef(FixtureCard.Id("original_definition"), new RuleId("original_rule"))));
                Assert.That(context.Kind, Is.EqualTo(EffectSourceKind.BoardMinion));
                Assert.That(context.SourceKeywords, Is.EqualTo(new[] { Keyword.LIFEDRAIN }),
                    "resolution-relevant riders are part of the immutable source snapshot");
                Assert.That(context.CurrentCarrier, Is.SameAs(carrier));
            });

            RuleDefinition ongoingRule = Rule("ongoing_rule", Trigger.ON_TURN_END);
            CardDefinition ongoingDefinition = Card(FixtureCard.Id("ongoing_definition"), CardType.SPELL,
                ongoingRule);
            var ongoing = new EffectSourceContext(new InstanceId(91), P1, ongoingDefinition,
                ongoingRule, EffectSourceKind.OngoingEffect, null);
            Assert.That(ongoing.CurrentCarrier, Is.Null,
                "an effect source must not need a throwaway CardInstance carrier");
        }

        [Test]
        public void Trigger_Event_Context_Holds_Event_Facts_Separately_From_Source_Facts()
        {
            TriggerEventContext turn = TriggerEventContext.ForIncomingTurn(P1);
            TriggerEventContext sourced = turn.WithTriggerSource(new InstanceId(42));

            Assert.Multiple(() =>
            {
                Assert.That(turn.IncomingTurnPlayer, Is.EqualTo(P1));
                Assert.That(turn.TriggerSource, Is.Null);
                Assert.That(sourced.IncomingTurnPlayer, Is.EqualTo(P1));
                Assert.That(sourced.TriggerSource, Is.EqualTo(new InstanceId(42)));
            });
        }

        [Test]
        public void Spell_Source_Snapshots_Printed_Keywords_Not_The_Uninitialized_Zone_Carrier()
        {
            RuleDefinition rule = Rule("cast", Trigger.ON_CAST);
            var definition = new CardDefinition(FixtureCard.Id("lifedrain_spell"), "lifedrain_spell", "lifedrain_spell",
                CardType.SPELL, 1, null, null, null, CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.LIFEDRAIN }, new[] { rule }, "", "art/lifedrain_spell",
                SetId.CORE, true, null, "");
            var carrier = new CardInstance(new InstanceId(18), definition.Id, P0)
            {
                Zone = Zone.Graveyard,
            };

            var context = new EffectSourceContext(carrier.Id, carrier.Controller, definition, rule,
                EffectSourceKind.Spell, carrier);

            Assert.That(carrier.Keywords, Is.Empty,
                "ordinary deck/hand spell instances do not seed printed keywords");
            Assert.That(context.SourceKeywords, Is.EqualTo(new[] { Keyword.LIFEDRAIN }));
        }

        [Test]
        public void Snapshot_Orders_Incoming_Board_Then_Weapon_Then_Ongoing_And_Repeats_For_Other_Side()
        {
            RuleDefinition p1LeftA = Rule("p1_left_a", Trigger.ON_TURN_START);
            RuleDefinition p1LeftB = Rule("p1_left_b", Trigger.ON_TURN_START);
            RuleDefinition p1Right = Rule("p1_right", Trigger.ON_TURN_START);
            RuleDefinition p1Weapon = Rule("p1_weapon", Trigger.ON_TURN_START);
            RuleDefinition p0Left = Rule("p0_left", Trigger.ON_TURN_START);
            RuleDefinition p0Weapon = Rule("p0_weapon", Trigger.ON_TURN_START);
            var db = new FakeCardDatabase()
                .Register(Card(FixtureCard.Id("p1_left"), CardType.MINION, p1LeftA, p1LeftB))
                .Register(Card(FixtureCard.Id("p1_right"), CardType.MINION, p1Right))
                .Register(Card(FixtureCard.Id("p1_weapon"), CardType.WEAPON, p1Weapon))
                .Register(Card(FixtureCard.Id("p0_left"), CardType.MINION, p0Left))
                .Register(Card(FixtureCard.Id("p0_weapon"), CardType.WEAPON, p0Weapon));
            GameState state = State();
            PutBoard(state, 1, FixtureCard.Id("p1_left"), 40);
            PutBoard(state, 1, FixtureCard.Id("p1_right"), 20);
            PutWeapon(state, 1, FixtureCard.Id("p1_weapon"), 30);
            PutBoard(state, 0, FixtureCard.Id("p0_left"), 70);
            PutWeapon(state, 0, FixtureCard.Id("p0_weapon"), 60);

            (int id, PlayerId controller, string ruleId)[] ongoing =
            {
                (95, P0, "p0_ongoing_late"),
                (15, P1, "p1_ongoing_early"),
                (85, P0, "p0_ongoing_early"),
                (25, P1, "p1_ongoing_late"),
            };
            foreach (var item in ongoing)
            {
                RuleDefinition rule = Rule(item.ruleId, Trigger.ON_TURN_START);
                int definitionId = FixtureCard.Id(item.ruleId + "_definition");
                db.Register(Card(definitionId, CardType.SPELL, rule));
                state.OngoingEffects.Add(OngoingEffectInstance.CreatePermanent(
                    new InstanceId(item.id), item.controller, definitionId, item.ruleId));
            }

            var snapshot = OrderedTriggerSources.Snapshot(state, db, P1, Trigger.ON_TURN_START);

            Assert.That(snapshot.Select(s => s.RuleReference.RuleId.Value), Is.EqualTo(new[]
            {
                "p1_left_a", "p1_left_b", "p1_right", "p1_weapon",
                "p1_ongoing_early", "p1_ongoing_late",
                "p0_left", "p0_weapon", "p0_ongoing_early", "p0_ongoing_late",
            }), "board list order is authoritative; instance id only orders ongoing sources");
            Assert.That(snapshot.Select(s => s.Kind), Is.EqualTo(new[]
            {
                EffectSourceKind.BoardMinion, EffectSourceKind.BoardMinion,
                EffectSourceKind.BoardMinion, EffectSourceKind.EquippedWeapon,
                EffectSourceKind.OngoingEffect, EffectSourceKind.OngoingEffect,
                EffectSourceKind.BoardMinion, EffectSourceKind.EquippedWeapon,
                EffectSourceKind.OngoingEffect, EffectSourceKind.OngoingEffect,
            }));
        }

        [Test]
        public void Snapshot_Never_Observes_Hand_Deck_Or_Graveyard()
        {
            RuleDefinition rule = Rule("observed", Trigger.ON_TURN_END);
            CardDefinition definition = Card(FixtureCard.Id("observer"), CardType.MINION, rule);
            var db = new FakeCardDatabase().Register(definition);
            GameState state = State();
            PutBoard(state, 0, definition.Id, 10);
            PutZone(state.Players[0].Hand, P0, definition.Id, Zone.Hand, 11);
            PutZone(state.Players[0].Deck, P0, definition.Id, Zone.Deck, 12);
            PutZone(state.Players[0].Graveyard, P0, definition.Id, Zone.Graveyard, 13);

            var snapshot = OrderedTriggerSources.Snapshot(state, db, P0, Trigger.ON_TURN_END);

            Assert.That(snapshot.Select(s => s.Identity),
                Is.EqualTo(new[] { new InstanceId(10) }));
        }

        [Test]
        public void Source_Trigger_Capabilities_Are_Explicit_And_Fail_Closed()
        {
            Assert.Multiple(() =>
            {
                Assert.That(EffectSourceCapabilities.Supports(
                    EffectSourceKind.EquippedWeapon, Trigger.ON_TURN_START), Is.True);
                Assert.That(EffectSourceCapabilities.Supports(
                    EffectSourceKind.BoardMinion, Trigger.ON_ATTACK), Is.True,
                    "ON_ATTACK is live on minions too, since 2026-09-05");
                Assert.That(EffectSourceCapabilities.Supports(
                    EffectSourceKind.BoardMinion, Trigger.ON_CAST), Is.False);
                Assert.That(EffectSourceCapabilities.Supports(
                    (EffectSourceKind)999, Trigger.ON_TURN_START), Is.False);
                Assert.That(EffectSourceCapabilities.Supports(
                    EffectSourceKind.BoardMinion, (Trigger)999), Is.False);
            });

            GameState state = State();
            RuleDefinition castOnly = Rule("cast", Trigger.ON_CAST);
            CardDefinition malformed = Card(FixtureCard.Id("malformed"), CardType.MINION, castOnly);
            var db = new FakeCardDatabase().Register(malformed);
            PutBoard(state, 0, malformed.Id, 20);

            Assert.That(OrderedTriggerSources.Snapshot(state, db, P0, Trigger.ON_CAST), Is.Empty,
                "enumeration must use the same capability policy that validation uses");
        }

        [Test]
        public void Validator_Uses_The_Shared_Source_Trigger_Capability_Policy()
        {
            CardDefinition weapon = Card(FixtureCard.Id("turn_weapon"), CardType.WEAPON,
                Rule("turn_start", Trigger.ON_TURN_START));
            CardDefinition invalidMinion = Card(FixtureCard.Id("cast_minion"), CardType.MINION,
                Rule("cast", Trigger.ON_CAST));
            var db = new FakeCardDatabase().Register(weapon).Register(invalidMinion);

            ValidationReport weaponReport = new CardValidator().Validate(weapon, db);
            ValidationReport minionReport = new CardValidator().Validate(invalidMinion, db);

            // Both halves ask about C21 BY CODE rather than about the report as a whole. A synthetic
            // fixture row cannot be Ok and never could: FixtureCard mints above 1_000_000 to stay
            // clear of shipped ids, which puts it outside every per-type band C1B enforces, and it
            // carries rules with no authored text, which is C26. Neither says anything about the
            // trigger capability this test exists to pin, and asserting Ok made those two unrelated
            // rules able to fail it -- which is exactly what they did.
            Assert.Multiple(() =>
            {
                Assert.That(weaponReport.Issues.Any(i =>
                    i.Code == "C21-TRIGGER-SOURCE-UNSUPPORTED"), Is.False,
                    "a capability enumerated at runtime must also be accepted by validation; got: " +
                    string.Join(", ", weaponReport.Issues.Select(i => i.Code + " " + i.Message)));
                Assert.That(minionReport.Issues.Any(i =>
                    i.Code == "C21-TRIGGER-SOURCE-UNSUPPORTED"), Is.True,
                    "a capability rejected at runtime must also be rejected by validation");
            });
        }

        private static RuleDefinition Rule(string id, Trigger trigger)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(trigger),
                trigger == Trigger.ON_TURN_START
                    ? (RuleCondition)new TurnOwnerCondition(TurnOwner.ANY)
                    : AlwaysCondition.Instance,
                trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? (RuleLifetime)OneShotLifetime.Instance
                    : SourceBoundLifetime.Instance,
                new DrawAction(1));

        private static CardDefinition Card(int id, CardType type, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), type, 1,
                type == CardType.MINION || type == CardType.WEAPON ? 1 : (int?)null,
                type == CardType.MINION ? 1 : (int?)null,
                type == CardType.WEAPON ? 1 : (int?)null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static GameState State()
            => new GameState(new MatchId("source-context"), new DeterministicRandom(3),
                new PlayerState(P0,
                    new HeroInstance(new InstanceId(1), P0, CardClass.EMBERWARD, 30)),
                new PlayerState(P1,
                    new HeroInstance(new InstanceId(2), P1, CardClass.TIDECALLER, 30)));

        private static void PutBoard(GameState state, int seat, int definitionId, int id)
        {
            var card = new CardInstance(new InstanceId(id), definitionId, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                Position = state.Players[seat].Board.Count,
            };
            state.Players[seat].Board.Add(card);
        }

        private static void PutWeapon(GameState state, int seat, int definitionId, int id)
            => state.Players[seat].Weapon = new CardInstance(new InstanceId(id), definitionId,
                state.Players[seat].Id)
            { Zone = Zone.Weapon };

        private static void PutZone(System.Collections.Generic.List<CardInstance> zone,
            PlayerId controller, int definitionId, Zone cardZone, int id)
            => zone.Add(new CardInstance(new InstanceId(id), definitionId, controller)
            {
                Zone = cardZone,
            });
    }
}
