using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardBehavior
{
    [TestFixture]
    [Category("Unit")]
    public sealed class UnearthTests
    {
        private static readonly PlayerId P0 = new PlayerId("P0");
        private static readonly PlayerId P1 = new PlayerId("P1");

        [Test]
        public void Offer_Uses_Canonical_Eligible_Pool_And_Preserves_Rng_Order()
        {
            CardDefinition a = Card("a");
            CardDefinition b = Card("b");
            CardDefinition c = Card("c");
            CardDefinition z = Card("z");
            CardDefinition notCollectible = Card("token", collectible: false);
            CardDefinition held = Card("held", held: true);
            var db = new PoolDatabase(new[] { z, held, c, b, notCollectible, a, c });
            var (state, processor, source) = Ready(db, count: 5, seed: 1UL);
            var expectedRng = new DeterministicRandom(1UL);
            var remaining = new List<string> { "a", "b", "c", "z" };
            var expected = new List<string>();
            while (remaining.Count > 0)
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
            Assert.That(state.PendingChoice.Options, Is.EqualTo(new[] { "b", "c", "a", "z" }),
                "sample order is the RNG roll order, not a post-sample sort");
            Assert.That(state.PendingChoice.Options.Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(state.PendingChoice.Options.Count));
            Assert.That(state.Rng.RawState, Is.EqualTo(expectedRng.RawState));
            Assert.That(result.Events.Count(e => e.Kind == GameEventKind.UnearthOffered), Is.EqualTo(1));
            GameEvent offered = result.Events.Single(e => e.Kind == GameEventKind.UnearthOffered);
            Assert.That((offered.Source, offered.Target, offered.Amount),
                Is.EqualTo((source.Id, (InstanceId?)null, (int?)null)));
            Assert.That(state.PendingChoice.ChoiceSequence, Is.EqualTo(offered.Sequence));
        }

        [Test]
        public void Invalid_Count_Fails_Closed_Without_Rng_Or_Offer()
        {
            foreach (int count in new[] { -1, 0 })
            {
                var db = new PoolDatabase(new[] { Card("eligible") });
                var (state, processor, source) = Ready(db, count, seed: 91UL);
                ulong rngBefore = state.Rng.RawState;

                CommandResult result = processor.Execute(state,
                    new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>()));

                Assert.That(result.Accepted, Is.True);
                Assert.That(state.PendingChoice, Is.Null);
                Assert.That(state.Rng.RawState, Is.EqualTo(rngBefore));
                Assert.That(result.Events.Any(e => e.Kind == GameEventKind.UnearthOffered), Is.False);
            }
        }

        [Test]
        public void Empty_Eligible_Pool_Fails_Closed_Without_Rng_Or_Offer()
        {
            var db = new PoolDatabase(new[]
            {
                Card("not_collectible", collectible: false),
                Card("held", held: true),
            });
            var (state, processor, source) = Ready(db, count: 3, seed: 92UL);
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
            var db = new PoolDatabase(new[] { Card("a"), Card("b"), Card("c") });
            var (state, processor, firstSource) = Ready(db, count: 2, seed: 2UL);
            CommandResult firstOffer = processor.Execute(state,
                new PlayCardIntent(P0, firstSource.Id, 0, Array.Empty<TargetRef>()));
            ulong firstSequence = state.PendingChoice!.ChoiceSequence;
            Assert.That(processor.Execute(state, new UnearthChoiceIntent(P0,
                state.PendingChoice.Options[0], firstSequence)).Accepted, Is.True);

            var secondSource = new CardInstance(state.NextInstanceId(), "unearth_source", P0)
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
                Options = { "choice_first", "choice_second", "choice_third" },
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
            var db = new PoolDatabase(new[] { Card("a"), Card("b"), Card("c") });
            var (state, processor, source) = Ready(db, count: 2, seed: 3UL);
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
                    P0, "not_offered", state.PendingChoice!.ChoiceSequence)),
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
            var processor = new CommandProcessor(new PoolDatabase(new[] { Card("a") }));
            string before = GameStateHasher.Hash(state);

            CommandResult result = processor.Execute(state, new UnearthChoiceIntent(P0, "a", 0));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(RejectReason.NoPendingChoice));
            Assert.That(result.Events, Is.Empty);
            Assert.That(GameStateHasher.Hash(state), Is.EqualTo(before));
        }

        [Test]
        public void Accepted_Choice_Creates_Exact_Card_Clears_Pending_And_Projects_Privately()
        {
            var db = new PoolDatabase(new[] { Card("a"), Card("b"), Card("c") });
            var (state, processor, source) = Ready(db, count: 3, seed: 5UL);
            Assert.That(processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>())).Accepted, Is.True);
            string chosen = state.PendingChoice!.Options[1];
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
            Assert.That(GameStateProjector.Project(state, P1).Players[0].Hand[0].DefinitionId, Is.Empty);
        }

        [Test]
        public void Pending_Choice_Locks_Unrelated_Commands_Including_EndTurn()
        {
            var db = new PoolDatabase(new[] { Card("a"), Card("b") });
            var (state, processor, source) = Ready(db, count: 2, seed: 7UL);
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
            var db = new PoolDatabase(new[] { Card("a"), Card("b"), Card("c") });
            var (state, processor, source) = Ready(db, count: 3, seed: 11UL);
            Assert.That(processor.Execute(state,
                new PlayCardIntent(P0, source.Id, 0, Array.Empty<TargetRef>())).Accepted, Is.True);
            string first = state.PendingChoice!.Options[0];
            int turn = state.TurnNumber;

            CommandResult result = processor.Execute(state, new AutoConfirmIntent(P0));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.PendingChoice, Is.Null);
            Assert.That(state.Players[0].Hand.Single().DefinitionId, Is.EqualTo(first));
            Assert.That((state.ActivePlayer, state.TurnNumber, state.Phase),
                Is.EqualTo((P0, turn, TurnPhase.Main)));
        }

        [Test]
        public void Full_Hand_Rejects_Choice_Without_Mutation()
        {
            var db = new PoolDatabase(new[] { Card("a") });
            var state = MainState(13UL);
            for (int i = 0; i < MatchEngine.HandCap; i++)
                state.Players[0].Hand.Add(new CardInstance(state.NextInstanceId(), $"filler_{i}", P0)
                { Zone = Zone.Hand });
            state.PendingChoice = new PendingChoice
            {
                Chooser = P0,
                Kind = PendingChoiceKind.Unearth,
                Source = state.Players[0].Hero.Id,
                ChoiceSequence = 17,
                Options = { "a" },
            };
            string before = GameStateHasher.Hash(state);

            CommandResult result = new CommandProcessor(db).Execute(state,
                new UnearthChoiceIntent(P0, "a", 17));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(RejectReason.HandFull));
            Assert.That(result.Events, Is.Empty);
            Assert.That(GameStateHasher.Hash(state), Is.EqualTo(before));
        }

        [Test]
        public void Pending_Choice_Projection_Is_Default_Deny_And_Order_Preserving()
        {
            var db = new PoolDatabase(new[]
            {
                Card("hidden_option_alpha_731"),
                Card("hidden_option_beta_842"),
                Card("hidden_option_gamma_953"),
            });
            var (state, processor, source) = Ready(db, count: 3, seed: 19UL);
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
            foreach (string option in authoritative.Options)
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
            PoolDatabase db, int count, ulong seed)
        {
            CardDefinition sourceDefinition = Card("unearth_source", CardType.SPELL,
                rules: new[] { Rule("unearth", Trigger.ON_CAST, new UnearthAction(count)) });
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

        private static CardDefinition Card(string id, CardType type = CardType.MINION,
            bool collectible = true, bool held = false, IReadOnlyList<RuleDefinition>? rules = null)
            => new CardDefinition(id, id, type, 1,
                type == CardType.MINION || type == CardType.WEAPON ? 1 : (int?)null,
                type == CardType.MINION ? 1 : (int?)null,
                type == CardType.WEAPON ? 1 : (int?)null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                rules ?? Array.Empty<RuleDefinition>(), "", "art/" + id, SetId.CORE,
                collectible, null, "", held);

        private sealed class PoolDatabase : ICardDatabase
        {
            private readonly Dictionary<string, CardDefinition> _definitions = new(StringComparer.Ordinal);
            private readonly List<CardDefinition> _pool;

            public PoolDatabase(IEnumerable<CardDefinition> pool)
            {
                _pool = new List<CardDefinition>(pool);
                foreach (CardDefinition definition in _pool) Register(definition);
            }

            public void Register(CardDefinition definition) => _definitions[definition.Id] = definition;

            public bool TryGet(string cardId, out CardDefinition def)
                => _definitions.TryGetValue(cardId, out def!);

            public CardDefinition Get(string cardId) => _definitions[cardId];

            public IReadOnlyCollection<CardDefinition> AllCollectible() => _pool;
        }
    }
}
