using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Interpreter
{
    /// <summary>
    /// The CONTROLS condition — "If you control another Construct, …".
    ///
    /// <para><b>The failure mode these exist for is a condition that silently always holds.</b>
    /// <c>EnterBoard</c> runs before the <c>ON_PLAY</c> raise, so a Construct asking whether you
    /// control a Construct finds ITSELF unless the source is excluded — and a card that always
    /// pays out looks exactly like a card that works. Nothing on the board, in the log or on the
    /// face says otherwise, which is why the empty-board case is the one worth pinning.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class ControlsConditionTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void Controls_Does_Not_Count_The_Source_Playing_Itself()
        {
            var db = new FakeCardDatabase();
            CardDefinition construct = Constructor(db);
            GameState state = State(db);
            CardInstance played = Hand(state, 0, construct);

            CommandResult result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(P0, played.Id, 0, System.Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(played.CurrentAttack, Is.EqualTo(4), "the only Construct on the board is itself");
            Assert.That(played.CurrentHealth, Is.EqualTo(4));
            Assert.That(state.Players[0].Hand, Is.Empty, "no draw either");
        }

        [Test]
        public void Controls_Holds_With_Another_Friendly_Minion_Of_That_Tribe()
        {
            var db = new FakeCardDatabase();
            CardDefinition construct = Constructor(db);
            CardDefinition cog = Tribal(db, "cog", Tribe.CONSTRUCT);
            GameState state = State(db);
            Board(state, db, 0, cog);
            CardInstance played = Hand(state, 0, construct);
            CardInstance top = Deck(state, 0, cog);

            CommandResult result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(P0, played.Id, 1, System.Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(played.CurrentAttack, Is.EqualTo(6));
            Assert.That(played.CurrentHealth, Is.EqualTo(6));
            Assert.That(state.Players[0].Hand, Is.EqualTo(new[] { top }));
        }

        [Test]
        public void Controls_Reads_Your_Board_Only()
        {
            var db = new FakeCardDatabase();
            CardDefinition construct = Constructor(db);
            CardDefinition cog = Tribal(db, "cog", Tribe.CONSTRUCT);
            GameState state = State(db);
            Board(state, db, 1, cog);                       // the OPPONENT's Construct
            CardInstance played = Hand(state, 0, construct);
            Deck(state, 0, cog);

            CommandResult result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(P0, played.Id, 0, System.Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(played.CurrentAttack, Is.EqualTo(4));
            Assert.That(state.Players[0].Hand, Is.Empty);
        }

        [Test]
        public void Controls_Reads_The_Tribe_It_Names_And_No_Other()
        {
            var db = new FakeCardDatabase();
            CardDefinition construct = Constructor(db);
            CardDefinition wolf = Tribal(db, "wolf", Tribe.BEAST);
            GameState state = State(db);
            Board(state, db, 0, wolf);
            CardInstance played = Hand(state, 0, construct);
            Deck(state, 0, wolf);

            CommandResult result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(P0, played.Id, 1, System.Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(played.CurrentAttack, Is.EqualTo(4));
            Assert.That(state.Players[0].Hand, Is.Empty);
        }

        // ── fixtures ────────────────────────────────────────────────────────────

        /// <summary>A 4/4 Construct that gains +2/+2 and draws when you control another one.</summary>
        private static CardDefinition Constructor(FakeCardDatabase db)
        {
            var condition = new ControlsCondition(Tribe.CONSTRUCT, excludesSource: true);
            var rules = new[]
            {
                new RuleDefinition(new RuleId("rule_1"), new TriggeredActivation(Trigger.ON_PLAY),
                    condition, OneShotLifetime.Instance,
                    new ModifyStatsAction(2, 2, new TargetSpec("SELF", TargetSelection.Of(1)))),
                new RuleDefinition(new RuleId("rule_2"), new TriggeredActivation(Trigger.ON_PLAY),
                    condition, OneShotLifetime.Instance, new DrawAction(1)),
            };
            int id = FixtureCard.Id("controls_constructor");
            var def = new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id),
                CardType.MINION, 4, 4, 4, null, CardClass.NEUTRAL, Rarity.EPIC,
                System.Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id),
                SetId.CORE, true, Tribe.CONSTRUCT, "");
            db.Register(def);
            return def;
        }

        private static CardDefinition Tribal(FakeCardDatabase db, string mnemonic, Tribe tribe)
        {
            int id = FixtureCard.Id("controls_" + mnemonic);
            var def = new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id),
                CardType.MINION, 1, 1, 1, null, CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(), System.Array.Empty<RuleDefinition>(), "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, tribe, "");
            db.Register(def);
            return def;
        }

        private static CardInstance Hand(GameState state, int seat, CardDefinition definition)
        {
            var card = new CardInstance(state.NextInstanceId(), definition.Id, state.Players[seat].Id)
            {
                Zone = Zone.Hand,
            };
            state.Players[seat].Hand.Add(card);
            return card;
        }

        private static CardInstance Deck(GameState state, int seat, CardDefinition definition)
        {
            var card = new CardInstance(state.NextInstanceId(), definition.Id, state.Players[seat].Id)
            {
                Zone = Zone.Deck,
            };
            state.Players[seat].Deck.Add(card);
            return card;
        }

        private static CardInstance Board(GameState state, ICardDatabase db, int seat,
            CardDefinition definition)
        {
            var card = new CardInstance(state.NextInstanceId(), definition.Id, state.Players[seat].Id);
            Assert.That(RulesEngine.EnterBoard(state, card, definition, seat, -1, db), Is.True);
            return card;
        }

        private static GameState State(ICardDatabase db)
        {
            var state = new GameState(new MatchId("controls-condition"), new DeterministicRandom(19),
                new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.CLOCKWRIGHT, 30)),
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
