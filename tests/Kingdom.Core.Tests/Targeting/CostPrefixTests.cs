using System.Collections.Generic;
using System.Linq;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

using Engine = Kingdom.Core.Targeting;

namespace Kingdom.Core.Tests.Targeting
{
    /// <summary>
    /// A cost is a clause prefix scoping the run of rules beneath it, and the run does not happen
    /// when the price goes unpaid.
    ///
    /// <para><b>Why a fixture and not the corpus.</b> Every shipped cost is REQUIRED, and a required
    /// cost is paid whenever the card was playable at all — so the gate never closes on any authored
    /// row and the whole optional half of the feature has no card exercising it. A green CardCi run
    /// says nothing about it. These are the assertions that do.</para>
    ///
    /// <para><b>The ruling, 2026-08-22 (Sam).</b> Cost-ness is AUTHORED, never read out of the prose:
    /// <i>"Destroy a friendly minion and draw a card."</i> is three different cards — plays and draws
    /// anyway, unplayable, plays and does not draw — and the sentence contains nothing that picks
    /// one. The flag picks. Two further rulings are pinned below: the verb is keyed on the target's
    /// SIDE because <i>"sacrifice is a choice, and thus can only apply to your own minions"</i>, and
    /// an optional cost is illegal on a spell because <i>"the spell should be unplayable if the cost
    /// is not able to be paid."</i></para>
    /// </summary>
    [TestFixture]
    public sealed class CostPrefixTests
    {
        // ── What the card face says ──────────────────────────────────────────

        [Test]
        public void A_required_cost_prints_as_a_clause_and_a_colon()
            => Assert.That(Face(Sacrifice(CostKind.Required), Draw(1)),
                Is.EqualTo("Sacrifice a friendly minion: Draw a card."));

        [Test]
        public void An_optional_cost_is_the_same_clause_behind_You_may()
            => Assert.That(Face(Sacrifice(CostKind.Optional), Draw(1)),
                Is.EqualTo("You may sacrifice a friendly minion: Draw a card."));

        /// <summary>
        /// The run beneath one cost is ONE sentence, exactly as a Toll's is — the prefix carries
        /// across the conjunction rather than being reprinted per effect.
        /// </summary>
        [Test]
        public void One_cost_prefixes_the_whole_run_it_buys()
            => Assert.That(Face(Sacrifice(CostKind.Required), Draw(1), Draw(2)),
                Is.EqualTo("Sacrifice a friendly minion: Draw a card and draw 2 cards."));

        /// <summary>
        /// Bounce has no cost-flavoured synonym, which is the case that killed reading cost-ness off
        /// the verb: two shipped rows pay their price by returning a minion, and "return" is the only
        /// word for it. The colon is doing all the work here.
        /// </summary>
        [Test]
        public void A_cost_paid_by_bouncing_needs_no_special_verb()
            => Assert.That(Face(Bounce(CostKind.Optional), Draw(1)),
                Is.EqualTo("You may return a friendly minion to your hand: Draw a card."));

        // ── Which word a destroy prints ──────────────────────────────────────

        [TestCase("ANY_FRIENDLY_MINION", "Sacrifice a friendly minion.")]
        [TestCase("ANY_MINION", "Destroy a minion.")]
        [TestCase("ANY_ENEMY_MINION", "Destroy an enemy minion.")]
        public void The_destroy_verb_follows_the_targets_side(string selector, string expected)
            => Assert.That(
                CardTextGenerator.Generate(One(new DestroyAction(
                    new TargetSpec(selector, TargetSelection.Of(1)))), CardTextGenerator.NoPool.Instance),
                Is.EqualTo(expected),
                "volition is what 'sacrifice' asserts, so it belongs to the side axis — an enemy " +
                "minion is only ever destroyed. Note the word does NOT track cost-ness: this " +
                "destroy is not a cost at all.");

        // ── Reading it back ──────────────────────────────────────────────────

