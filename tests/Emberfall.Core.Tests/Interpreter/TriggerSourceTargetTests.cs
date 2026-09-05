using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Interpreter
{
    /// <summary>
    /// What <c>TRIGGER_SOURCE</c> actually resolves to at runtime — the half a rendering test
    /// cannot answer.
    ///
    /// <para><b>Why this exists.</b> 1015 Grove Mender is the FIRST shipped card to use the
    /// selector: it validated, it rendered, and until 2026-08-27 nothing in the corpus or the
    /// suite had ever asked the engine to resolve one. This repo's own rule is that a trigger
    /// which parses is not a trigger that fires, and the same goes for a selector — "it compiles
    /// and prints a sentence" is not evidence that the buff lands on the minion the player just
    /// played.</para>
    ///
    /// <para>The two assertions that matter are the pair: the buff reaches the minion that caused
    /// the trigger, and NOT the observer that carries the rule. Those are one instance apart and a
    /// resolver that confused them would still look right in every log and every card face.</para>
    ///
    /// <para>The lower half of the file is the SIDE the trigger sweeps, which was both of them
    /// until #272 (Sam, 2026-09-04). Those tests were pins recording a defect and are now
    /// assertions guarding the ruling: same fixtures, inverted expectations.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class TriggerSourceTargetTests
    {
        [Test]
        public void A_trigger_source_buff_lands_on_the_minion_that_caused_the_trigger()
        {
            (GameState state, FakeCardDatabase db) = Board();

            CardInstance observer = Play(state, db, FixtureCard.Id("ts_observer"));
            CardInstance summoned = Play(state, db, FixtureCard.Id("ts_body"));

            Assert.That(Stats(summoned), Is.EqualTo("3/3"),
                "the minion whose arrival raised ON_FRIENDLY_SUMMON is the trigger source, so the "
                + "+1/+1 belongs to it — a 2/2 body arriving at 3/3");
            Assert.That(Stats(observer), Is.EqualTo("2/2"),
                "and NOT to the observer carrying the rule. SELF and TRIGGER_SOURCE are one "
                + "instance apart and a resolver confusing them looks identical on the card face");
        }

        /// <summary>
        /// The observer does not fire on its own arrival. That is <c>RaiseObservers</c>' documented
        /// exclusion rather than anything this selector does, and it is asserted here because it is
        /// what makes "give it +1/+1" a true sentence: a card that buffed itself on the way in
        /// would be printing one effect and doing two.
        /// </summary>
        [Test]
        public void The_observer_does_not_fire_on_its_own_arrival()
        {
            (GameState state, FakeCardDatabase db) = Board();

            CardInstance observer = Play(state, db, FixtureCard.Id("ts_observer"));

            Assert.That(Stats(observer), Is.EqualTo("2/2"));
        }

        [Test]
        public void Every_later_summon_is_buffed_while_the_observer_lives()
        {
            (GameState state, FakeCardDatabase db) = Board();

            Play(state, db, FixtureCard.Id("ts_observer"));
            CardInstance first = Play(state, db, FixtureCard.Id("ts_body"));
            CardInstance second = Play(state, db, FixtureCard.Id("ts_body"));

            Assert.That(Stats(first), Is.EqualTo("3/3"), "SOURCE_BOUND is for as long as it lives");
            Assert.That(Stats(second), Is.EqualTo("3/3"), "not a one-shot on the first arrival");
        }

        /// <summary>
        /// <b>ON_FRIENDLY_SUMMON IS CONFINED TO THE SUMMONER'S SIDE.</b> The word "Friendly" was
        /// enforced by nothing until #272 (Sam, 2026-09-04); this test is the failure mode, not the
        /// feature.
        ///
        /// <para><c>OrderedTriggerSources.Snapshot</c> appended BOTH sides unconditionally — it is
        /// the engine's single observer sweep, shared with <c>ON_TURN_END</c> (EF-012) and
        /// <c>ON_SPELL_CAST</c> (EF-016), which ARE both-sided by decision. This trigger inherited
        /// the same walk and was never given a controller filter; <c>excluded</c> only removes the
        /// summoned card itself, so the name was the only thing claiming a side.</para>
        ///
        /// <para><b>The consequence this assertion guards is the expensive one.</b> On an enemy
        /// summon the trigger source IS the enemy's minion, so a <c>TRIGGER_SOURCE</c> effect
        /// buffed the OPPONENT. That is what took 1015 Grove Mender's draft form out of the corpus
        /// and cost Thornwake 7.8 points across a 1000-match ladder run — and it is invisible to a
        /// rendering test, because the card face was right and only the landing was wrong.</para>
        /// </summary>
        [Test]
        public void An_enemy_summon_does_not_raise_it_so_a_trigger_source_effect_cannot_buff_the_enemy()
        {
            (GameState state, FakeCardDatabase db) = Board();
            Play(state, db, FixtureCard.Id("ts_observer"));

            var enemy = new CardInstance(state.NextInstanceId(), FixtureCard.Id("ts_body"),
                state.Players[1].Id)
            { Zone = Zone.Hand };
            state.Players[1].Hand.Add(enemy);
            state.ActivePlayer = state.Players[1].Id;

            var result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[1].Id, enemy.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(result.Accepted, Is.True);
            Assert.That(Stats(enemy), Is.EqualTo("2/2"),
                "the sweep no longer visits the other board, so our observer never sees the "
                + "opponent's arrival and the enemy 2/2 stays a 2/2");
        }

        /// <summary>
        /// The shipped-card half: 1159 Cogsworth, The Grand Rebuilder reads "Whenever you summon a
        /// minion, give all friendly minions +1/+1" and now means it. Its target is
        /// <c>ANY_FRIENDLY_MINION</c>, so the old bug buffed OUR board off the opponent's tempo — a
        /// bonus rather than a hazard, and still not what the card said. Asserted with the shipped
        /// shape rather than the pronoun one so the claim about the legendary is evidence.
        /// </summary>
        [Test]
        public void The_shipped_Cogsworth_shape_ignores_the_opponents_summon()
        {
            var db = new FakeCardDatabase()
                .Register(Minion(FixtureCard.Id("ts_cogsworth"), TestRules.Rule("wide",
                    Trigger.ON_FRIENDLY_SUMMON,
                    new ModifyStatsAction(1, 1,
                        new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(7))))))
                .Register(Minion(FixtureCard.Id("ts_body")));
            (GameState state, _) = Board();

            Play(state, db, FixtureCard.Id("ts_cogsworth"));
            CardInstance mine = Play(state, db, FixtureCard.Id("ts_body"));
            string afterOwnSummon = Stats(mine);
            Assert.That(afterOwnSummon, Is.EqualTo("3/3"), "our own summon still fires it");

            var enemy = new CardInstance(state.NextInstanceId(), FixtureCard.Id("ts_body"),
                state.Players[1].Id)
            { Zone = Zone.Hand };
            state.Players[1].Hand.Add(enemy);
            state.ActivePlayer = state.Players[1].Id;
            new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[1].Id, enemy.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(Stats(mine), Is.EqualTo(afterOwnSummon),
                "our board did NOT grow again on the OPPONENT's summon — 1159 Cogsworth reads "
                + "\"Whenever you summon a minion\" and since #272 it means it");
        }

        /// <summary>
        /// <b>The scope is measured from the SUMMONED CARD, never from the active player.</b> A
        /// summon can happen on the opponent's turn — a toll, an ongoing effect — and an
        /// implementation that read <c>_state.ActivePlayer</c> would pass every other test in this
        /// file and quietly sweep the wrong board here. Both raise sites therefore name the
        /// controller explicitly, and <c>RaiseObservers</c> throws for a controller-scoped trigger
        /// that arrives without one.
        /// </summary>
        [Test]
        public void An_off_turn_summon_is_measured_from_the_summoner_not_the_active_player()
        {
            (GameState state, FakeCardDatabase db) = Board();
            Play(state, db, FixtureCard.Id("ts_observer"));

            // Hand the turn to the opponent, then summon for P0 anyway — the shape a toll firing
            // during the enemy's turn produces.
            state.ActivePlayer = state.Players[1].Id;

            var token = new CardInstance(state.NextInstanceId(), FixtureCard.Id("ts_body"),
                state.Players[0].Id);
            Assert.That(RulesEngine.EnterBoard(state, token, db.Get(token.DefinitionId),
                    seat: 0, atIndex: -1, db), Is.True);

            var interpreter = new EffectInterpreter(state, db, new EventBuffer(state));
            interpreter.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: token,
                triggerSource: token.Id, observerController: token.Controller);
            interpreter.Drain();

            Assert.That(Stats(token), Is.EqualTo("3/3"),
                "P0's observer saw P0's off-turn summon: the sweep starts at the summoner's seat, "
                + "which is not the active player's");
        }

        /// <summary>
        /// The forcing function for a future raise site. A controller-scoped trigger with no
        /// controller is a mistake the compiler cannot catch and a default would hide — silently
        /// sweeping the active player's board, which is right often enough to ship.
        /// </summary>
        [Test]
        public void A_controller_scoped_raise_without_a_controller_throws()
        {
            (GameState state, FakeCardDatabase db) = Board();
            var interpreter = new EffectInterpreter(state, db, new EventBuffer(state));

            Assert.That(() => interpreter.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: null),
                Throws.InstanceOf<InvalidOperationException>());
        }

        // ── Fixture ──────────────────────────────────────────────────────────

        /// <summary>1015 Grove Mender's rule, reduced to the shape under test.</summary>
        private static CardDefinition Observer()
            => Minion(FixtureCard.Id("ts_observer"), TestRules.Rule("grow", Trigger.ON_FRIENDLY_SUMMON,
                new ModifyStatsAction(1, 1, new TargetSpec("TRIGGER_SOURCE", TargetSelection.Of(1)))));

        private static CardDefinition Minion(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION,
                0, 2, 2, null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static (GameState, FakeCardDatabase) Board()
        {
            var db = new FakeCardDatabase()
                .Register(Observer())
                .Register(Minion(FixtureCard.Id("ts_body")));

            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var state = new GameState(new MatchId("trigger-source"), new DeterministicRandom(7UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.VERDANT, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)))
            {
                ActivePlayer = p0,
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            foreach (PlayerState p in state.Players)
                p.Mana.Max = p.Mana.Available = 10;
            return (state, db);
        }

        private static CardInstance Play(GameState state, FakeCardDatabase db, int definitionId)
        {
            var card = new CardInstance(state.NextInstanceId(), definitionId, state.Players[0].Id)
            {
                Zone = Zone.Hand,
            };
            state.Players[0].Hand.Add(card);

            var result = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, card.Id, state.Players[0].Board.Count,
                    Array.Empty<TargetRef>()));
            Assert.That(result.Accepted, Is.True, "fixture play must be legal");
            return card;
        }

        private static string Stats(CardInstance card)
            => $"{card.CurrentAttack}/{card.CurrentHealth}";
    }
}
