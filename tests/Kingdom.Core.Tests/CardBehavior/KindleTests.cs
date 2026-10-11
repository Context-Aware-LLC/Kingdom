using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.CardBehavior
{
    /// <summary>
    /// The Kindle shape (Sam, 2026-10-06): "Deal X damage to a character", where every targetable
    /// character is legal and a rider pays off only when the target was your own. Plus the hero
    /// power that reads whether your hero has been damaged this turn.
    ///
    /// <para>What these pin is the part no rule row shows: a rider does not narrow the choice, it is
    /// gated on the target that was chosen rather than on its own selector, and a damage amount
    /// that depends on the hero's state is decided before the hit that could change that state.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class KindleTests
    {
        private static readonly int HeroPowerId = FixtureCard.Id("hp_kindle_bolt");

        private static CardDefinition Minion(string handle, int attack, int health,
            params RuleDefinition[] rules)
            => new CardDefinition(FixtureCard.Id(handle), handle, handle, CardType.MINION, 0, attack,
                health, null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + handle, SetId.CORE, true, null, "");

        private static CardDefinition Spell(string handle, params RuleDefinition[] rules)
            => new CardDefinition(FixtureCard.Id(handle), handle, handle, CardType.SPELL, 0, null,
                null, null, CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(), rules, "",
                "art/" + handle, SetId.CORE, true, null, "");

        private static TargetSpec Character() => new TargetSpec("ANY_CHARACTER", TargetSelection.Of(1));

        private static RuleDefinition MinionRider(int attack, int health)
            => TestRules.Rule("rider", Trigger.ON_CAST,
                new TargetIsFriendlyCondition(FriendlyTargetKind.MINION),
                new ModifyStatsAction(attack, health,
                    new TargetSpec("ANY_FRIENDLY_MINION", TargetSelection.Of(1))));

        /// <summary>"Deal N damage to a character; a friendly minion gains +A/+H." Rider first.</summary>
        private static CardDefinition Kindle(int damage, int attack, int health)
            => Spell("kindle_" + damage + "_" + attack + "_" + health,
                MinionRider(attack, health),
                TestRules.Rule("burn", Trigger.ON_CAST, new DamageAction(damage, Character())));

        /// <summary>"Deal 1 damage to a character; if it was your hero, draw a card."</summary>
        private static CardDefinition Brazier()
            => Minion("brazier", 2, 3,
                TestRules.Rule("burn", Trigger.ON_PLAY, new DamageAction(1, Character())),
                TestRules.Rule("stoke", Trigger.ON_PLAY,
                    new TargetIsFriendlyCondition(FriendlyTargetKind.HERO), new DrawAction(1)));

        /// <summary>"Deal 1 damage. Deals 2 instead if your hero has taken damage this turn."</summary>
        private static CardDefinition Bolt()
            => new CardDefinition(HeroPowerId, "hp_kindle_bolt", "hp_kindle_bolt", CardType.HERO_POWER,
                2, null, null, null, CardClass.EMBERWARD, Rarity.COMMON, Array.Empty<Keyword>(),
                new[]
                {
                    TestRules.Rule("bolt", Trigger.ON_CAST,
                        new DamageAction(1, Character(), 2, HeroDamagedThisTurnCondition.Instance)),
                },
                "", "art/hp_kindle_bolt", SetId.CORE, false, null, "");

        private static GameState State()
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("kindle"), new DeterministicRandom(23UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)
                {
                    HeroPowerId = HeroPowerId,
                }),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)
                {
                    HeroPowerId = HeroPowerId,
                }))
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

        private static CardInstance ToHand(GameState s, int seat, CardDefinition def)
        {
            var card = new CardInstance(s.NextInstanceId(), def.Id, s.Players[seat].Id) { Zone = Zone.Hand };
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
            InstanceId target)
        {
            var result = new CommandProcessor(db).Execute(s,
                new PlayCardIntent(s.ActivePlayer, card.Id, 0, new[] { new TargetRef(target) }));
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return result;
        }

        private static CommandResult Press(GameState s, ICardDatabase db, InstanceId target)
        {
            var result = new CommandProcessor(db).Execute(s,
                new HeroPowerIntent(s.ActivePlayer, new[] { new TargetRef(target) }));
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return result;
        }

        // ── the choice ────────────────────────────────────────────────────────────

        [Test]
        public void A_Rider_Does_Not_Narrow_What_The_Player_May_Target()
        {
            CardDefinition kindle = Kindle(2, 2, 1);
            CardDefinition body = Minion("body", 3, 3);
            var db = new FakeCardDatabase().Register(kindle).Register(body);
            GameState s = State();
            CardInstance mine = OnBoard(s, db, 0, body);
            CardInstance theirs = OnBoard(s, db, 1, body);

            IReadOnlyList<TargetChoiceRequirement> plan = Kingdom.Core.Targeting.PlayerChoicePlan(
                s, db, s.ActivePlayer, kindle, Trigger.ON_CAST);

            Assert.That(plan, Has.Count.EqualTo(1), "one choice: the rider makes none of its own");
            Assert.That(plan[0].RequiredCount, Is.EqualTo(1));
            Assert.That(plan[0].Candidates.Select(c => c.Instance), Is.EquivalentTo(new[]
            {
                s.Players[0].Hero.Id, mine.Id, s.Players[1].Hero.Id, theirs.Id,
            }));
        }

        // ── the minion payoff ─────────────────────────────────────────────────────

        [Test]
        public void Kindling_Your_Own_Minion_Buffs_It_And_Then_Burns_It()
        {
            CardDefinition kindle = Kindle(2, 2, 1);
            CardDefinition body = Minion("body", 3, 3);
            var db = new FakeCardDatabase().Register(kindle).Register(body);
            GameState s = State();
            CardInstance mine = OnBoard(s, db, 0, body);

            Play(s, db, ToHand(s, 0, kindle), mine.Id);

            Assert.That((mine.CurrentAttack, mine.CurrentHealth, mine.MaxHealth), Is.EqualTo((5, 2, 4)));
        }

        [Test]
        public void The_Buff_Lands_Before_The_Damage_So_A_One_Health_Body_Can_Survive()
        {
            CardDefinition kindle = Kindle(2, 2, 2);
            CardDefinition body = Minion("frail", 1, 1);
            var db = new FakeCardDatabase().Register(kindle).Register(body);
            GameState s = State();
            CardInstance mine = OnBoard(s, db, 0, body);

            Play(s, db, ToHand(s, 0, kindle), mine.Id);

            Assert.That(s.Players[0].Board, Does.Contain(mine), "damage first would have killed it");
            Assert.That((mine.CurrentAttack, mine.CurrentHealth), Is.EqualTo((3, 1)));
        }

        [Test]
        public void An_Enemy_Minion_Takes_The_Damage_And_No_Buff()
        {
            CardDefinition kindle = Kindle(2, 2, 1);
            CardDefinition body = Minion("body", 3, 3);
            var db = new FakeCardDatabase().Register(kindle).Register(body);
            GameState s = State();
            CardInstance theirs = OnBoard(s, db, 1, body);

            Play(s, db, ToHand(s, 0, kindle), theirs.Id);

            Assert.That((theirs.CurrentAttack, theirs.CurrentHealth, theirs.MaxHealth), Is.EqualTo((3, 1, 3)));
        }

        [Test]
        public void Either_Hero_Takes_The_Damage_And_Nothing_Is_Buffed()
        {
            CardDefinition kindle = Kindle(2, 2, 1);
            CardDefinition body = Minion("body", 3, 3);
            var db = new FakeCardDatabase().Register(kindle).Register(body);
            GameState s = State();
            CardInstance mine = OnBoard(s, db, 0, body);

            Play(s, db, ToHand(s, 0, kindle), s.Players[1].Hero.Id);
            Play(s, db, ToHand(s, 0, kindle), s.Players[0].Hero.Id);

            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(28));
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(28));
            Assert.That((mine.CurrentAttack, mine.CurrentHealth), Is.EqualTo((3, 3)));
        }

        // ── the hero payoff ───────────────────────────────────────────────────────

        [Test]
        public void The_Hero_Rider_Fires_Only_When_The_Target_Was_Your_Own_Hero()
        {
            CardDefinition brazier = Brazier();
            CardDefinition filler = Minion("filler", 1, 1);
            var db = new FakeCardDatabase().Register(brazier).Register(filler);
            GameState s = State();
            for (int i = 0; i < 4; i++)
                s.Players[0].Deck.Add(new CardInstance(s.NextInstanceId(), filler.Id, s.Players[0].Id)
                { Zone = Zone.Deck });

            Play(s, db, ToHand(s, 0, brazier), s.Players[1].Hero.Id);
            Assert.That(s.Players[0].Hand, Is.Empty, "the enemy hero is not yours: no card");
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(29));

            Play(s, db, ToHand(s, 0, brazier), s.Players[0].Hero.Id);
            Assert.That(s.Players[0].Hand, Has.Count.EqualTo(1));
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(29));
        }

        // ── the hero power ────────────────────────────────────────────────────────

        [Test]
        public void The_Bolt_Deals_One_Until_Your_Hero_Has_Been_Damaged_This_Turn()
        {
            CardDefinition kindle = Kindle(2, 2, 1);
            var db = new FakeCardDatabase().Register(Bolt()).Register(kindle);
            GameState s = State();

            Press(s, db, s.Players[1].Hero.Id);
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(29));

            s.Players[0].HeroPowerUsedThisTurn = false;
            Play(s, db, ToHand(s, 0, kindle), s.Players[0].Hero.Id);
            Assert.That(s.Players[0].HeroDamageTakenThisTurn, Is.EqualTo(2));

            Press(s, db, s.Players[1].Hero.Id);
            Assert.That(s.Players[1].Hero.CurrentHealth, Is.EqualTo(27));
        }

        [Test]
        public void A_Bolt_Aimed_At_Your_Own_Hero_Does_Not_Charge_Itself()
        {
            var db = new FakeCardDatabase().Register(Bolt());
            GameState s = State();

            Press(s, db, s.Players[0].Hero.Id);

            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(29), "one hit of one");
            Assert.That(s.Players[0].HeroDamageTakenThisTurn, Is.EqualTo(1));
        }

        [Test]
        public void Damage_Soaked_By_Armor_Still_Counts_And_The_Count_Ends_With_The_Turn()
        {
            CardDefinition filler = Minion("filler", 1, 1);
            var db = new FakeCardDatabase().Register(Bolt()).Register(filler);
            GameState s = State();
            s.Players[0].Hero.Armor = 5;
            s.Players[1].HeroDamageTakenThisTurn = 4;
            // A card to draw, or the incoming turn's fatigue would put damage straight back.
            s.Players[1].Deck.Add(new CardInstance(s.NextInstanceId(), filler.Id, s.Players[1].Id)
            { Zone = Zone.Deck });

            Press(s, db, s.Players[0].Hero.Id);
            Assert.That(s.Players[0].Hero.CurrentHealth, Is.EqualTo(30));
            Assert.That(s.Players[0].HeroDamageTakenThisTurn, Is.EqualTo(1));

            new CommandProcessor(db).Execute(s, new EndTurnIntent(s.ActivePlayer));

            Assert.That(s.Players[0].HeroDamageTakenThisTurn, Is.EqualTo(0));
            Assert.That(s.Players[1].HeroDamageTakenThisTurn, Is.EqualTo(0),
                "both seats clear: what a hero took last turn is not this turn's");
        }

        // ── the validator ─────────────────────────────────────────────────────────

        private static IReadOnlyList<string> Codes(CardDefinition def)
            => new CardValidator().Validate(def, new FakeCardDatabase().Register(def))
                .Issues.Select(issue => issue.Code).ToList();

        [Test]
        public void A_Rider_With_No_Rule_To_Ride_Is_Refused()
        {
            CardDefinition orphan = Spell("orphan", MinionRider(2, 1));

            Assert.That(Codes(orphan), Does.Contain("C32-RIDER-CHOICE"));
        }

        [Test]
        public void A_Rider_Is_Refused_When_The_Choice_Can_Never_Be_Your_Own()
        {
            CardDefinition enemyOnly = Spell("enemy_only",
                MinionRider(2, 1),
                TestRules.Rule("burn", Trigger.ON_CAST,
                    new DamageAction(2, new TargetSpec("ANY_ENEMY_MINION", TargetSelection.Of(1)))));

            Assert.That(Codes(enemyOnly), Does.Contain("C32-RIDER-UNREACHABLE"));
        }

        [Test]
        public void An_Instead_Amount_Must_Differ_And_Read_Game_State()
        {
            CardDefinition same = Spell("same",
                TestRules.Rule("burn", Trigger.ON_CAST,
                    new DamageAction(2, Character(), 2, HeroDamagedThisTurnCondition.Instance)));
            CardDefinition wrongSwitch = Spell("wrong_switch",
                TestRules.Rule("burn", Trigger.ON_CAST,
                    new DamageAction(1, Character(), 2, new ControlsCondition(Tribe.CONSTRUCT, false))));

            Assert.That(Codes(same), Does.Contain("C33-INSTEAD-AMOUNT"));
            Assert.That(Codes(wrongSwitch), Does.Contain("C33-INSTEAD-CONDITION"));
        }

        // ── the row ───────────────────────────────────────────────────────────────

        [Test]
        public void The_New_Nodes_Read_Back_From_A_Card_Row()
        {
            const string row = @"{""id"": 1, ""handle"": ""hero_power_bolt"", ""name"": ""Bolt"",
                ""type"": ""HERO_POWER"", ""cost"": 2, ""class"": ""EMBERWARD"", ""rarity"": ""COMMON"",
                ""keywords"": [], ""setId"": ""CORE"", ""collectible"": false, ""artId"": ""bolt"",
                ""text"": """", ""rules"": [
                  {""id"": ""rule_1"", ""activation"": {""type"": ""TRIGGERED"", ""trigger"": ""ON_CAST""},
                   ""condition"": {""type"": ""TARGET_IS_FRIENDLY"", ""kind"": ""HERO""},
                   ""lifetime"": {""type"": ""ONE_SHOT""}, ""action"": {""type"": ""DRAW"", ""count"": 1}},
                  {""id"": ""rule_2"", ""activation"": {""type"": ""TRIGGERED"", ""trigger"": ""ON_CAST""},
                   ""condition"": {""type"": ""ALWAYS""}, ""lifetime"": {""type"": ""ONE_SHOT""},
                   ""action"": {""type"": ""DAMAGE"", ""amount"": 1,
                     ""instead"": {""amount"": 2, ""condition"": {""type"": ""HERO_DAMAGED_THIS_TURN""}},
                     ""target"": {""selector"": ""ANY_CHARACTER"", ""selection"": ""1""}}}]}";

            CardDefinition def = CardJson.Deserialize(row);

            var rider = (TargetIsFriendlyCondition)def.Rules[0].Condition;
            var damage = (DamageAction)def.Rules[1].Action;
            Assert.That(rider.Kind, Is.EqualTo(FriendlyTargetKind.HERO));
            Assert.That((damage.Amount, damage.InsteadAmount), Is.EqualTo((1, (int?)2)));
            Assert.That(damage.InsteadCondition, Is.SameAs(HeroDamagedThisTurnCondition.Instance));
        }
    }
}