        /// <summary>
        /// The parser has to recover the cost KIND, not merely notice there was one: required and
        /// optional differ by two words and produce different cards.
        /// </summary>
        [TestCase(CostKind.Required)]
        [TestCase(CostKind.Optional)]
        public void The_cost_prefix_round_trips_through_the_parser(CostKind kind)
        {
            CardDefinition card = Card(CardType.MINION, Sacrifice(kind), Draw(1));

            Assert.That(CardTextParser.TryParse(card, out IReadOnlyList<RuleDefinition> rules,
                out string reason), Is.True, $"refused: {reason}");
            Assert.That(CardTextGenerator.Generate(rules, CardTextGenerator.NoPool.Instance), Is.EqualTo(card.Text));

            TargetSpec? cost = RuleSemantics.TargetOf(rules[0].Action);
            Assert.That(cost?.Cost, Is.EqualTo(kind), "the cost kind is what the round trip is for");
            Assert.That(rules[1].Condition, Is.InstanceOf<CostPaidCondition>(),
                "the run beneath a cost is gated on it, or the price buys nothing");
        }

        // ── What the engine does ─────────────────────────────────────────────

        /// <summary>
        /// The whole reason a flag alone was insufficient: an unpaid price must suppress the SIBLING
        /// rule it was paying for. Nothing on the cost's own target can reach that rule.
        /// </summary>
        [Test]
        public void An_unpaid_cost_withholds_the_run_it_buys()
        {
            (GameState state, FakeCardDatabase db, CardInstance carrier) =
                Played(CardType.MINION, Sacrifice(CostKind.Optional), Draw(1));
            int handBefore = state.Players[0].Hand.Count;

            Resolve(state, db, carrier, NoPicks);

            Assert.That(state.Players[0].Hand.Count, Is.EqualTo(handBefore),
                "the price went unpaid, so the draw it was buying must not happen");
        }

        [Test]
        public void A_paid_cost_lets_the_run_through()
        {
            (GameState state, FakeCardDatabase db, CardInstance carrier) =
                Played(CardType.MINION, Sacrifice(CostKind.Optional), Draw(1));
            int handBefore = state.Players[0].Hand.Count;
            int boardBefore = state.Players[0].Board.Count;

            Resolve(state, db, carrier, Pick(state, carrier));

            Assert.That(state.Players[0].Board.Count, Is.EqualTo(boardBefore - 1), "the price is paid");
            Assert.That(state.Players[0].Hand.Count, Is.EqualTo(handBefore + 1), "so the draw lands");
        }

        /// <summary>
        /// A gate on a card charging no price is shut forever. C28 rejects that shape, so this is
        /// the runtime half of the same statement — fail closed rather than resolve on a cost that
        /// was never asked for.
        /// </summary>
        [Test]
        public void A_gate_with_no_cost_before_it_never_opens()
        {
            (GameState state, FakeCardDatabase db, CardInstance carrier) = Played(Orphan());
            int handBefore = state.Players[0].Hand.Count;

            Resolve(state, db, carrier, NoPicks);

            Assert.That(state.Players[0].Hand.Count, Is.EqualTo(handBefore));
        }

        // ── What the player is allowed to send ───────────────────────────────

        /// <summary>
        /// Declining is a legal play for an optional cost and a malformed command for every other
        /// pick. <c>MinimumCount</c> is the only thing that separates them, and it is the piece a
        /// client needs before the affordance can exist at all.
        /// </summary>
        [TestCase(CostKind.Optional, true)]
        [TestCase(CostKind.Required, false)]
        public void Only_an_optional_cost_may_be_declined(CostKind kind, bool accepted)
        {
            (GameState state, FakeCardDatabase db, CardInstance carrier) =
                Played(CardType.MINION, Sacrifice(kind), Draw(1));

            Assert.That(Engine.ValidatePlayerChoices(state, db, state.Players[0].Id,
                db.Get(carrier.DefinitionId), Trigger.ON_PLAY, NoPicks, carrier.Id),
                Is.EqualTo(accepted));

            IReadOnlyList<TargetChoiceRequirement> plan = Engine.PlayerChoicePlan(
                state, db, state.Players[0].Id, db.Get(carrier.DefinitionId), Trigger.ON_PLAY,
                carrier.Id);
            Assert.That(plan.Single().MinimumCount, Is.EqualTo(accepted ? 0 : 1));
        }

        // ── What may be authored ─────────────────────────────────────────────

