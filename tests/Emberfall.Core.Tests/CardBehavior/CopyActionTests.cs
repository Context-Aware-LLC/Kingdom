using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardBehavior
{
    /// <summary>
    /// COPY summons a fresh minion under the caster's control carrying the TARGET's current
    /// (buffed) stats and keywords, not the printed form (Sam, 2026-09-05) -- see
    /// <see cref="ActionHandlers.CopyHandler"/> and <see cref="CopyAction"/>.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CopyActionTests
    {
        private static CardDefinition Minion(int id, int attack, int health, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 0, attack, health,
                null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 0, null, null,
                null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("copy"), new DeterministicRandom(23UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)))
            {
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
                NextInstanceCounter = 3,
            };
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = 10;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;
            return s;
        }

        private static CardInstance ToHand(GameState s, int seat, int definitionId)
        {
            var card = new CardInstance(s.NextInstanceId(), definitionId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(card);
            return card;
        }

        // Places a minion already carrying a buff -- a real Enchantment, not just raised numbers --
        // and already damaged, so the test can tell "copied the buff" apart from "copied the wound".
        private static CardInstance BuffedOnBoard(GameState s, ICardDatabase db, int seat, CardDefinition def,
            int attackDelta, int healthDelta, int damageTaken, params Keyword[] extraKeywords)
        {
            var card = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id);
            Assert.That(RulesEngine.EnterBoard(s, card, def, seat, -1, db), Is.True);
            card.Enchantments.Add(new Enchantment { Source = card.Id, AttackDelta = attackDelta, HealthDelta = healthDelta });
            foreach (var k in extraKeywords) card.Keywords.Add(k);
            DerivedStats.Recompute(card, def);
            card.CurrentHealth -= damageTaken;
            card.SummonedThisTurn = false; // so it can attack in tests that need to tell it apart from a fresh copy
            return card;
        }

        private static CommandResult Play(GameState s, ICardDatabase db, CardInstance card,
            params TargetRef[] targets)
        {
            var result = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.ActivePlayer, card.Id, 0, targets));
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return result;
        }

        [Test]
        public void Copy_Arrives_With_The_Targets_Current_Buffed_Stats_And_Keywords_But_Full_Health()
        {
            var original = Minion(FixtureCard.Id("original"), 2, 4);
            var mirrorImage = Spell(FixtureCard.Id("mirror_image"), TestRules.Rule("copy_it", Trigger.ON_CAST,
                new CopyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(original).Register(mirrorImage);
            var s = State();
            // base 2/4, +3/+2 buff -> current 5/6, then took 2 damage -> current health 4/6.
            var target = BuffedOnBoard(s, db, 0, original, attackDelta: 3, healthDelta: 2, damageTaken: 2, Keyword.AEGIS);
            target.HasAegisShield = true;

            var result = Play(s, db, ToHand(s, 0, mirrorImage.Id), new TargetRef(target.Id));

            Assert.That(s.Players[0].Board, Has.Count.EqualTo(2), "the original and the fresh copy");
            var copy = s.Players[0].Board.Single(c => c.Id != target.Id);
            Assert.That(copy.DefinitionId, Is.EqualTo(original.Id));
            Assert.That((copy.CurrentAttack, copy.MaxHealth), Is.EqualTo((5, 6)), "buffed stats carried over");
            Assert.That(copy.CurrentHealth, Is.EqualTo(6), "arrives whole -- the original's wound does not carry");
            Assert.That(copy.Keywords, Does.Contain(Keyword.AEGIS));
            Assert.That(copy.HasAegisShield, Is.True);
            Assert.That(copy.SummonedThisTurn, Is.True);
            Assert.That(result.Events.Any(e => e.Kind == GameEventKind.CardSummoned && e.Source == copy.Id), Is.True);
        }

        [Test]
        public void Copy_Stats_Survive_A_Later_Aura_Recompute()
        {
            // Regression guard for EF-017a: stats are def + Enchantments, recomputed on every board
            // change. If the buffed snapshot were baked into raw numbers instead of its own
            // Enchantment entry, the next AuraEngine.Recompute sweep (any board mutation triggers
            // one) would silently fold it back down to the printed base.
            var original = Minion(FixtureCard.Id("original"), 2, 4);
            var filler = Minion(FixtureCard.Id("filler"), 1, 1);
            var mirrorImage = Spell(FixtureCard.Id("mirror_image"), TestRules.Rule("copy_it", Trigger.ON_CAST,
                new CopyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(original).Register(filler).Register(mirrorImage);
            var s = State();
            var target = BuffedOnBoard(s, db, 0, original, attackDelta: 3, healthDelta: 2, damageTaken: 0);

            Play(s, db, ToHand(s, 0, mirrorImage.Id), new TargetRef(target.Id));
            var copy = s.Players[0].Board.Single(c => c.Id != target.Id);

            // Any later board entry re-runs the full aura fold across every minion in play.
            Assert.That(RulesEngine.EnterBoard(s, new CardInstance(s.NextInstanceId(), filler.Id, s.Players[0].Id),
                filler, 0, -1, db), Is.True);

            Assert.That((copy.CurrentAttack, copy.MaxHealth), Is.EqualTo((5, 6)),
                "the copy's buff must not evaporate on the next unrelated board change");
        }

        [Test]
        public void Copy_Is_Summoned_Under_The_Casters_Control_Even_When_Copying_An_Enemy_Minion()
        {
            var enemyMinion = Minion(FixtureCard.Id("enemy_minion"), 3, 3);
            var mirrorImage = Spell(FixtureCard.Id("mirror_image"), TestRules.Rule("copy_it", Trigger.ON_CAST,
                new CopyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(enemyMinion).Register(mirrorImage);
            var s = State();
            var target = BuffedOnBoard(s, db, 1, enemyMinion, attackDelta: 0, healthDelta: 0, damageTaken: 0);

            Play(s, db, ToHand(s, 0, mirrorImage.Id), new TargetRef(target.Id));

            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "the enemy's own minion is untouched");
            var copy = s.Players[0].Board.Single();
            Assert.That(copy.Controller, Is.EqualTo(s.Players[0].Id));
        }

        [Test]
        public void A_Full_Board_Stops_The_Copy_Rather_Than_Rejecting_The_Cast()
        {
            var filler = Minion(FixtureCard.Id("filler"), 1, 1);
            var original = Minion(FixtureCard.Id("original"), 2, 4);
            var mirrorImage = Spell(FixtureCard.Id("mirror_image"), TestRules.Rule("copy_it", Trigger.ON_CAST,
                new CopyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(filler).Register(original).Register(mirrorImage);
            var s = State();
            var target = BuffedOnBoard(s, db, 0, original, attackDelta: 0, healthDelta: 0, damageTaken: 0);
            for (int i = 0; i < 6; i++) // target already occupies one of the 7 board slots
            {
                var f = new CardInstance(s.NextInstanceId(), filler.Id, s.Players[0].Id);
                Assert.That(RulesEngine.EnterBoard(s, f, filler, 0, -1, db), Is.True);
            }

            Play(s, db, ToHand(s, 0, mirrorImage.Id), new TargetRef(target.Id));

            Assert.That(s.Players[0].Board, Has.Count.EqualTo(7), "board stayed at cap -- no copy fit");
        }
    }
}
