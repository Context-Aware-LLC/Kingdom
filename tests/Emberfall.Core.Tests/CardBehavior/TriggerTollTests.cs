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
    /// TRIGGER_TOLL — ringing a LIVING minion's rattle. Everything here is about the half of the
    /// verb that leaves no trace on the board: the toll fires, and the minion that owns it is
    /// supposed to be exactly as it was. A rung toll that quietly spent the minion's REKINDLE, or
    /// that formed work from a silenced one, produces a board that looks right today and is wrong
    /// several turns later — which is the class of failure a human playtester cannot attribute.
    ///
    /// <para>Every selection here is <c>random: 1</c> against a board holding exactly ONE eligible
    /// minion, so the roll has a single outcome and none of these assertions depend on the RNG seed.
    /// The randomness is the card's, not the test's.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class TriggerTollTests
    {
        /// <summary>The shape Grave Maul ships: a weapon whose own toll rings a friendly minion's.</summary>
        [Test]
        public void A_Broken_Weapons_Toll_Rings_A_Friendly_Minions_Rattle()
        {
            CardDefinition token = Minion(FixtureCard.Id("wisp"), 1, 1);
            CardDefinition ringer = Minion(FixtureCard.Id("ringer"), 2, 4, Array.Empty<Keyword>(),
                TestRules.Rule("toll", Trigger.ON_DEATH, new SummonAction(token.Id, 1)));
            CardDefinition maul = Weapon(FixtureCard.Id("maul"), 2, 1,
                TestRules.Rule("weapon_toll", Trigger.ON_DEATH, RingARandomFriendlyMinion()));
            var db = new FakeCardDatabase().Register(token).Register(ringer).Register(maul);

            GameState s = State();
            CardInstance minion = OnBoard(s, db, 0, ringer);
            Play(s, db, ToHand(s, 0, maul.Id));

            // One swing spends the weapon's only durability, so the break — and its toll — happen
            // inside this command rather than being set up by hand.
            CommandResult swing = new CommandProcessor(db).Execute(s,
                new AttackIntent(s.ActivePlayer, s.Players[0].Hero.Id, new TargetRef(s.Players[1].Hero.Id)));
            Assert.That(swing.Accepted, Is.True, swing.Reason?.ToString());

            Assert.That(s.Players[0].Weapon, Is.Null, "the weapon did not break on its last durability");
            Assert.That(s.Players[0].Board.Select(c => c.DefinitionId), Contains.Item(token.Id),
                "the friendly minion's toll did not fire when the weapon broke");
            Assert.That(s.Players[0].Board, Contains.Item(minion),
                "ringing a toll killed the minion that owns it");
            Assert.That(minion.CurrentHealth, Is.EqualTo(4), "the rung minion took damage");
        }

        /// <summary>
        /// The silent half. A minion whose toll has been rung did not die, so nothing about it may
        /// be spent: not its health, not its slot, and above all not its one REKINDLE — the state
        /// whose loss surfaces only on the turn it fails to come back.
        /// </summary>
        [Test]
        public void Ringing_A_Toll_Spends_Neither_The_Minion_Nor_Its_Rekindle()
        {
            CardDefinition token = Minion(FixtureCard.Id("wisp"), 1, 1);
            CardDefinition ringer = Minion(FixtureCard.Id("ringer"), 2, 4, new[] { Keyword.REKINDLE },
                TestRules.Rule("toll", Trigger.ON_DEATH, new SummonAction(token.Id, 1)));
            CardDefinition bell = Spell(FixtureCard.Id("bell"), RingARandomFriendlyMinion());
            var db = new FakeCardDatabase().Register(token).Register(ringer).Register(bell);

            GameState s = State();
            CardInstance minion = OnBoard(s, db, 0, ringer);
            int slot = minion.Position;

            Play(s, db, ToHand(s, 0, bell.Id));

            Assert.That(s.Players[0].Board.Select(c => c.DefinitionId), Contains.Item(token.Id),
                "the toll did not fire");
            // The spell itself is in there, having resolved. What must NOT be is the minion.
            Assert.That(s.Players[0].Graveyard, Does.Not.Contain(minion),
                "the minion was buried by a toll nobody paid");
            Assert.That(minion.Zone, Is.EqualTo(Zone.Board));
            Assert.That(minion.Position, Is.EqualTo(slot), "the rung minion moved slot");
            Assert.That(minion.HasUsedRekindle, Is.False,
                "ringing the toll consumed the minion's REKINDLE — it will not come back when it " +
                "really dies, and nothing in the match will say why");
        }

        /// <summary>
        /// Silence answers for the minion whose bell is being rung, not for the card ringing it.
        /// The gate is <c>RaiseSelf</c>'s, which is the same one a real death goes through — so this
        /// pins that TRIGGER_TOLL did not find a second way in.
        /// </summary>
        [Test]
        public void A_Silenced_Minions_Toll_Cannot_Be_Rung()
        {
            CardDefinition token = Minion(FixtureCard.Id("wisp"), 1, 1);
            CardDefinition ringer = Minion(FixtureCard.Id("ringer"), 2, 4, Array.Empty<Keyword>(),
                TestRules.Rule("toll", Trigger.ON_DEATH, new SummonAction(token.Id, 1)));
            CardDefinition bell = Spell(FixtureCard.Id("bell"), RingARandomFriendlyMinion());
            var db = new FakeCardDatabase().Register(token).Register(ringer).Register(bell);

            GameState s = State();
            CardInstance minion = OnBoard(s, db, 0, ringer);
            minion.IsSilenced = true;

            Play(s, db, ToHand(s, 0, bell.Id));

            Assert.That(s.Players[0].Board.Select(c => c.DefinitionId), Does.Not.Contain(token.Id),
                "a silenced minion's toll was rung");
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(1));
        }

        /// <summary>
        /// A minion with nothing to ring is a legal target that does nothing. Deliberate — see
        /// <see cref="TriggerTollAction"/>: narrowing to toll-carriers is a filter on the SELECTOR,
        /// and this verb does not own that axis.
        /// </summary>
        [Test]
        public void Ringing_A_Vanilla_Minions_Toll_Is_A_Legal_No_Op()
        {
            CardDefinition vanilla = Minion(FixtureCard.Id("vanilla"), 2, 4);
            CardDefinition bell = Spell(FixtureCard.Id("bell"), RingARandomFriendlyMinion());
            var db = new FakeCardDatabase().Register(vanilla).Register(bell);

            GameState s = State();
            CardInstance minion = OnBoard(s, db, 0, vanilla);

            Play(s, db, ToHand(s, 0, bell.Id));

            Assert.That(s.Players[0].Board, Is.EqualTo(new[] { minion }));
            Assert.That(s.Outcome, Is.EqualTo(MatchOutcome.InProgress));
        }

        /// <summary>
        /// Two minions each ringing a random friendly toll point at one another forever. Nothing
        /// prevents that by design; <c>MatchEngine.ActionResolutionCap</c> is what has to stop it,
        /// the same backstop every other toll cascade leans on. What this pins is that the cascade
        /// TERMINATES and the command still returns — a hang here is a server that stops answering.
        /// </summary>
        [Test]
        public void Two_Minions_Ringing_Each_Other_Terminate_On_The_Resolution_Cap()
        {
            CardDefinition ringer = Minion(FixtureCard.Id("ringer"), 2, 4, Array.Empty<Keyword>(),
                TestRules.Rule("toll", Trigger.ON_DEATH, RingARandomFriendlyMinion()));
            CardDefinition bell = Spell(FixtureCard.Id("bell"), RingARandomFriendlyMinion());
            var db = new FakeCardDatabase().Register(ringer).Register(bell);

            GameState s = State();
            OnBoard(s, db, 0, ringer);
            OnBoard(s, db, 0, ringer);

            Play(s, db, ToHand(s, 0, bell.Id));

            Assert.That(s.ActionResolutionsThisTurn, Is.GreaterThan(MatchEngine.ActionResolutionCap),
                "the mutual ring did not actually recurse, so this test is not measuring the cap");
            Assert.That(s.Players[0].Board, Has.Count.EqualTo(2), "the cascade damaged the board");
        }

        // ── fixtures ─────────────────────────────────────────────────────────────

        /// <summary>The one authored shape under test, spelled once: "a random friendly minion's Toll".</summary>
        private static TriggerTollAction RingARandomFriendlyMinion()
            => new TriggerTollAction(
                new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.RandomOf(1)));

        private static CardDefinition Minion(int id, int attack, int health,
            Keyword[]? keywords = null, params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION,
                0, attack, health, null, CardClass.NEUTRAL, Rarity.COMMON,
                keywords ?? Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id),
                SetId.CORE, true, null, "");

        private static CardDefinition Weapon(int id, int attack, int durability,
            params RuleDefinition[] rules)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.WEAPON,
                0, attack, null, durability, CardClass.NEUTRAL, Rarity.COMMON,
                Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE,
                true, null, "");

        private static CardDefinition Spell(int id, RuleAction action)
            => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL,
                0, null, null, null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                new[] { TestRules.Rule("cast", Trigger.ON_CAST, action) }, "",
                "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("trigger-toll"), new DeterministicRandom(23UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.GRAVEKEEP, 30)),
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
            var card = new CardInstance(s.NextInstanceId(), definitionId, s.Players[seat].Id)
            {
                Zone = Zone.Hand,
            };
            s.Players[seat].Hand.Add(card);
            return card;
        }

        private static CardInstance OnBoard(GameState s, ICardDatabase db, int seat, CardDefinition def)
        {
            var card = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id);
            Assert.That(RulesEngine.EnterBoard(s, card, def, seat, -1, db), Is.True);
            return card;
        }

        private static CommandResult Play(GameState s, ICardDatabase db, CardInstance card,
            params TargetRef[] targets)
        {
            CommandResult result = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.ActivePlayer, card.Id, 0, targets));
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return result;
        }
    }
}
