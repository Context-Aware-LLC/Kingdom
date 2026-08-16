using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Interpreter
{
    /// <summary>
    /// A player-chosen target with no legal candidate FIZZLES on a card with a body and REFUSES the
    /// play on one without (Sam, 2026-08-07). Before this ruling every such card was simply
    /// unplayable — 15 rows in the shipped corpus, including every Reinforce herald, which could
    /// therefore never be your first minion.
    ///
    /// <para><c>target.required</c> opts a COST back out of the fizzle: "sacrifice a friendly minion"
    /// is unplayable with nothing to sacrifice, body or no body.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class OptionalTargetTests
    {
        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("optional-target"), new DeterministicRandom(42),
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
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = 10;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;
            return s;
        }

        private static CardDefinition Card(string id, CardType type, params RuleDefinition[] rules)
            => new CardDefinition(id, id, type, 1, type == CardType.SPELL ? (int?)null : 1,
                type == CardType.MINION ? (int?)3 : null,
                type == CardType.WEAPON ? (int?)2 : null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "", id,
                SetId.CORE, true, null, "");

        private static CardInstance Hand(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            { Zone = Zone.Hand };
            state.Players[seat].Hand.Add(card);
            return card;
        }

        private static CardInstance Board(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = state.Players[seat].Board.Count,
            };
            state.Players[seat].Board.Add(card);
            return card;
        }

        private static CommandResult Play(GameState state, ICardDatabase db, CardInstance card,
            params TargetRef[] targets)
            => new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, card.Id, 0, targets));

        // ── The body survives ────────────────────────────────────────────────────

        [Test]
        public void Minion_Herald_With_No_Candidates_Is_Played_And_The_Herald_Fizzles()
        {
            var herald = Card("herald", CardType.MINION, TestRules.Rule("r1", Trigger.ON_PLAY,
                new ReinforceAction(new TargetSpec("ANY_FRIENDLY_MINION"))));
            var db = new FakeCardDatabase().Register(herald);
            var state = State();
            CardInstance inHand = Hand(state, 0, herald);

            CommandResult result = Play(state, db, inHand);

            Assert.That(result.Accepted, Is.True, "a herald with nothing to aim at keeps its body");
            Assert.That(state.Players[0].Board, Has.Count.EqualTo(1));
            Assert.That(state.Players[0].Board[0].CurrentHealth, Is.EqualTo(3),
                "the reinforce found no target and did nothing");
        }

        [Test]
        public void Weapon_Herald_With_No_Candidates_Still_Equips()
        {
            var blade = Card("blade", CardType.WEAPON, TestRules.Rule("r1", Trigger.ON_PLAY,
                new GrantKeywordAction(Keyword.SHROUD, new TargetSpec("ANY_FRIENDLY_MINION"))));
            var db = new FakeCardDatabase().Register(blade);
            var state = State();
            CardInstance inHand = Hand(state, 0, blade);

            CommandResult result = Play(state, db, inHand);

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[0].Weapon, Is.Not.Null);
        }

        [Test]
        public void A_Herald_With_A_Legal_Target_Still_Owes_One()
        {
            var body = Card("body", CardType.MINION);
            var herald = Card("herald", CardType.MINION, TestRules.Rule("r1", Trigger.ON_PLAY,
                new ReinforceAction(new TargetSpec("ANY_FRIENDLY_MINION"))));
            var db = new FakeCardDatabase().Register(body).Register(herald);
            var state = State();
            CardInstance ally = Board(state, 0, body);
            CardInstance inHand = Hand(state, 0, herald);

            Assert.That(Play(state, db, inHand).Accepted, Is.False,
                "with a legal target available the choice is not optional");

            state = State();
            ally = Board(state, 0, body);
            inHand = Hand(state, 0, herald);
            Assert.That(Play(state, db, inHand, new TargetRef(ally.Id)).Accepted, Is.True);
            Assert.That(ally.CurrentHealth, Is.EqualTo(4), "reinforce is a flat +1/+1");
        }

        [Test]
        public void A_Herald_Cannot_Smuggle_A_Target_The_Plan_Did_Not_Offer()
        {
            var herald = Card("herald", CardType.MINION, TestRules.Rule("r1", Trigger.ON_PLAY,
                new ReinforceAction(new TargetSpec("ANY_FRIENDLY_MINION"))));
            var db = new FakeCardDatabase().Register(herald);
            var state = State();
            CardInstance inHand = Hand(state, 0, herald);

            CommandResult result = Play(state, db, inHand,
                new TargetRef(state.Players[1].Hero.Id));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.EqualTo(RejectReason.IllegalTarget));
        }

        // ── No body: the play is still refused ───────────────────────────────────

        [Test]
        public void Spell_With_No_Candidates_Is_Still_Refused()
        {
            var bolt = Card("bolt", CardType.SPELL, TestRules.Rule("r1", Trigger.ON_CAST,
                new DamageAction(3, new TargetSpec("ANY_MINION"))));
            var db = new FakeCardDatabase().Register(bolt);
            var state = State();
            CardInstance inHand = Hand(state, 0, bolt);

            CommandResult result = Play(state, db, inHand);

            Assert.That(result.Accepted, Is.False, "a spell that fizzles is pure card-burn");
            Assert.That(result.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(state.Players[0].Hand, Has.Count.EqualTo(1));
        }

        // ── A cost is not an effect ──────────────────────────────────────────────

        [Test]
        public void Required_Target_Keeps_A_Minion_Unplayable_With_Nothing_To_Sacrifice()
        {
            var priest = Card("priest", CardType.MINION, TestRules.Rule("r1", Trigger.ON_PLAY,
                new DestroyAction(new TargetSpec("ANY_FRIENDLY_MINION", required: true))));
            var db = new FakeCardDatabase().Register(priest);
            var state = State();
            CardInstance inHand = Hand(state, 0, priest);

            CommandResult result = Play(state, db, inHand);

            Assert.That(result.Accepted, Is.False, "an unpayable cost is unplayable, body or not");
            Assert.That(result.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(state.Players[0].Board, Is.Empty);
        }

        [Test]
        public void Required_Target_Plays_Normally_Once_There_Is_Something_To_Sacrifice()
        {
            var body = Card("body", CardType.MINION);
            var priest = Card("priest", CardType.MINION, TestRules.Rule("r1", Trigger.ON_PLAY,
                new DestroyAction(new TargetSpec("ANY_FRIENDLY_MINION", required: true))));
            var db = new FakeCardDatabase().Register(body).Register(priest);
            var state = State();
            CardInstance fodder = Board(state, 0, body);
            CardInstance inHand = Hand(state, 0, priest);

            CommandResult result = Play(state, db, inHand, new TargetRef(fodder.Id));

            Assert.That(result.Accepted, Is.True);
            Assert.That(fodder.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(state.Players[0].Board, Has.Count.EqualTo(1), "only the priest is left");
        }

        // ── Partial fills, and the multi-binding shape ───────────────────────────

        [Test]
        public void A_Herald_Takes_As_Many_Targets_As_Exist_Below_Its_Authored_Count()
        {
            var body = Card("body", CardType.MINION);
            var herald = Card("herald", CardType.MINION, TestRules.Rule("r1", Trigger.ON_PLAY,
                new ReinforceAction(new TargetSpec("ANY_FRIENDLY_MINION", count: 2))));
            var db = new FakeCardDatabase().Register(body).Register(herald);
            var state = State();
            CardInstance only = Board(state, 0, body);
            CardInstance inHand = Hand(state, 0, herald);

            Assert.That(Play(state, db, inHand, new TargetRef(only.Id)).Accepted, Is.True,
                "one candidate for a count-2 spec asks for one, not two");
        }

        [Test]
        public void One_Binding_Can_Fizzle_While_Another_Is_Still_Owed()
        {
            // The shipped shape of tide_tidal_emissary: an enemy target (the hero always
            // exists) plus a friendly-minion target that may have nowhere to go.
            var emissary = Card("emissary", CardType.MINION,
                TestRules.Rule("r1", Trigger.ON_PLAY,
                    new FrostAction(new TargetSpec("ANY_ENEMY", binding: "frost_target"))),
                TestRules.Rule("r2", Trigger.ON_PLAY,
                    new ReinforceAction(new TargetSpec("ANY_FRIENDLY_MINION",
                        binding: "reinforce_target"))));
            var db = new FakeCardDatabase().Register(emissary);
            var state = State();
            CardInstance inHand = Hand(state, 0, emissary);

            IReadOnlyList<TargetChoiceRequirement> plan = Targeting.PlayerChoicePlan(
                state, db, state.Players[0].Id, emissary, Trigger.ON_PLAY, inHand.Id);
            Assert.That(plan, Has.Count.EqualTo(2));
            Assert.That(plan[0].RequiredCount, Is.EqualTo(1), "the enemy hero is always aimable");
            Assert.That(plan[1].RequiredCount, Is.EqualTo(0), "no friendly minion to reinforce");

            CommandResult result = Play(state, db, inHand,
                new TargetRef(state.Players[1].Hero.Id, "frost_target"));

            Assert.That(result.Accepted, Is.True);
            Assert.That(state.Players[1].Hero.IsFrosted, Is.True);
            Assert.That(state.Players[0].Board, Has.Count.EqualTo(1));
        }

        // ── The hero power keeps its old behavior ────────────────────────────────

        [Test]
        public void Hero_Power_With_No_Candidates_Is_Still_Refused()
        {
            var power = Card("power", CardType.HERO_POWER, TestRules.Rule("r1", Trigger.ON_CAST,
                new DamageAction(1, new TargetSpec("ANY_MINION"))));
            var db = new FakeCardDatabase().Register(power);
            var state = State();
            state.Players[0].Hero.HeroPowerId = "power";

            CommandResult result = new CommandProcessor(db).Execute(state,
                new HeroPowerIntent(state.Players[0].Id, Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.False,
                "a hero power is reusable next turn; spending it on nothing is never right");
            Assert.That(state.Players[0].HeroPowerUsedThisTurn, Is.False);
        }
    }
}
