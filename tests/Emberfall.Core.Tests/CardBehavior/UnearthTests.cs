using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using NUnit.Framework;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardBehavior
{
    [TestFixture]
    [Category("Unit")]
    public sealed class UnearthTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void Gear_Cache_Is_The_First_Shipped_Unearth_Card()
        {
            CardDefinition gearCache = ShippedCards.Db.Get(ShippedCards.Id("clock_gear_cache"));
            // Single() was right until the card gained a second rule. Selecting the UNEARTH rule by
            // its action keeps this about Unearth rather than about how many rules the card has —
            // the latter is a balance decision and not this fixture's business.
            RuleDefinition rule = gearCache.Rules.Single(r => r.Action is UnearthAction);

            Assert.Multiple(() =>
            {
                // NO NUMBER on the face, and a NOUN. The offer size is UnearthAction.Options, so
                // printing it would restate a constant as though the card chose it; the object is
                // printed anyway because the clause has to compose — this card's own text is the
                // proof, since "Unearth and draw a card" is not English.
                Assert.That(gearCache.Text, Does.StartWith("Unearth a card"));
                Assert.That(gearCache.Text, Does.Not.Match(@"Unearth\s+\d"),
                    "the offer size is a constant and must never reach the face");
                Assert.That(rule.Activation, Is.TypeOf<TriggeredActivation>());
                Assert.That(((TriggeredActivation)rule.Activation).Trigger, Is.EqualTo(Trigger.ON_CAST));
            });
        }

        [Test]
        public void Offer_Uses_Canonical_Eligible_Pool_And_Preserves_Rng_Order()
        {
            CardDefinition a = Card(FixtureCard.Id("a"));
            CardDefinition b = Card(FixtureCard.Id("b"));
            CardDefinition c = Card(FixtureCard.Id("c"));
            CardDefinition z = Card(FixtureCard.Id("z"));
            CardDefinition notCollectible = Card(FixtureCard.Id("token"), collectible: false);
            CardDefinition held = Card(FixtureCard.Id("held"), collectible: false);
            // FOUR eligible rows against an offer of three, so the sample is a strict subset and
            // this still exercises "stop at Options" as well as the order. The pool being larger
            // than the offer is the case that used to be reached by authoring count: 5.
            var db = new PoolDatabase(new[] { z, held, c, b, notCollectible, a, c });
            var (state, processor, source) = Ready(db, seed: 1UL);
            var expectedRng = new DeterministicRandom(1UL);
            // Sorted, because the canonical pool is NUMERIC order. It used to be the ordinal order
            // of the handles, where "a","b","c","z" was already canonical and the sort was invisible
            // -- the numeric card id moved it and left this list quietly modelling the old key.
            // ActionHandlers.CanonicalEligiblePool carries the same note.
            var remaining = FixtureCard.Ids("a", "b", "c", "z");
            remaining.Sort();
            var expected = new List<int>();
            while (expected.Count < UnearthAction.Options && remaining.Count > 0)
            {
                int index = expectedRng.NextInt(remaining.Count);
                expected.Add(remaining[index]);
                remaining.RemoveAt(index);
            }

            CommandResult result = processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.PendingChoice!.Options, Is.EqualTo(expected));
            // The point of this line is that nothing SORTS the sample after rolling it. Spelling
            // the winning order out by mnemonic restated `expected` and pinned a permutation that
            // the id change alone could invalidate; asserting it is not ascending says the actual
            // claim, and the fixed seed keeps it deterministic.
            Assert.That(state.PendingChoice.Options, Is.Not.Ordered.Ascending,
                "sample order is the RNG roll order, not a post-sample sort");
            Assert.That(state.PendingChoice.Options.Distinct().Count(),
                Is.EqualTo(state.PendingChoice.Options.Count));
            Assert.That(state.Rng.RawState, Is.EqualTo(expectedRng.RawState));
            Assert.That(result.Events.Count(e => e.Kind == GameEventKind.UnearthOffered), Is.EqualTo(1));
            GameEvent offered = result.Events.Single(e => e.Kind == GameEventKind.UnearthOffered);
            Assert.That((offered.Source, offered.Target, offered.Amount),
                Is.EqualTo((source.Id, (InstanceId?)null, (int?)null)));
            Assert.That(state.PendingChoice.ChoiceSequence, Is.EqualTo(offered.Sequence));
        }

        /// <summary>
        /// A pool SMALLER than the offer still opens a choice, of whatever the pool has. This is
        /// what remains of `Invalid_Count_Fails_Closed_Without_Rng_Or_Offer`, deleted 2026-09-04
        /// with the authored count it tested: there is no longer a number on the card that can be
        /// zero or negative, so the only way the offer can come up short is the pool, and coming up
        /// short is legal rather than a failure. Empty is the fail-closed case, covered below.
        /// </summary>
        [Test]
        public void A_Pool_Smaller_Than_The_Offer_Offers_What_It_Has()
        {
            var db = new PoolDatabase(new[] { Card(FixtureCard.Id("eligible")) });
            var (state, processor, source) = Ready(db, seed: 91UL);

            CommandResult result = processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.PendingChoice, Is.Not.Null);
            Assert.That(state.PendingChoice!.Options, Has.Count.EqualTo(1),
                "one eligible row means a one-option offer, not a refusal");
            Assert.That(result.Events.Count(e => e.Kind == GameEventKind.UnearthOffered), Is.EqualTo(1));
        }

        [Test]
        public void Empty_Eligible_Pool_Fails_Closed_Without_Rng_Or_Offer()
        {
            var db = new PoolDatabase(new[]
            {
                Card(FixtureCard.Id("not_collectible"), collectible: false),
                Card(FixtureCard.Id("held"), collectible: false),
            });
            var (state, processor, source) = Ready(db, seed: 92UL);
            ulong rngBefore = state.Rng.RawState;

            CommandResult result = processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.Rng.RawState, Is.EqualTo(rngBefore));
            Assert.That(result.Events.Any(e => e.Kind == GameEventKind.UnearthOffered), Is.False);
        }

        [Test]
        public void Choice_Sequence_Is_Monotonic_Across_Offers()
        {
            var db = new PoolDatabase(new[] { Card(FixtureCard.Id("a")), Card(FixtureCard.Id("b")), Card(FixtureCard.Id("c")) });
            var (state, processor, firstSource) = Ready(db, seed: 2UL);
            CommandResult firstOffer = processor.Execute(state,
                new PlayCardIntent(P0, firstSource.Id, 0, Array.Empty<TargetRef>()));
            ulong firstSequence = state.PendingChoice!.ChoiceSequence;
            Assert.That(processor.Execute(state, new UnearthChoiceIntent(P0,
                state.PendingChoice.Options[0], firstSequence)).Accepted, Is.True);

            var secondSource = new CardInstance(state.NextInstanceId(), FixtureCard.Id("unearth_source"), P0)
            { Zone = Zone.Hand };
            state.Players[0].Hand.Add(secondSource);
            CommandResult secondOffer = processor.Execute(state,
                new PlayCardIntent(P0, secondSource.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(secondOffer.Accepted, Is.True);
            Assert.That(state.PendingChoice!.ChoiceSequence, Is.GreaterThan(firstSequence));
            Assert.That(state.PendingChoice.ChoiceSequence,
                Is.EqualTo(secondOffer.Events.Single(e => e.Kind == GameEventKind.UnearthOffered).Sequence));
            Assert.That(firstOffer.Events.Single(e => e.Kind == GameEventKind.UnearthOffered).Sequence,
                Is.EqualTo(firstSequence));
        }

        [Test]
        public void Pending_Choice_Order_And_Sequence_Are_Hash_Canonical()
        {
            GameState state = MainState(93UL);
            state.PendingChoice = new PendingChoice
            {
                Chooser = P0,
                Kind = PendingChoiceKind.Unearth,
                Source = state.Players[0].Hero.Id,
                ChoiceSequence = 51,
                Options = { FixtureCard.Id("choice_first"), FixtureCard.Id("choice_second"), FixtureCard.Id("choice_third") },
            };
            string baseline = GameStateHasher.Hash(state);

            (state.PendingChoice.Options[0], state.PendingChoice.Options[1]) =
                (state.PendingChoice.Options[1], state.PendingChoice.Options[0]);
            Assert.That(GameStateHasher.Hash(state), Is.Not.EqualTo(baseline),
                "option order changes which choice AutoConfirm will make");

            (state.PendingChoice.Options[0], state.PendingChoice.Options[1]) =
                (state.PendingChoice.Options[1], state.PendingChoice.Options[0]);
            state.PendingChoice.ChoiceSequence++;
            Assert.That(GameStateHasher.Hash(state), Is.Not.EqualTo(baseline),
                "the stale-intent correlation id is canonical state");
        }

        [TestCaseSource(nameof(RejectedChoices))]
        public void Invalid_Choice_Is_Zero_Mutation_And_Zero_Events(
            Func<GameState, UnearthChoiceIntent> intentFactory, RejectReason expectedReason)
        {
            var db = new PoolDatabase(new[] { Card(FixtureCard.Id("a")), Card(FixtureCard.Id("b")), Card(FixtureCard.Id("c")) });
            var (state, processor, source) = Ready(db, seed: 3UL);
            Assert.That(processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>())).Accepted, Is.True);
            string before = GameStateHasher.Hash(state);
            ulong version = state.StateVersion;

            CommandResult result = processor.Execute(state, intentFactory(state));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(expectedReason));
            Assert.That(result.Events, Is.Empty);
            Assert.That(result.StateVersion, Is.EqualTo(version));
            Assert.That(GameStateHasher.Hash(state), Is.EqualTo(before));
        }

        private static IEnumerable<TestCaseData> RejectedChoices()
        {
            yield return new TestCaseData(
                new Func<GameState, UnearthChoiceIntent>(state => new UnearthChoiceIntent(
                    P1, state.PendingChoice!.Options[0], state.PendingChoice.ChoiceSequence)),
                RejectReason.NotChoiceOwner).SetName("Choice_rejects_wrong_player");
            yield return new TestCaseData(
                new Func<GameState, UnearthChoiceIntent>(state => new UnearthChoiceIntent(
                    P0, FixtureCard.Id("not_offered"), state.PendingChoice!.ChoiceSequence)),
                RejectReason.InvalidChoice).SetName("Choice_rejects_card_outside_exact_offer");
            yield return new TestCaseData(
                new Func<GameState, UnearthChoiceIntent>(state => new UnearthChoiceIntent(
                    P0, state.PendingChoice!.Options[0], state.PendingChoice.ChoiceSequence + 1)),
                RejectReason.StaleChoice).SetName("Choice_rejects_stale_sequence");
        }

        [Test]
        public void Choice_Without_Pending_Choice_Is_Rejected_Without_Mutation()
        {
            var state = MainState(4UL);
            var processor = new CommandProcessor(new PoolDatabase(new[] { Card(FixtureCard.Id("a")) }));
            string before = GameStateHasher.Hash(state);

            CommandResult result = processor.Execute(state, new UnearthChoiceIntent(P0, FixtureCard.Id("a"), 0));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(RejectReason.NoPendingChoice));
            Assert.That(result.Events, Is.Empty);
            Assert.That(GameStateHasher.Hash(state), Is.EqualTo(before));
        }

        [Test]
        public void Accepted_Choice_Creates_Exact_Card_Clears_Pending_And_Projects_Privately()
        {
            var db = new PoolDatabase(new[] { Card(FixtureCard.Id("a")), Card(FixtureCard.Id("b")), Card(FixtureCard.Id("c")) });
            var (state, processor, source) = Ready(db, seed: 5UL);
            Assert.That(processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>())).Accepted, Is.True);
            int chosen = state.PendingChoice!.Options[1];
            ulong sequence = state.PendingChoice.ChoiceSequence;

            CommandResult result = processor.Execute(state,
                new UnearthChoiceIntent(P0, chosen, sequence));

            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.Players[0].Hand, Has.Count.EqualTo(1));
            CardInstance created = state.Players[0].Hand[0];
            Assert.That((created.DefinitionId, created.Controller, created.Zone),
                Is.EqualTo((chosen, P0, Zone.Hand)));
            GameEvent createdEvent = result.Events.Single();
            Assert.That((createdEvent.Kind, createdEvent.Source, createdEvent.Target),
                Is.EqualTo((GameEventKind.CardCreatedInHand, (InstanceId?)created.Id, (InstanceId?)null)));

            GameEvent chooserEvent = ProjectEvent(createdEvent, processor, viewerSeat: 0);
            GameEvent opponentEvent = ProjectEvent(createdEvent, processor, viewerSeat: 1);
            Assert.That(chooserEvent.Source, Is.EqualTo(created.Id));
            Assert.That(opponentEvent.Source, Is.EqualTo(new InstanceId(0)));
            Assert.That(GameStateProjector.Project(state, P1).Players[0].Hand[0].DefinitionId, Is.Zero);
        }

        [Test]
        public void Pending_Choice_Locks_Unrelated_Commands_Including_EndTurn()
        {
            var db = new PoolDatabase(new[] { Card(FixtureCard.Id("a")), Card(FixtureCard.Id("b")) });
            var (state, processor, source) = Ready(db, seed: 7UL);
            Assert.That(processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>())).Accepted, Is.True);
            PlayerIntent[] unrelated =
            {
                new EndTurnIntent(P0),
                new PlayCardIntent(P0, new InstanceId(999), 0, Array.Empty<TargetRef>()),
                new AttackIntent(P0, state.Players[0].Hero.Id, new TargetRef(state.Players[1].Hero.Id)),
                new HeroPowerIntent(P0, Array.Empty<TargetRef>()),
                new MulliganIntent(P0, Array.Empty<InstanceId>()),
                new ConcedeIntent(P0),
            };

            foreach (PlayerIntent intent in unrelated)
            {
                string before = GameStateHasher.Hash(state);
                CommandResult rejected = processor.Execute(state, intent);
                Assert.That(rejected.Accepted, Is.False, intent.GetType().Name);
                Assert.That(rejected.Reason, Is.EqualTo(RejectReason.ChoicePending), intent.GetType().Name);
                Assert.That(rejected.Events, Is.Empty, intent.GetType().Name);
                Assert.That(GameStateHasher.Hash(state), Is.EqualTo(before), intent.GetType().Name);
            }
        }

        [Test]
        public void AutoConfirm_Selects_First_Offer_Without_Ending_Turn()
        {
            var db = new PoolDatabase(new[] { Card(FixtureCard.Id("a")), Card(FixtureCard.Id("b")), Card(FixtureCard.Id("c")) });
            var (state, processor, source) = Ready(db, seed: 11UL);
            Assert.That(processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>())).Accepted, Is.True);
            int first = state.PendingChoice!.Options[0];
            int turn = state.TurnNumber;

            CommandResult result = processor.Execute(state, new AutoConfirmIntent(P0));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.Players[0].Hand.Single().DefinitionId, Is.EqualTo(first));
            Assert.That((state.ActivePlayer, state.TurnNumber, state.Phase),
                Is.EqualTo((P0, turn, TurnPhase.Main)));
        }

        [Test]
        public void Full_Hand_Fizzles_The_Choice_Instead_Of_Deadlocking()
        {
            // A trigger elsewhere in the SAME event cascade can fill the last hand slot after the
            // offer already checked (fail-closed) it had room — RaiseObservers has no PendingChoice
            // awareness and keeps cascading. Rejecting the choice at that point would deadlock the
            // match: UnearthChoiceIntent and AutoConfirmIntent are the only two intents CanExecute
            // still allows while a choice is pending, and both would reject forever. Measured
            // 2026-09-05 in the EMBERWARD vs CLOCKWRIGHT matrix soak.
            var db = new PoolDatabase(new[] { Card(FixtureCard.Id("a")) });
            var state = MainState(13UL);
            for (int i = 0; i < MatchEngine.HandCap; i++)
                state.Players[0].Hand.Add(new CardInstance(state.NextInstanceId(), FixtureCard.Id($"filler_{i}"), P0)
                { Zone = Zone.Hand });
            state.PendingChoice = new PendingChoice
            {
                Chooser = P0,
                Kind = PendingChoiceKind.Unearth,
                Source = state.Players[0].Hero.Id,
                ChoiceSequence = 17,
                Options = { FixtureCard.Id("a") },
            };

            CommandResult result = new CommandProcessor(db).Execute(state,
                new UnearthChoiceIntent(P0, FixtureCard.Id("a"), 17));

            Assert.That(result.Accepted, Is.True);
            Assert.That(result.Events, Is.Empty);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.Players[0].Hand.Count, Is.EqualTo(MatchEngine.HandCap));
            Assert.That(state.Players[0].Hand.Any(c => c.DefinitionId == FixtureCard.Id("a")), Is.False);
        }

        [Test]
        public void Pending_Choice_Projection_Is_Default_Deny_And_Order_Preserving()
        {
            var db = new PoolDatabase(new[]
            {
                Card(FixtureCard.Id("hidden_option_alpha_731")),
                Card(FixtureCard.Id("hidden_option_beta_842")),
                Card(FixtureCard.Id("hidden_option_gamma_953")),
            });
            var (state, processor, source) = Ready(db, seed: 19UL);
            CommandResult result = processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>()));
            Assert.That(result.Accepted, Is.True);

            PendingChoice authoritative = state.PendingChoice!;
            PendingChoice chooser = GameStateProjector.Project(state, P0).PendingChoice!;
            PendingChoice opponent = GameStateProjector.Project(state, P1).PendingChoice!;
            Assert.That(chooser.Options, Is.EqualTo(authoritative.Options));
            Assert.That((chooser.Source, chooser.ChoiceSequence),
                Is.EqualTo((authoritative.Source, authoritative.ChoiceSequence)));
            Assert.That((opponent.Chooser, opponent.Kind),
                Is.EqualTo((P0, PendingChoiceKind.Unearth)));
            Assert.That(opponent.Options, Is.Empty);
            Assert.That((opponent.Source.Value, opponent.ChoiceSequence), Is.EqualTo((0, 0UL)));

            byte[] opponentBytes = Serialize(GameStateProjector.Project(state, P1));
            foreach (int option in authoritative.Options)
                Assert.That(Encoding.UTF8.GetString(opponentBytes), Does.Not.Contain(option));
        }

        private static GameEvent ProjectEvent(GameEvent gameEvent, CommandProcessor processor, int viewerSeat)
            => GameStateProjector.ProjectEvents(new[] { gameEvent }, processor.LastEventVisibility, viewerSeat)[0];

        private static byte[] Serialize(GameState state)
        {
            var sink = new BufferSink();
            GameStateSerializer.Serialize(state, sink);
            return sink.ToArray();
        }

        private static (GameState state, CommandProcessor processor, CardInstance source) Ready(
            PoolDatabase db, ulong seed)
        {
            CardDefinition sourceDefinition = Card(FixtureCard.Id("unearth_source"), CardType.SPELL,
                rules: new[] { Rule("unearth", Trigger.ON_CAST, new UnearthAction()) });
            db.Register(sourceDefinition);
            GameState state = MainState(seed);
            var source = new CardInstance(state.NextInstanceId(), sourceDefinition.Id, P0) { Zone = Zone.Hand };
            state.Players[0].Hand.Add(source);
            return (state, new CommandProcessor(db), source);
        }

        private static GameState MainState(ulong seed)
        {
            var state = new GameState(new MatchId("unearth"), new DeterministicRandom(seed),
                new PlayerState(P0, new HeroInstance(new InstanceId(1), P0, CardClass.GRAVEKEEP, 30)),
                new PlayerState(P1, new HeroInstance(new InstanceId(2), P1, CardClass.EMBERWARD, 30)))
            {
                ActivePlayer = P0,
                Phase = TurnPhase.Main,
                TurnNumber = 3,
                Mulligan0Done = true,
                Mulligan1Done = true,
                NextInstanceCounter = 3,
            };
            state.Players[0].Mana.Max = state.Players[0].Mana.Available = 10;
            return state;
        }

        private static RuleDefinition Rule(string id, Trigger trigger, RuleAction action)
            => new RuleDefinition(new RuleId(id), new TriggeredActivation(trigger), AlwaysCondition.Instance,
                trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? (RuleLifetime)OneShotLifetime.Instance
                    : SourceBoundLifetime.Instance,
                action);

        // `held: true` used to set requiresEngineeringPrimitive, the second way a row could be
        // excluded from the canonical pool. NOR-224 retired that flag, so collectible:false is the
        // only exclusion left and both fixtures below express it that way.
        private static CardDefinition Card(int id, CardType type = CardType.MINION,
            bool collectible = true, IReadOnlyList<RuleDefinition>? rules = null)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), type, 1,
                type == CardType.MINION || type == CardType.WEAPON ? 1 : (int?)null,
                type == CardType.MINION ? 1 : (int?)null,
                type == CardType.WEAPON ? 1 : (int?)null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                rules ?? Array.Empty<RuleDefinition>(), "", "art/" + FixtureCard.Handle(id), SetId.CORE,
                collectible, null, "");

        private sealed class PoolDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _definitions = new();
            private readonly List<CardDefinition> _pool;

            public PoolDatabase(IEnumerable<CardDefinition> pool)
            {
                _pool = new List<CardDefinition>(pool);
                foreach (CardDefinition definition in _pool) Register(definition);
            }

            public void Register(CardDefinition definition) => _definitions[definition.Id] = definition;

            public bool TryGet(int cardId, out CardDefinition def)
                => _definitions.TryGetValue(cardId, out def!);

            public CardDefinition Get(int cardId) => _definitions[cardId];

            public IReadOnlyCollection<CardDefinition> AllCollectible() => _pool;
        }
    }
}
