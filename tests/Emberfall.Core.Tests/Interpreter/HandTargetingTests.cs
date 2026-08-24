using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

// This fixture sits under Emberfall.Core.Tests, whose own Targeting namespace shadows the
// engine's. Name the engine seam explicitly.
using Engine = Emberfall.Core.Targeting;

namespace Emberfall.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class HandTargetingTests
    {
        [Test]
        public void Destroy_Is_A_Validated_Live_Hand_Card_Action()
        {
            CardDefinition discard = DiscardSpell();
            var db = new FakeCardDatabase().Register(discard);

            ValidationReport report = new CardValidator().Validate(discard, db);

            Assert.That(report.Ok, Is.True,
                string.Join("; ", report.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        }

        [Test]
        public void Destroying_A_Chosen_Hand_Card_Discards_It_Without_Offering_The_Source_Spell()
        {
            CardDefinition body = Minion(FixtureCard.Id("body"));
            CardDefinition discard = DiscardSpell();
            var db = new FakeCardDatabase().Register(body).Register(discard);
            GameState state = State();
            CardInstance victim = Hand(state, 0, body);
            CardInstance source = Hand(state, 0, discard);

            GameState rejectedState = State();
            CardInstance rejectedSource = Hand(rejectedState, 0, discard);
            CommandResult rejected = new CommandProcessor(db).Execute(rejectedState,
                new PlayCardIntent(rejectedState.Players[0].Id, rejectedSource.Id, 0,
                    new[] { new TargetRef(rejectedSource.Id, "discard_target") }));

            Assert.That(rejected.Accepted, Is.False);
            Assert.That(rejected.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(rejected.Events, Is.Empty);
            Assert.That(rejectedState.Players[0].Hand, Does.Contain(rejectedSource));
            Assert.That(rejectedState.Players[0].Graveyard, Is.Empty);

            var plan = Engine.PlayerChoicePlan(state, db, state.Players[0].Id,
                discard, Trigger.ON_CAST, source.Id);

            Assert.That(plan.Single().Candidates.Select(candidate => candidate.Instance),
                Is.EqualTo(new[] { victim.Id }),
                "the source spell is still in hand while choices are planned but cannot discard itself");

            CommandResult result = new CommandProcessor(db).Execute(state, new PlayCardIntent(
                state.Players[0].Id, source.Id, 0,
                new[] { new TargetRef(victim.Id, "discard_target") }));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Hand, Is.Empty);
            Assert.That(state.Players[0].Graveyard, Does.Contain(source));
            Assert.That(state.Players[0].Graveyard, Does.Contain(victim));
            Assert.That(victim.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(result.Events.Count(gameEvent =>
                gameEvent.Kind == GameEventKind.CardDestroyed && gameEvent.Source == victim.Id),
                Is.EqualTo(1));
        }

        private static CardDefinition DiscardSpell()
            // Validated by CardValidator below, so the id must sit in the SPELL band (C1B), and the
            // row must carry the sentence its rules read as (C26).
            => TestCards.Authored(Spell(FixtureCard.Id("discard", CardType.SPELL),
                TestRules.Rule("discard", Trigger.ON_CAST,
                    new DestroyAction(new TargetSpec("FRIENDLY_HAND", TargetSelection.Of(1), binding: "discard_target")))));

        private static CardDefinition Spell(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 1, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(), rules,
                "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Minion(int id)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 1, 1, 3, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                System.Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE,
                true, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var state = new GameState(new MatchId("hand-targeting"), new DeterministicRandom(42),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.NIGHTVEIL, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TEMPEST, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            state.Players[0].Mana.Max = state.Players[0].Mana.Available = 10;
            state.Players[1].Mana.Max = state.Players[1].Mana.Available = 10;
            return state;
        }

        private static CardInstance Hand(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = Zone.Hand,
            };
            state.Players[seat].Hand.Add(card);
            return card;
        }
    }
}
