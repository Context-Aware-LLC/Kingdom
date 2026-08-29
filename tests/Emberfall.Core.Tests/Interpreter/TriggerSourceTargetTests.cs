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
        /// <b>ON_FRIENDLY_SUMMON FIRES ON BOTH PLAYERS' SUMMONS. The word "Friendly" in the name
        /// is enforced by nothing.</b> Measured 2026-08-27, and this test is the record of it.
        ///
        /// <para><c>OrderedTriggerSources.Snapshot</c> appends BOTH sides unconditionally — it is
        /// the engine's single observer sweep, shared with <c>ON_TURN_END</c> (EF-012) and
        /// <c>ON_SPELL_CAST</c> (EF-016), both of which are documented as both-sides. This trigger
        /// inherited the same sweep and was never given a controller filter; <c>excluded</c> only
        /// removes the summoned card itself. So the name is the only thing claiming a side.</para>
        ///
        /// <para><b>Two live consequences, both a ruling for Sam rather than something to patch
        /// underneath a shipped card.</b> 1159 Cogsworth, The Grand Rebuilder reads "Whenever you
        /// summon a minion, give all friendly minions +1/+1" and also fires on the OPPONENT's
        /// summons — see the sibling test. And a <c>TRIGGER_SOURCE</c> effect is worse than merely
        /// generous: on an enemy summon the trigger source IS the enemy's minion, so the card
        /// buffs the opponent. That is what took 1015 Grove Mender's draft form out of the corpus
        /// and cost Thornwake 7.8 points across a 1000-match ladder run.</para>
        ///
        /// <para>Pinned rather than asserted-against, because the fix is a decision: give the
        /// trigger a controller filter (which changes a shipped legendary), or rename it and admit
        /// the scope in the card grammar. <c>card-schema.md</c> currently claims the narrow
        /// reading, and the code wins.</para>
        /// </summary>
        [Test]
        public void An_enemy_summon_ALSO_raises_it_and_a_trigger_source_effect_buffs_the_enemy()
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
            Assert.That(Stats(enemy), Is.EqualTo("3/3"),
                "NOT what the name says: our observer buffed the OPPONENT's minion, because the "
                + "sweep visits both boards and the trigger source is whoever just arrived");
        }

        /// <summary>
        /// The shipped-card half of the finding above, so the claim about 1159 Cogsworth is
        /// evidence rather than inference. Its target is <c>ANY_FRIENDLY_MINION</c>, so an enemy
        /// summon does not buff the enemy — it buffs OUR board, off the opponent's tempo. Strictly
        /// a bonus rather than a hazard, and still not what the card says.
        /// </summary>
        [Test]
        public void The_shipped_Cogsworth_shape_fires_off_the_opponents_summon_too()
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

            var enemy = new CardInstance(state.NextInstanceId(), FixtureCard.Id("ts_body"),
                state.Players[1].Id)
            { Zone = Zone.Hand };
            state.Players[1].Hand.Add(enemy);
            state.ActivePlayer = state.Players[1].Id;
            new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[1].Id, enemy.Id, 0, Array.Empty<TargetRef>()));

            Assert.That(Stats(mine), Is.Not.EqualTo(afterOwnSummon),
                "our board grew again on the OPPONENT's summon — 1159 Cogsworth reads "
                + "\"Whenever you summon a minion\" and does not mean it");
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