        [Test]
        public void An_optional_cost_is_not_expressible_on_a_spell()
            => Assert.That(Rejections(Card(CardType.SPELL, Sacrifice(CostKind.Optional), Draw(1))),
                Does.Contain("C28-COST-OPTIONAL-SPELL"),
                "a spell leaves no body behind, so playing one whose price went unpaid is a mana " +
                "and a card for nothing (Sam, 2026-08-07, held 2026-08-22)");

        [Test]
        public void A_gate_naming_a_price_the_card_never_charges_is_rejected()
            => Assert.That(Rejections(Card(Orphan())), Does.Contain("C28-COST-PAID-ORPHAN"));

        [Test]
        public void A_cost_that_buys_nothing_is_rejected()
            => Assert.That(Rejections(Card(CardType.MINION, Sacrifice(CostKind.Required))),
                Does.Contain("C28-COST-UNBOUGHT"),
                "an unbought cost renders no prefix at all, so the player would pay a price the " +
                "card face never printed — the one shape where text LOSES an effect");

        [Test]
        public void A_second_cost_under_one_trigger_is_rejected()
            => Assert.That(Rejections(Card(CardType.MINION,
                    Sacrifice(CostKind.Required), Bounce(CostKind.Required), Draw(1))),
                Does.Contain("C28-COST-SECOND"),
                "adjacency is what ties a gate to its cost, and two costs make that ambiguous");

        [Test]
        public void A_cost_on_an_observer_trigger_is_rejected()
        {
            var cost = new DestroyAction(new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1), cost: CostKind.Required));
            var card = TestCards.Authored(new CardDefinition(
                id: FixtureCard.Id("t_toll_cost"), handle: "t_toll_cost", name: "Toll Cost", type: CardType.MINION, cost: 3,
                attack: 2, health: 2, @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                durability: null,
                keywords: System.Array.Empty<Keyword>(),
                rules: new[]
                {
                    new RuleDefinition(new RuleId("rule_1"), new TriggeredActivation(Trigger.ON_DEATH),
                        AlwaysCondition.Instance, SourceBoundLifetime.Instance, cost),
                    new RuleDefinition(new RuleId("rule_2"), new TriggeredActivation(Trigger.ON_DEATH),
                        CostPaidCondition.Instance, SourceBoundLifetime.Instance, new DrawAction(1)),
                },
                flavorText: "", artId: "t_toll_cost", setId: SetId.CORE, collectible: false,
                tribe: null, factionId: ""));

