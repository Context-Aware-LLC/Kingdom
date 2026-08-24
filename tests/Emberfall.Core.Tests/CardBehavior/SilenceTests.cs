using System;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Projection;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardBehavior
{
    /// <summary>
    /// NOR-31: silence is a live-permanent state, not merely an enchantment reset. Its subscription
    /// gate is intentionally applied when work is enqueued; a rule already queued before silence keeps
    /// its rule snapshot and resolves, but no later observer/self subscription is formed.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class SilenceTests
    {
        private static CardDefinition Minion(int id, int attack, int health, Keyword[] keywords,
            params RuleDefinition[] rules) => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.MINION, 0, attack, health, null,
                CardClass.NEUTRAL, Rarity.COMMON, keywords, rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Weapon(int id, int attack, int durability, Keyword[] keywords,
            params RuleDefinition[] rules) => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.WEAPON, 0, attack, null, durability,
                CardClass.NEUTRAL, Rarity.COMMON, keywords, rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static CardDefinition Spell(int id, params RuleDefinition[] rules) => new CardDefinition(id, FixtureCard.Handle(id), FixtureCard.Handle(id), CardType.SPELL, 0, null, null, null,
            CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "", "art/" + FixtureCard.Handle(id), SetId.CORE, true, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("silence"), new DeterministicRandom(23UL),
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

        private static CardInstance OnBoard(GameState s, ICardDatabase db, int seat, CardDefinition def)
        {
            var card = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id);
            Assert.That(RulesEngine.EnterBoard(s, card, def, seat, -1, db), Is.True);
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
        public void Silenced_Minion_Loses_All_Abilities_But_Already_Queued_Trigger_Still_Resolves()
        {
            var witness = Minion(FixtureCard.Id("witness"), 2, 5, new[] { Keyword.AEGIS, Keyword.LIFEDRAIN },
                TestRules.Rule("spell_witness", Trigger.ON_SPELL_CAST,
                    TestRules.Damage(Trigger.ON_SPELL_CAST, 2, TargetSelection.Of(1))),
                TestRules.Rule("death_burst", Trigger.ON_DEATH,
                    TestRules.Damage(Trigger.ON_DEATH, 4, TargetSelection.Of(1))));
            var silence = Spell(FixtureCard.Id("silence"), TestRules.Rule("silence", Trigger.ON_CAST,
                new SilenceAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))));
            var blank = Spell(FixtureCard.Id("blank"));
            var destroy = Spell(FixtureCard.Id("destroy"), TestRules.Rule("destroy", Trigger.ON_CAST,
                new DestroyAction(new TargetSpec("ANY_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(witness).Register(silence).Register(blank).Register(destroy);
            var s = State();
            var minion = OnBoard(s, db, 1, witness);
            minion.Enchantments.Add(new Enchantment { Source = new InstanceId(99), AttackDelta = 3, HealthDelta = 4 });
            minion.Enchantments.Add(new Enchantment { Source = new InstanceId(100), GrantedKeyword = Keyword.BULWARK });
            minion.Keywords.Add(Keyword.BULWARK);
            DerivedStats.Recompute(minion, witness);
            minion.CurrentHealth = 6; // nine-health body has taken three damage.
            TestFrost.Apply(minion);

            int heroBefore = s.Players[0].Hero.CurrentHealth;
            var silenceResult = Play(s, db, ToHand(s, 0, silence.Id), new TargetRef(minion.Id));

            Assert.That(minion.IsSilenced, Is.True);
            Assert.That(minion.Enchantments, Is.Empty);
            Assert.That(minion.Keywords, Is.Empty, "printed riders and trigger markers are both ineffective");
            Assert.That((minion.CurrentAttack, minion.MaxHealth, minion.CurrentHealth), Is.EqualTo((2, 5, 2)));
            Assert.That((minion.HasAegisShield, minion.IsFrosted()), Is.EqualTo((false, false)));
            Assert.That(silenceResult.Events.Any(e => e.Kind == GameEventKind.CardSilenced && e.Target == minion.Id), Is.True);
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(heroBefore - 2),
                "the ON_SPELL_CAST rule snapshot existed before the silence action resolved");

            _ = Play(s, db, ToHand(s, 0, blank.Id));
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(heroBefore - 2),
                "a silenced live minion forms no future observer subscription");

            _ = Play(s, db, ToHand(s, 0, destroy.Id), new TargetRef(minion.Id));
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(heroBefore - 2),
                "silence also suppresses the printed ON_DEATH trigger marker");
        }

        [Test]
        public void Silenced_Weapon_Keeps_Spent_Durability_But_Loses_Riders_And_Self_Triggers()
        {
            var blade = Weapon(FixtureCard.Id("blade"), 2, 3, new[] { Keyword.LIFEDRAIN, Keyword.DOUBLESTRIKE },
                TestRules.Rule("attack_ping", Trigger.ON_ATTACK,
                    TestRules.Damage(Trigger.ON_ATTACK, 1, TargetSelection.Of(1))));
            var silence = Spell(FixtureCard.Id("silence"), TestRules.Rule("silence", Trigger.ON_CAST,
                new SilenceAction(new TargetSpec("FRIENDLY_WEAPON", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(blade).Register(silence);
            var s = State();
            var equipped = ToHand(s, 0, blade.Id);
            _ = Play(s, db, equipped);
            s.Players[0].Hero.CurrentHealth = 20;
            s.Players[0].Weapon!.CurrentDurability = 1; // already spent: silence must not refund it.

            _ = Play(s, db, ToHand(s, 0, silence.Id));
            var weapon = s.Players[0].Weapon!;
            Assert.That(weapon.IsSilenced, Is.True);
            Assert.That((weapon.CurrentAttack, weapon.CurrentDurability), Is.EqualTo((2, 1)));
            Assert.That(weapon.Keywords, Is.Empty);
            Assert.That(weapon.Enchantments, Is.Empty);

            int targetBefore = s.Players[1].Hero.CurrentHealth;
            var swing = new CommandProcessor(db).Execute(s,
                new AttackIntent(s.ActivePlayer, s.Players[0].Hero.Id, new TargetRef(s.Players[1].Hero.Id)));
            Assert.That(swing.Accepted, Is.True, swing.Reason?.ToString());
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(targetBefore - 2),
                "the weapon's printed ON_ATTACK rule did not become a new source");
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(20),
                "the stripped lifedrain rider did not heal the attacking hero");
            Assert.That(s.Players[0].Weapon, Is.Null, "the preserved final durability was spent normally");
        }

        [Test]
        public void Bounce_Transform_And_Entry_Start_A_New_Unsilenced_Zone_Life()
        {
            var body = Minion(FixtureCard.Id("body"), 2, 3, new[] { Keyword.BULWARK });
            var replacement = Minion(FixtureCard.Id("replacement"), 4, 4, Array.Empty<Keyword>());
            var blade = Weapon(FixtureCard.Id("blade"), 2, 3, new[] { Keyword.LIFEDRAIN });
            var bounce = Spell(FixtureCard.Id("bounce"), TestRules.Rule("bounce", Trigger.ON_CAST,
                new ReturnToHandAction(new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1)))));
            var bounceWeapon = Spell(FixtureCard.Id("bounce_weapon"), TestRules.Rule("bounce_weapon", Trigger.ON_CAST,
                new ReturnToHandAction(new TargetSpec("FRIENDLY_WEAPON", TargetSelection.Of(1)))));
            var transform = Spell(FixtureCard.Id("transform"), TestRules.Rule("transform", Trigger.ON_CAST,
                new TransformAction(replacement.Id, new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1)))));
            var db = new FakeCardDatabase().Register(body).Register(replacement).Register(blade)
                .Register(bounce).Register(bounceWeapon).Register(transform);
            var s = State();
            var minion = OnBoard(s, db, 0, body);
            DerivedStats.ResetToSilenced(minion, body, preserveDamage: true);

            _ = Play(s, db, ToHand(s, 0, bounce.Id), new TargetRef(minion.Id));
            var bounced = s.Players[0].Hand.Single(c => c.DefinitionId == body.Id);
            Assert.That(bounced.IsSilenced, Is.False);
            Assert.That(bounced.Keywords, Is.EquivalentTo(new[] { Keyword.BULWARK }));

            _ = Play(s, db, bounced);
            Assert.That(s.Players[0].Board.Single().IsSilenced, Is.False);

            var live = s.Players[0].Board.Single();
            DerivedStats.ResetToSilenced(live, body, preserveDamage: true);
            _ = Play(s, db, ToHand(s, 0, transform.Id), new TargetRef(live.Id));
            Assert.That(s.Players[0].Board.Single().DefinitionId, Is.EqualTo(replacement.Id));
            Assert.That(s.Players[0].Board.Single().IsSilenced, Is.False, "transform creates a new instance");

            var weapon = ToHand(s, 0, blade.Id);
            _ = Play(s, db, weapon);
            weapon.CurrentDurability = 1;
            DerivedStats.ResetToSilenced(weapon, blade, preserveDamage: false);
            _ = Play(s, db, ToHand(s, 0, bounceWeapon.Id));
            var bouncedWeapon = s.Players[0].Hand.Single(c => c.DefinitionId == blade.Id);
            Assert.That((bouncedWeapon.IsSilenced, bouncedWeapon.CurrentDurability), Is.EqualTo((false, 1)),
                "bounce starts a new ability life without restoring spent weapon durability");
            Assert.That(bouncedWeapon.Keywords, Is.EquivalentTo(new[] { Keyword.LIFEDRAIN }));
        }

        [Test]
        public void Silence_State_Is_Hash_And_Projection_Complete()
        {
            var state = CanonicalStateFixtures.BuildSeedState();
            var card = CanonicalStateFixtures.HandCard(state);
            string before = GameStateHasher.Hash(state);
            card.IsSilenced = true;

            Assert.That(GameStateHasher.Hash(state), Is.Not.EqualTo(before));
            Assert.That(GameStateProjector.Project(state, state.Players[0].Id).Players[0].Hand[0].IsSilenced, Is.True);
        }
    }
}
