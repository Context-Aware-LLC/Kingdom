using System;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.CardBehavior
{
    /// <summary>
    /// Verdant's Empower (Sam, 2026-11-04): "Give a friendly minion +1/+1, or give your hero +1 Attack
    /// this turn and 1 Armor." One choice aimed at either half, played off the shipped row so the
    /// card data and the engine are checked together.
    ///
    /// <para>What these pin is what the row cannot show: one MODIFY_STATS on ANY_FRIENDLY is a lasting
    /// +1/+1 on a minion and, on a hero, Attack this turn plus Armor. That Attack lets an unarmed hero
    /// swing and adds to a weapon, and it is gone when the turn ends while the Armor stays.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class EmpowerTests
    {
        private const int EmpowerId = 4;

        private static readonly CardDefinition Body =
            new CardDefinition(FixtureCard.Id("empower_body"), "empower_body", "empower_body", CardType.MINION,
                0, 2, 2, null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                Array.Empty<RuleDefinition>(), "", "art/empower_body", SetId.CORE, true, null, "");

        private static readonly CardDefinition Club =
            new CardDefinition(FixtureCard.Id("empower_club"), "empower_club", "empower_club", CardType.WEAPON,
                0, 2, null, 2, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                Array.Empty<RuleDefinition>(), "", "art/empower_club", SetId.CORE, true, null, "");

        private static FakeCardDatabase Db()
            => new FakeCardDatabase().Register(ShippedCards.Db.Get(EmpowerId)).Register(Body).Register(Club);

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("empower"), new DeterministicRandom(29UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.VERDANT, 30)
                {
                    HeroPowerId = EmpowerId,
                }),
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

        private static CardInstance OnBoard(GameState s, ICardDatabase db, int seat)
        {
            var card = new CardInstance(s.NextInstanceId(), Body.Id, s.Players[seat].Id);
            Assert.That(RulesEngine.EnterBoard(s, card, Body, seat, -1, db), Is.True);
            return card;
        }

        private static CommandResult Run(GameState s, ICardDatabase db, PlayerIntent intent)
            => new CommandProcessor(db).Execute(s, intent);

        private static void Press(GameState s, ICardDatabase db, InstanceId target)
        {
            CommandResult r = Run(s, db, new HeroPowerIntent(s.ActivePlayer, new[] { new TargetRef(target) }));
            Assert.That(r.Accepted, Is.True, r.Reason?.ToString() ?? "no reason");
        }

        private static CommandResult Swing(GameState s, ICardDatabase db)
            => Run(s, db, new AttackIntent(s.ActivePlayer, s.Players[0].Hero.Id, new TargetRef(s.Players[1].Hero.Id)));

        [Test]
        public void On_a_friendly_minion_it_is_a_lasting_plus_one_plus_one_and_nothing_for_the_hero()
        {
            var db = Db();
            GameState s = State();
            CardInstance body = OnBoard(s, db, 0);

            Press(s, db, body.Id);

            Assert.That(body.CurrentAttack, Is.EqualTo(3));
            Assert.That(body.CurrentHealth, Is.EqualTo(3));
            Assert.That(s.Players[0].HeroAttackThisTurn, Is.EqualTo(0));
            Assert.That(s.Players[0].Hero.Armor, Is.EqualTo(0));

            Assert.That(Run(s, db, new EndTurnIntent(s.ActivePlayer)).Accepted, Is.True);
            Assert.That(body.CurrentAttack, Is.EqualTo(3), "the minion's +1/+1 is not a this-turn buff");
        }

        [Test]
        public void On_your_hero_it_is_attack_this_turn_and_armor_and_an_unarmed_hero_can_swing()
        {
            var db = Db();
            GameState s = State();

            Press(s, db, s.Players[0].Hero.Id);
            Assert.That(s.Players[0].HeroAttackThisTurn, Is.EqualTo(1));
            Assert.That(s.Players[0].Hero.Armor, Is.EqualTo(1));
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(30), "the +1 Health is Armor, not Health");

            CommandResult swing = Swing(s, db);
            Assert.That(swing.Accepted, Is.True, swing.Reason?.ToString());
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(29));

            Assert.That(Run(s, db, new EndTurnIntent(s.ActivePlayer)).Accepted, Is.True);
            Assert.That(s.Players[0].HeroAttackThisTurn, Is.EqualTo(0), "the Attack lasts this turn only");
            Assert.That(s.Players[0].Hero.Armor, Is.EqualTo(1), "the Armor stays");
        }

        [Test]
        public void The_attack_adds_to_an_equipped_weapon()
        {
            var db = Db();
            GameState s = State();
            s.Players[0].Weapon = new CardInstance(s.NextInstanceId(), Club.Id, s.Players[0].Id)
            {
                Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2,
            };

            Press(s, db, s.Players[0].Hero.Id);
            CommandResult swing = Swing(s, db);

            Assert.That(swing.Accepted, Is.True, swing.Reason?.ToString());
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27));
            Assert.That(s.Players[0].Weapon!.CurrentDurability, Is.EqualTo(1));
        }

        [Test]
        public void An_unarmed_hero_with_no_attack_gained_still_cannot_swing()
        {
            var db = Db();
            GameState s = State();
            CommandResult swing = Swing(s, db);
            Assert.That(swing.Accepted, Is.False);
            Assert.That(swing.Reason, Is.EqualTo(RejectReason.WeaponMissing));
        }

        [Test]
        public void It_cannot_be_aimed_at_an_enemy()
        {
            var db = Db();
            GameState s = State();
            CardInstance theirs = OnBoard(s, db, 1);

            Assert.That(Run(s, db, new HeroPowerIntent(s.ActivePlayer, new[] { new TargetRef(theirs.Id) })).Accepted,
                Is.False);
            Assert.That(Run(s, db, new HeroPowerIntent(s.ActivePlayer,
                new[] { new TargetRef(s.Players[1].Hero.Id) })).Accepted, Is.False);
        }
    }
}
