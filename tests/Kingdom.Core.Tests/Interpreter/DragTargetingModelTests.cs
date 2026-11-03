using System.Linq;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class DragTargetingModelTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void Choice_Plan_Collects_PerBinding_Counts_And_Emits_Bound_TargetRefs()
        {
            var body = Minion(FixtureCard.Id("body"));
            var spell = Spell(FixtureCard.Id("split"), new[]
            {
                TestRules.Rule("first_hit", Trigger.ON_CAST,
                    new DamageAction(1, new TargetSpec("ANY_CHARACTER", TargetSelection.Of(2), binding: "first"))),
                TestRules.Rule("second_hit", Trigger.ON_CAST,
                    new DamageAction(1, new TargetSpec("ANY_CHARACTER", TargetSelection.Of(1), binding: "second", distinctFrom: "first"))),
            });
            var db = new FakeCardDatabase().Register(body).Register(spell);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), spell.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            CardInstance friendly = Board(state, 0, body);
            CardInstance enemy = Board(state, 1, body);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            PlayerIntent first = model.Resolve(source, PointerSubject.OfCard(enemy.Id, P1, Zone.Board),
                DropPoint.Board(0f));

            Assert.That(first, Is.Null);
            Assert.That(model.Phase, Is.EqualTo(DragTargetingModel.TargetingPhase.AwaitingEffectTarget));
            Assert.That((model.AwaitingBinding, model.AwaitingSelectedCount, model.AwaitingRequiredCount),
                Is.EqualTo(("first", 1, 2)));
            Assert.That(model.AwaitingTargets(), Does.Not.Contain(enemy.Id));

            PlayerIntent second = model.Resolve(DragSource.None,
                PointerSubject.OfCard(friendly.Id, P0, Zone.Board), DropPoint.Board(0f));

            Assert.That(second, Is.Null);
            Assert.That((model.AwaitingBinding, model.AwaitingSelectedCount, model.AwaitingRequiredCount),
                Is.EqualTo(("second", 0, 1)));
            Assert.That(model.AwaitingTargets(), Does.Not.Contain(enemy.Id));
            Assert.That(model.AwaitingTargets(), Does.Not.Contain(friendly.Id));

            var completed = model.Resolve(DragSource.None,
                PointerSubject.OfHero(state.Players[1].Hero.Id, P1), DropPoint.Board(0f)) as PlayCardIntent;

            Assert.That(completed, Is.Not.Null);
            Assert.That(model.Phase, Is.EqualTo(DragTargetingModel.TargetingPhase.Direct));
            Assert.That(completed!.Targets.Select(t => (t.Binding, t.Instance)), Is.EqualTo(new[]
            {
                ("first", enemy.Id),
                ("first", friendly.Id),
                ("second", state.Players[1].Hero.Id),
            }));
        }

        // ---- untargeted, no-slot cards play from anywhere but the hand (Sam, 2026-08-09) --------

        [Test]
        public void An_Untargeted_Spell_Casts_When_Dropped_On_Nothing()
        {
            var (model, source, _) = ArmCoin();

            var intent = model.Resolve(source, PointerSubject.None, DropPoint.Board(0f)) as PlayCardIntent;

            Assert.That(intent, Is.Not.Null, "no slot and no target means nowhere it is REQUIRED to land");
        }

        [Test]
        public void An_Untargeted_Spell_Casts_When_Dropped_On_An_Enemy_Card()
        {
            var (model, source, state) = ArmCoin();
            CardInstance enemyBody = Board(state, 1, Minion(FixtureCard.Id("body")));

            var intent = model.Resolve(source, PointerSubject.OfCard(enemyBody.Id, P1, Zone.Board),
                DropPoint.Board(0f)) as PlayCardIntent;

            Assert.That(intent, Is.Not.Null);
        }

        [Test]
        public void An_Untargeted_Spell_Casts_When_Dropped_On_Our_Own_Board()
        {
            // Only "back to the hand it came from" reads as changing your mind — dropping on our
            // own board is not that, so it still casts.
            var (model, source, state) = ArmCoin();
            CardInstance ourBody = Board(state, 0, Minion(FixtureCard.Id("body")));

            var intent = model.Resolve(source, PointerSubject.OfCard(ourBody.Id, P0, Zone.Board),
                DropPoint.Board(0f)) as PlayCardIntent;

            Assert.That(intent, Is.Not.Null);
        }

        [Test]
        public void An_Untargeted_Spell_Cancels_When_Dropped_Back_In_Our_Hand()
        {
            var (model, source, _) = ArmCoin();

            var intent = model.Resolve(source, PointerSubject.None, DropPoint.OwnHand);

            Assert.That(intent, Is.Null, "dropping it back where it came from is the one way to change your mind");
        }

        [Test]
        public void A_Vanilla_Minion_Dropped_On_Open_Board_Plays_At_That_X()
        {
            // REVERSED by Sam on 2026-08-12 (#139). This used to assert Is.Null: a minion needed a
            // slot collider under the pointer, and open board was a miss. There are no slot
            // colliders now — the row's x answers everywhere — so the only miss left is the hand.
            var body = Minion(FixtureCard.Id("body"));
            var db = new FakeCardDatabase().Register(body);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), body.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            var played = model.Resolve(source, PointerSubject.None, DropPoint.Board(2.7f)) as PlayCardIntent;
            var cancelled = model.Resolve(source, PointerSubject.None, DropPoint.OwnHand);

            Assert.That(played, Is.Not.Null, "open board is a placement now, not a miss");
            Assert.That(played!.BoardSlot, Is.EqualTo(0), "empty row: every x is position 0");
            Assert.That(cancelled, Is.Null, "back over the hand is the one release that means 'never mind'");
        }

        [Test]
        public void A_Card_The_Mirror_Has_Already_Played_Cannot_Be_Picked_Up()
        {
            // The descriptor still says Hand until the playback queue shows the play; the mirror
            // already knows the card is gone, and a second drag would queue a second play of it.
            var body = Minion(FixtureCard.Id("body"));
            var db = new FakeCardDatabase().Register(body);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), body.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            state.Players[0].Hand.Remove(card);

            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            Assert.That(source.Kind, Is.EqualTo(DragSourceKind.None));
        }

        [Test]
        public void An_Unaffordable_Card_Dropped_On_The_Board_Returns_To_Hand()
        {
            var body = Minion(FixtureCard.Id("unaffordable"));
            var db = new FakeCardDatabase().Register(body);
            GameState state = State();
            state.Players[0].Mana.Available = 0;
            var card = new CardInstance(state.NextInstanceId(), body.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            PlayerIntent intent = model.Resolve(source, PointerSubject.None, DropPoint.Board(0f));

            Assert.That(intent, Is.Null,
                "a play the engine must reject should cancel locally so the dragged card returns to its hand slot");
            Assert.That(model.Phase, Is.EqualTo(DragTargetingModel.TargetingPhase.Direct));
        }

        [Test]
        public void A_Targeted_Card_With_No_Legal_Target_Dropped_On_The_Board_Returns_To_Hand()
        {
            var spell = Spell(FixtureCard.Id("targetless"), new[]
            {
                TestRules.Rule("hit", Trigger.ON_CAST,
                    new DamageAction(1, new TargetSpec("ANY_ENEMY_MINION", TargetSelection.Of(1)))),
            });
            var db = new FakeCardDatabase().Register(spell);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), spell.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            PlayerIntent intent = model.Resolve(source, PointerSubject.None, DropPoint.Board(0f));

            Assert.That(intent, Is.Null,
                "a targeted card with no valid destination should cancel locally and return to hand");
            Assert.That(model.Phase, Is.EqualTo(DragTargetingModel.TargetingPhase.Direct));
        }

        [Test]
        public void A_Herald_With_An_Unpayable_Required_Target_Returns_To_Hand()
        {
            var herald = Minion(FixtureCard.Id("targetless-herald"), new[]
            {
                TestRules.Rule("hit", Trigger.ON_PLAY,
                    new DamageAction(1, new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1),
                        cost: CostKind.Required))),
            });
            var db = new FakeCardDatabase().Register(herald);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), herald.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            PlayerIntent intent = model.Resolve(source, PointerSubject.None, DropPoint.Board(0f));

            Assert.That(intent, Is.Null,
                "an unpayable required target must return the card to hand");
            Assert.That(model.Phase, Is.EqualTo(DragTargetingModel.TargetingPhase.Direct));
        }

        [Test]
        public void A_Minion_Whose_Only_Target_Is_Shrouded_Can_Play_While_Its_Effect_Fizzles()
        {
            var striker = Minion(FixtureCard.Id("striker"), new[]
            {
                TestRules.Rule("hit", Trigger.ON_PLAY,
                    new DamageAction(1, new TargetSpec("ANY_MINION", TargetSelection.Of(1)))),
            });
            var enemy = Minion(FixtureCard.Id("shrouded-enemy"));
            var db = new FakeCardDatabase().Register(striker).Register(enemy);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), striker.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            CardInstance shrouded = Board(state, 1, enemy);
            shrouded.Keywords.Add(Keyword.SHROUD);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            Assert.That(source.NeedsTarget, Is.False);
            var play = model.Resolve(source, PointerSubject.None, DropPoint.Board(0f)) as PlayCardIntent;
            Assert.That(play, Is.Not.Null, "the minion should still play when its damage fizzles");
            Assert.That(play!.Targets, Is.Empty);
            Assert.That(new CommandProcessor(db).CanExecute(state, play, out _), Is.True,
                "the presentation and game authority must agree about this targetless play");
        }

        // ---- minions can be placed anywhere in the row, not just past the end (Sam, 2026-08-09) ---

        [Test]
        public void A_Minion_Dropped_On_Our_Front_Minion_Inserts_Before_It()
        {
            var body = Minion(FixtureCard.Id("body"));
            var db = new FakeCardDatabase().Register(body);
            GameState state = State();
            CardInstance front = Board(state, 0, body);   // position 0
            CardInstance back = Board(state, 0, body);    // position 1
            var card = new CardInstance(state.NextInstanceId(), body.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            // Two minions on the row centre at x -0.675 and +0.675. Dropping on the front one means
            // dropping at ITS x, and the answer comes from that x, not from the card that was hit.
            var intent = model.Resolve(source, PointerSubject.OfCard(front.Id, P0, Zone.Board),
                DropPoint.Board(-BoardRowGeometry.SlotSpacing * 0.5f)) as PlayCardIntent;

            Assert.That(intent, Is.Not.Null, "dropping on an existing minion is a placement, not a miss");
            Assert.That(intent!.BoardSlot, Is.EqualTo(0), "lands where it was dropped, not past the end");
        }

        [Test]
        public void A_Minion_Dropped_On_Our_Back_Minion_Inserts_There_Too()
        {
            var body = Minion(FixtureCard.Id("body"));
            var db = new FakeCardDatabase().Register(body);
            GameState state = State();
            Board(state, 0, body);                        // position 0
            CardInstance back = Board(state, 0, body);     // position 1
            var card = new CardInstance(state.NextInstanceId(), body.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            var intent = model.Resolve(source, PointerSubject.OfCard(back.Id, P0, Zone.Board),
                DropPoint.Board(BoardRowGeometry.SlotSpacing * 0.5f)) as PlayCardIntent;

            Assert.That(intent, Is.Not.Null);
            Assert.That(intent!.BoardSlot, Is.EqualTo(1), "not forced to the very left slot");
        }

        [Test]
        public void A_Minion_Dropped_Over_The_Enemy_Board_Still_Plays_On_Ours()
        {
            var body = Minion(FixtureCard.Id("body"));
            var db = new FakeCardDatabase().Register(body);
            GameState state = State();
            CardInstance enemy = Board(state, 1, body);
            var card = new CardInstance(state.NextInstanceId(), body.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));

            // REVERSED by Sam on 2026-08-12 (#139): "dropping anything on the enemy's side of the
            // board should resolve; that shouldn't cancel". The drop's y is not read at all, so
            // releasing over their half places the minion on OUR row at that x.
            var intent = model.Resolve(source, PointerSubject.OfCard(enemy.Id, P1, Zone.Board),
                DropPoint.Board(0f)) as PlayCardIntent;

            Assert.That(intent, Is.Not.Null, "the opponent's half of the board is not a dead zone");
            Assert.That(intent!.BoardSlot, Is.EqualTo(0));
        }

        private static (DragTargetingModel model, DragSource source, GameState state) ArmCoin()
        {
            var coin = Spell(FixtureCard.Id("coin"), new[]
            {
                TestRules.Rule("gain_mana", Trigger.ON_CAST, new GainManaAction(1, ManaGainKind.TEMPORARY)),
            });
            var db = new FakeCardDatabase().Register(coin);
            GameState state = State();
            var card = new CardInstance(state.NextInstanceId(), coin.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(card);
            var model = new DragTargetingModel(P0, db) { State = state };
            DragSource source = model.BeginSource(PointerSubject.OfCard(card.Id, P0, Zone.Hand));
            Assert.That(source.NeedsTarget, Is.False, "the coin owes no chosen target");
            return (model, source, state);
        }

        private static GameState State()
        {
            var state = new GameState(new MatchId("drag-target-plan"), new DeterministicRandom(1),
                new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.NIGHTVEIL, 30)),
                new PlayerState(P1, new HeroInstance(new InstanceId(2), P1, CardClass.TEMPEST, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = P0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            state.Players[0].Mana.Available = state.Players[0].Mana.Max = 10;
            return state;
        }

        private static CardInstance Board(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                CurrentAttack = 1,
                CurrentHealth = 3,
                MaxHealth = 3,
                Position = state.Players[seat].Board.Count,
            };
            state.Players[seat].Board.Add(card);
            return card;
        }

        private static CardDefinition Minion(int id)
            => Minion(id, System.Array.Empty<RuleDefinition>());

        private static CardDefinition Minion(int id, RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 1, 1, 3, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE,
                true, null, "");

        private static CardDefinition Spell(int id, RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 1, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");
    }
}
