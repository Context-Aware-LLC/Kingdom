using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardBehavior
{
    /// <summary>
    /// The negate switch on the FROST action (Sam, 2026-10-06): <c>FrostAction(target, negate: true)</c>
    /// clears the Frost effect the positive form applies, so a card can unfreeze a character.
    ///
    /// <para>It lives on FROST and not on GRANT_KEYWORD because a frozen character is not carrying the
    /// <c>FROST</c> keyword — it is carrying an <c>EffectKind.Frost</c> instance left by the Freeze
    /// action or the Frost damage rider. The keyword means "freezes what it damages", and removing it
    /// would strip that carrier behaviour without thawing anybody.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class UnfreezeTests
    {
        private static readonly int Thaw = FixtureCard.Id("t_unfreeze_spell");
        private static readonly int Vanilla = FixtureCard.Id("t_unfreeze_vanilla");

        private static FakeCardDatabase Db()
        {
            var thaw = TestRules.Rule("rule_1", Trigger.ON_CAST,
                new FrostAction(new TargetSpec("ANY_CHARACTER", TargetSelection.Of(1)), negate: true));
            return new FakeCardDatabase()
                .Register(new CardDefinition(Thaw, FixtureCard.Handle(Thaw), FixtureCard.Handle(Thaw), CardType.SPELL,
                    cost: 1, attack: null, health: null, durability: null, CardClass.NEUTRAL, Rarity.COMMON,
                    System.Array.Empty<Keyword>(), new[] { thaw }, "", "art/" + FixtureCard.Handle(Thaw),
                    SetId.CORE, true, null, ""))
                .Register(new CardDefinition(Vanilla, FixtureCard.Handle(Vanilla), FixtureCard.Handle(Vanilla), CardType.MINION,
                    cost: 2, attack: 2, health: 3, durability: null, CardClass.NEUTRAL, Rarity.COMMON,
                    System.Array.Empty<Keyword>(), System.Array.Empty<RuleDefinition>(), "",
                    "art/" + FixtureCard.Handle(Vanilla), SetId.CORE, true, null, ""));
        }

        private static GameState MainPhase(int p0Mana = 10)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var hero0 = new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30);
            var hero1 = new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30);
            var ps0 = new PlayerState(p0, hero0);
            var ps1 = new PlayerState(p1, hero1);
            var s = new GameState(new MatchId("M"), new DeterministicRandom(42UL), ps0, ps1)
            {
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.ActivePlayer = p0;
            ps0.Mana.Max = ps0.Mana.Available = p0Mana;
            ps1.Mana.Max = ps1.Mana.Available = 10;
            return s;
        }

        private static CardInstance Hand(GameState s, int seat, int defId)
        {
            var ci = new CardInstance(s.NextInstanceId(), defId, s.Players[seat].Id) { Zone = Zone.Hand };
            s.Players[seat].Hand.Add(ci);
            return ci;
        }

        private static CardInstance Board(GameState s, int seat, CardDefinition def)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = ps.Board.Count,
            };
            ps.Board.Add(ci);
            return ci;
        }

        private static CommandResult Cast(FakeCardDatabase db, GameState s, CardInstance spell, InstanceId target)
            => new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.Players[0].Id, spell.Id, 0, new[] { new TargetRef(target) }));

        [Test]
        public void Negated_Frost_Clears_A_Frosted_Minion()
        {
            var db = Db();
            var s = MainPhase();
            var minion = Board(s, 0, db.Get(Vanilla));
            TestFrost.Apply(minion, turnsRemaining: 1);
            Assume.That(minion.IsFrosted(), Is.True);

            var r = Cast(db, s, Hand(s, 0, Thaw), minion.Id);

            Assert.That(r.Accepted, Is.True, $"rejected: {r.Reason}");
            Assert.That(minion.IsFrosted(), Is.False, "the negated FROST action thaws its target");
        }

        [Test]
        public void Negated_Frost_Clears_A_Frosted_Hero()
        {
            var db = Db();
            var s = MainPhase();
            var hero = s.Players[0].Hero;
            TestFrost.Apply(hero, turnsRemaining: 1);
            Assume.That(hero.IsFrosted(), Is.True);

            var r = Cast(db, s, Hand(s, 0, Thaw), hero.Id);

            Assert.That(r.Accepted, Is.True, $"rejected: {r.Reason}");
            Assert.That(hero.IsFrosted(), Is.False, "a hero is a character too");
        }

        [Test]
        public void Negated_Frost_On_An_Unfrosted_Target_Changes_Nothing()
        {
            var db = Db();
            var s = MainPhase();
            var minion = Board(s, 0, db.Get(Vanilla));
            Assume.That(minion.IsFrosted(), Is.False);

            var r = Cast(db, s, Hand(s, 0, Thaw), minion.Id);

            Assert.That(r.Accepted, Is.True, $"rejected: {r.Reason}");
            Assert.That(minion.IsFrosted(), Is.False, "unfreezing the unfrozen must never freeze");
            Assert.That(minion.Effects, Is.Empty);
        }

        [Test]
        public void Positive_Frost_Still_Freezes()
        {
            var freezeId = FixtureCard.Id("t_unfreeze_freeze_control");
            var db = Db().Register(new CardDefinition(freezeId, FixtureCard.Handle(freezeId), FixtureCard.Handle(freezeId),
                CardType.SPELL, cost: 1, attack: null, health: null, durability: null, CardClass.NEUTRAL, Rarity.COMMON,
                System.Array.Empty<Keyword>(),
                new[] { TestRules.Rule("rule_1", Trigger.ON_CAST, new FrostAction(new TargetSpec("ANY_CHARACTER", TargetSelection.Of(1)))) },
                "", "art/" + FixtureCard.Handle(freezeId), SetId.CORE, true, null, ""));
            var s = MainPhase();
            var minion = Board(s, 1, db.Get(Vanilla));

            var r = Cast(db, s, Hand(s, 0, freezeId), minion.Id);

            Assert.That(r.Accepted, Is.True, $"rejected: {r.Reason}");
            Assert.That(minion.IsFrosted(), Is.True, "the default is still the positive form");
        }
    }
}