            Assert.That(Rejections(card), Does.Contain("C28-COST-TRIGGER"),
                "a price is paid when the card is PLAYED; a toll has no play to refuse");
        }

        // ── Fixture plumbing ─────────────────────────────────────────────────

        private static readonly TargetRef[] NoPicks = System.Array.Empty<TargetRef>();

        private static DestroyAction Sacrifice(CostKind kind)
            => new DestroyAction(new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1),
                cost: kind));

        private static ReturnToHandAction Bounce(CostKind kind)
            => new ReturnToHandAction(new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1),
                cost: kind));

        private static DrawAction Draw(int count) => new DrawAction(count);

        /// <summary>
        /// The rules a run of actions reads as: the first is a cost when its target says so, and
        /// everything after it waits on that cost. Exactly what the authoring tool emits — the
        /// author ticks one box and writes no rule.
        /// </summary>
        private static IReadOnlyList<RuleDefinition> Rules(CardType type, params RuleAction[] actions)
        {
            Trigger trigger = type == CardType.SPELL || type == CardType.HERO_POWER
                ? Trigger.ON_CAST
                : Trigger.ON_PLAY;
            bool charged = false;
            var rules = new List<RuleDefinition>(actions.Length);
            foreach (RuleAction action in actions)
            {
                bool isCost = (RuleSemantics.TargetOf(action)?.Cost ?? CostKind.None) != CostKind.None;
                rules.Add(new RuleDefinition(
                    new RuleId("rule_" + (rules.Count + 1)),
                    new TriggeredActivation(trigger),
                    charged && !isCost ? (RuleCondition)CostPaidCondition.Instance : AlwaysCondition.Instance,
                    OneShotLifetime.Instance,
                    action));
                charged |= isCost;
            }
            return rules;
        }

        private static IReadOnlyList<RuleDefinition> One(RuleAction action)
            => Rules(CardType.MINION, action);

        /// <summary>
        /// A gate waiting on a price the card never charges — the shape <see cref="Rules"/> cannot
        /// build, because it only gates what follows a cost. Hand-assembled precisely so the two
        /// halves of the rejection can be checked: C28 refuses to let it be authored, and the
        /// runtime refuses to open it if one ever reaches the engine anyway.
        /// </summary>
        private static IReadOnlyList<RuleDefinition> Orphan()
            => new[]
            {
                new RuleDefinition(new RuleId("rule_1"), new TriggeredActivation(Trigger.ON_PLAY),
                    CostPaidCondition.Instance, OneShotLifetime.Instance, new DrawAction(1)),
            };

        private static string Face(params RuleAction[] actions)
            => CardTextGenerator.Generate(Rules(CardType.MINION, actions), CardTextGenerator.NoPool.Instance);

        private static CardDefinition Card(CardType type, params RuleAction[] actions)
            => Card(Rules(type, actions), type);

        private static CardDefinition Card(IReadOnlyList<RuleDefinition> rules,
            CardType type = CardType.MINION)
            => TestCards.Authored(new CardDefinition(
                id: FixtureCard.Id("t_cost_card"), handle: "t_cost_card", name: "Cost Card", type: type, cost: 3,
                attack: type == CardType.MINION ? 2 : (int?)null,
                health: type == CardType.MINION ? 2 : (int?)null,
                durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(),
                rules: rules,
                flavorText: "", artId: "t_cost_card", setId: SetId.CORE, collectible: false,
                tribe: null, factionId: ""));

        private static IReadOnlyList<string> Rejections(CardDefinition card)
        {
            var db = new FakeCardDatabase();
            db.Register(card);
            return new CardValidator().Validate(card, db).Issues.Select(i => i.Code).ToList();
        }

        /// <summary>A board holding the card in play, one other friendly minion to pay with, and a
        /// deck to draw from — so paying and drawing are both observable.</summary>
        private static (GameState, FakeCardDatabase, CardInstance) Played(
            CardType type, params RuleAction[] actions)
            => Played(Rules(type, actions), type);

        private static (GameState, FakeCardDatabase, CardInstance) Played(
            IReadOnlyList<RuleDefinition> rules, CardType type = CardType.MINION)
        {
            CardDefinition card = Card(rules, type);
            var db = new FakeCardDatabase();
            db.Register(card);

            var p0 = new PlayerId("p0");
            var p1 = new PlayerId("p1");
            var state = new GameState(new MatchId("m-cost-prefix"), new DeterministicRandom(0xC057UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(0), p0, CardClass.NEUTRAL, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(1), p1, CardClass.NEUTRAL, 30)))
            {
                Phase = TurnPhase.Main,
                NextInstanceCounter = 2,
            };

            PlayerState player = state.Players[0];
            var fodder = new CardInstance(state.NextInstanceId(), FixtureCard.Id("t_fodder"), p0)
            { Zone = Zone.Board, Position = 0, CurrentAttack = 1, CurrentHealth = 1, MaxHealth = 1 };
            player.Board.Add(fodder);

            var carrier = new CardInstance(state.NextInstanceId(), card.Id, p0)
            { Zone = Zone.Board, Position = 1, CurrentAttack = 2, CurrentHealth = 2, MaxHealth = 2 };
            player.Board.Add(carrier);

            for (int i = 0; i < 3; i++)
                player.Deck.Add(new CardInstance(state.NextInstanceId(), FixtureCard.Id("t_deck"), p0) { Zone = Zone.Deck });

            return (state, db, carrier);
        }

        /// <summary>The one friendly minion that is not the carrier — the only thing on this board a
        /// sacrifice can spend.</summary>
        private static IReadOnlyList<TargetRef> Pick(GameState state, CardInstance carrier)
            => state.Players[0].Board
                .Where(m => m.Id != carrier.Id)
                .Select(m => new TargetRef(m.Id, ""))
                .ToList();

        private static void Resolve(GameState state, ICardDatabase db, CardInstance carrier,
            IReadOnlyList<TargetRef> picks)
        {
            var events = new EventBuffer(state);
            var interpreter = new EffectInterpreter(state, db, events);
            interpreter.RaiseSelf(carrier, Trigger.ON_PLAY, picks);
            interpreter.Drain();
        }
    }
}
