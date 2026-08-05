using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.CardValidation
{
    /// <summary>
    /// Runs the real CardValidator (C1-C20) against the real M1 card set, not synthetic fixtures,
    /// so a rule regression against actual content is caught (dev-docs/pipelines/card-authoring-and-import.md:11).
    /// </summary>
    [TestFixture]
    [Category("CardValidation")]
    public sealed class CardValidatorTests
    {
        private static string LoadM1CardsJson()
        {
            // tests/Emberfall.Core.Tests -> repo root -> unity/Emberfall/Assets/CardData/M1.cards.json
            // (moved out of Resources/ by the M2 D1 Addressables migration)
            string dir = TestContext.CurrentContext.TestDirectory;
            string? path = dir;
            for (int i = 0; i < 8 && path != null; i++)
            {
                string candidate = Path.Combine(path, "unity", "Emberfall", "Assets", "CardData", "M1.cards.json");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                path = Path.GetDirectoryName(path);
            }
            Assert.Fail("Could not locate unity/Emberfall/Assets/CardData/M1.cards.json from test directory.");
            return null!;
        }

        private static (System.Collections.Generic.IReadOnlyList<CardDefinition> cards, JsonCardDatabaseTestDouble db) LoadM1Set()
        {
            var json = LoadM1CardsJson();
            var cards = CardJson.DeserializeArray(json);
            return (cards, new JsonCardDatabaseTestDouble(cards));
        }

        [Test]
        public void All_M1_Cards_Pass_Core_Validation()
        {
            // ANY_MINION / ANY_FRIENDLY_MINION were canonized into the Selector vocabulary by the
            // M2 Phase 0 grammar frost (dev-docs/specs/card-schema.md §5.3), so tide_undertow — previously the
            // one known-bad row — now validates clean along with everything else.
            var (cards, db) = LoadM1Set();
            var validator = new CardValidator();

            var errorsByCard = cards
                .Select(c => (card: c, report: validator.Validate(c, db)))
                .Where(r => !r.report.Ok)
                .ToList();

            Assert.That(errorsByCard, Is.Empty,
                "Unexpected validation errors: " + string.Join("; ", errorsByCard.Select(r =>
                    $"{r.card.Id}: " + string.Join(", ", r.report.Issues.Where(i => i.Severity == Severity.Error).Select(i => i.Code)))));
        }

        [Test]
        public void CardJson_Preserves_The_Complete_Target_Object()
        {
            const string json = @"{
              ""id"":""target_shape"", ""name"":""Target Shape"", ""type"":""SPELL"", ""cost"":1,
              ""class"":""NIGHTVEIL"", ""rarity"":""COMMON"", ""keywords"":[], ""setId"":""CORE"",
              ""rules"":[{
                ""id"":""destroy_two"",
                ""activation"":{""type"":""TRIGGERED"",""trigger"":""ON_CAST""},
                ""condition"":{""type"":""ALWAYS""},
                ""lifetime"":{""type"":""ONE_SHOT""},
                ""action"":{""type"":""DESTROY"",""target"":{
                  ""selector"":""ANY_MINION"", ""chosenBy"":""PLAYER"", ""count"":2,
                  ""binding"":""victims"", ""distinctFrom"":""first"",
                  ""filter"":{""damaged"":true,""tribe"":""CONSTRUCT""}
                }}
              }]
            }";

            TargetSpec target = ((DestroyAction)CardJson.Deserialize(json).Rules[0].Action).Target;

            Assert.That(target.Selector, Is.EqualTo("ANY_MINION"));
            Assert.That(target.ChosenBy, Is.EqualTo(TargetChosenBy.PLAYER));
            Assert.That(target.Count, Is.EqualTo(2));
            Assert.That(target.Binding, Is.EqualTo("victims"));
            Assert.That(target.DistinctFrom, Is.EqualTo("first"));
            Assert.That(target.Filter!.Damaged, Is.True);
            Assert.That(target.Filter.Tribe, Is.EqualTo(Tribe.CONSTRUCT));
        }

        [Test]
        public void Adjacent_Selector_Is_Implemented_Without_A_Primitive_Gate()
        {
            RuleDefinition Adjacent() => Rule(Trigger.ON_PLAY,
                new DamageAction(1, new TargetSpec("ADJACENT")));
            CardDefinition Card(bool gated) => new CardDefinition(
                id: "test_adjacent", name: "Test", type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: new[] { Adjacent() },
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: gated);

            var validator = new CardValidator();
            var ungated = validator.Validate(Card(gated: false), new JsonCardDatabaseTestDouble(new[] { Card(false) }));
            Assert.That(ungated.Ok, Is.True);

            var gated = validator.Validate(Card(gated: true), new JsonCardDatabaseTestDouble(new[] { Card(true) }));
            Assert.That(gated.Ok, Is.True);
        }

        [Test]
        public void GainMana_Rejects_An_Unknown_Typed_Kind()
        {
            var rule = new RuleDefinition(new RuleId("gain_mana"),
                new TriggeredActivation(Trigger.ON_CAST), AlwaysCondition.Instance,
                OneShotLifetime.Instance, new GainManaAction(1, (ManaGainKind)999));
            var def = new CardDefinition(
                id: "test_empty_mana", name: "Test", type: CardType.SPELL, cost: 1,
                attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: new[] { rule },
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

            var report = new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));
            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C15-MANA-KIND" && i.Severity == Severity.Error));
        }

        [Test]
        public void CheckDuplicateIds_Flags_Real_Collisions_Only()
        {
            var (cards, _) = LoadM1Set();
            var issues = CardValidator.CheckDuplicateIds(cards);
            Assert.That(issues, Is.Empty, "M1.cards.json is not expected to contain duplicate ids today.");

            var withDuplicate = cards.Append(cards[0]).ToList();
            var dupIssues = CardValidator.CheckDuplicateIds(withDuplicate);
            Assert.That(dupIssues.Count, Is.EqualTo(2), "Both instances of the duplicated id should be flagged.");
            Assert.That(dupIssues.All(i => i.Code == "C2-ID-DUPLICATE"));
        }

        [Test]
        public void Minion_Missing_Health_Is_An_Error()
        {
            var def = new CardDefinition(
                id: "test_bad_minion", name: "Test", type: CardType.MINION, cost: 1,
                attack: 1, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: System.Array.Empty<RuleDefinition>(),
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

            var report = new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));
            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C4-MINION-HEALTH"));
        }

        [Test]
        public void Summon_Without_TokenId_Is_An_Error()
        {
            RuleDefinition rule = Rule(Trigger.ON_DEATH, new SummonAction("", 1));
            var def = new CardDefinition(
                id: "test_bad_summon", name: "Test", type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: new[] { rule },
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

            var report = new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));
            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C11-PAYLOAD-REQUIRED"));
        }

        [Test]
        public void RequiresEngineeringPrimitive_Flows_Through_To_Report()
        {
            var def = new CardDefinition(
                id: "test_primitive", name: "Test", type: CardType.SPELL, cost: 1,
                attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: System.Array.Empty<RuleDefinition>(),
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: true);

            var report = new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));
            Assert.That(report.RequiresEngineeringPrimitive, Is.True);
        }

        [Test]
        public void Action_Target_Capability_Rejects_A_Selector_Whose_Objects_The_Handler_Cannot_Affect()
        {
            RuleDefinition rule = Rule(Trigger.ON_CAST,
                new ModifyStatsAction(1, 0, new TargetSpec("FRIENDLY_WEAPON")));
            CardDefinition def = WithRule(CardType.SPELL, rule);

            ValidationReport report = Validator().Validate(def, Db(def));

            Assert.That(report.Issues.Any(i =>
                i.Code == "C14F-ACTION-TARGET-INCOMPATIBLE" && i.Severity == Severity.Error), Is.True);
        }

        [TestCase(CardType.WEAPON, Trigger.ON_PLAY)]
        [TestCase(CardType.SPELL, Trigger.ON_CAST)]
        public void Self_Capability_Uses_The_Source_Lifecycle_Instead_Of_An_AnyKind_Wildcard(
            CardType type, Trigger trigger)
        {
            RuleDefinition rule = Rule(trigger,
                new ModifyStatsAction(1, 0, new TargetSpec("SELF")));
            CardDefinition def = WithRule(type, rule);

            ValidationReport report = Validator().Validate(def, Db(def));

            Assert.That(report.Issues.Any(i => i.Code == "C14F-ACTION-TARGET-INCOMPATIBLE"), Is.True,
                $"BUFF cannot affect the actual {type} SELF carrier at {trigger}.");
        }

        [Test]
        public void TriggerSource_Capability_Fails_Closed_When_The_Event_Source_Kind_Is_Incompatible()
        {
            RuleDefinition rule = Rule(Trigger.ON_SPELL_CAST,
                new ModifyStatsAction(1, 0, new TargetSpec("TRIGGER_SOURCE")));
            CardDefinition def = WithRule(CardType.MINION, rule);

            ValidationReport report = Validator().Validate(def, Db(def));

            Assert.That(report.Issues.Any(i => i.Code == "C14F-ACTION-TARGET-INCOMPATIBLE"), Is.True,
                "ON_SPELL_CAST's trigger source is a graveyard card, not a BUFF-capable minion.");
        }

        // ── C21 — trigger liveness ───────────────────────────────────────────────────
        // A dead trigger (declared in the enum but raised nowhere) is silent at runtime; C21 is
        // the only guard. These lock the table so flipping a trigger live/dead is a deliberate edit.

        private static CardValidator Validator() => new CardValidator();
        private static JsonCardDatabaseTestDouble Db(CardDefinition d) => new JsonCardDatabaseTestDouble(new[] { d });

        private static CardDefinition WithRule(CardType type, RuleDefinition rule)
            => new CardDefinition(
                id: "test_c21", name: "Test", type: type, cost: 1,
                attack: type == CardType.MINION || type == CardType.WEAPON ? 1 : (int?)null,
                health: type == CardType.MINION ? 1 : (int?)null,
                durability: type == CardType.WEAPON ? 1 : (int?)null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), rules: new[] { rule },
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        private static RuleDefinition Rule(Trigger trigger, RuleAction action,
            RuleCondition? condition = null)
            => new RuleDefinition(new RuleId("rule_1"), new TriggeredActivation(trigger),
                condition ?? AlwaysCondition.Instance,
                trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST
                    ? (RuleLifetime)OneShotLifetime.Instance
                    : SourceBoundLifetime.Instance,
                action);

        private static RuleDefinition TurnStart(TurnOwner owner)
            => Rule(Trigger.ON_TURN_START, new DrawAction(1), new TurnOwnerCondition(owner));

        [Test]
        public void Dead_Trigger_Is_An_Error()
        {
            // AURA is declared in the Trigger enum but has no raise site — and cannot get one until a
            // derived-stat recompute pass exists (EF-017a/b). This probe has migrated twice as triggers
            // went live under it: ON_TURN_START (EF-011), then ON_HEAL (EF-015). AURA is the last one
            // left, so when EF-017b lands this test needs a different subject or a different shape.
            var rule = new RuleDefinition(new RuleId("rule_1"), AuraActivation.Instance,
                AlwaysCondition.Instance, SourceBoundLifetime.Instance, new DrawAction(1));
            var report = Validator().Validate(WithRule(CardType.MINION, rule), Db(WithRule(CardType.MINION, rule)));
            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C21-TRIGGER-DEAD"));
        }

        [Test]
        public void OnHeal_Is_Live_On_A_Minion_And_Dead_Everywhere_Else()
        {
            // EF-015 flipped ON_HEAL from Dead to MinionOnly. DamagePipeline.ApplyHeal raises it on the
            // character whose health was restored, and only a minion can be that character: a SPELL is
            // not a permanent, a WEAPON has no health. So the type gate here is permanent, not staging.
            RuleDefinition Heal() => Rule(Trigger.ON_HEAL, new DrawAction(1));

            var onMinion = Validator().Validate(WithRule(CardType.MINION, Heal()), Db(WithRule(CardType.MINION, Heal())));
            Assert.That(onMinion.Ok, Is.True,
                "a minion 'whenever this is healed' effect has a runtime raise site (DamagePipeline.ApplyHeal).");
            Assert.That(onMinion.Issues.Any(i => i.Code == "C21-TRIGGER-DEAD"), Is.False,
                "the dead-trigger gate must not fire on ON_HEAL any more.");

            foreach (var type in new[] { CardType.SPELL, CardType.WEAPON })
            {
                var report = Validator().Validate(WithRule(type, Heal()), Db(WithRule(type, Heal())));
                Assert.That(report.Ok, Is.False, $"ON_HEAL on a {type} is inert — nothing there can be healed.");
                Assert.That(report.Issues.Any(i => i.Code == "C21-TRIGGER-MINION-ONLY"), Is.True,
                    $"ON_HEAL on a {type} must be rejected as minion-only.");
            }
        }

        [Test]
        public void OnAttack_Is_Dead_On_A_Minion_But_Live_On_A_Weapon()
        {
            RuleDefinition Attack() => Rule(Trigger.ON_ATTACK, new DrawAction(1));

            var onMinion = Validator().Validate(WithRule(CardType.MINION, Attack()), Db(WithRule(CardType.MINION, Attack())));
            Assert.That(onMinion.Ok, Is.False);
            Assert.That(onMinion.Issues.Any(i => i.Code == "C21-TRIGGER-WEAPON-ONLY"));

            var onWeapon = Validator().Validate(WithRule(CardType.WEAPON, Attack()), Db(WithRule(CardType.WEAPON, Attack())));
            Assert.That(onWeapon.Ok, Is.True, "ON_ATTACK is raised for weapons — a weapon 'whenever this attacks' effect is legal.");
        }

        [TestCase(TurnOwner.CONTROLLER)]
        [TestCase(TurnOwner.OPPONENT)]
        [TestCase(TurnOwner.ANY)]
        public void OnTurnStart_Is_Live_On_A_Minion_With_Explicit_Turn_Ownership(TurnOwner owner)
        {
            var def = WithRule(CardType.MINION, TurnStart(owner));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                $"A minion with explicit {owner} turn ownership has a runtime observation path.");
        }

        [Test]
        public void OnTurnStart_Requires_A_TurnOwner_Condition()
        {
            RuleDefinition rule = Rule(Trigger.ON_TURN_START, new DrawAction(1));
            var def = WithRule(CardType.MINION, rule);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C22-TURN-OWNER-REQUIRED"));
        }

        [Test]
        public void CardJson_Requires_A_TurnOwner_Value()
        {
            const string json = "{\"id\":\"missing_turn_owner\",\"type\":\"MINION\",\"rules\":[{" +
                "\"id\":\"rule_1\",\"activation\":{\"type\":\"TRIGGERED\",\"trigger\":\"ON_TURN_START\"}," +
                "\"condition\":{\"type\":\"TURN_OWNER\"},\"lifetime\":{\"type\":\"SOURCE_BOUND\"}," +
                "\"action\":{\"type\":\"DRAW\",\"count\":1}}]}";

            Assert.Throws<System.FormatException>(() => CardJson.Deserialize(json));
        }

        [Test]
        public void OnTurnStart_Rejects_An_Invalid_TurnOwner_Value()
        {
            var def = WithRule(CardType.MINION, TurnStart((TurnOwner)999));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C22-TURN-OWNER-INVALID"));
        }

        [TestCase(CardType.SPELL)]
        [TestCase(CardType.HERO_POWER)]
        public void OnTurnStart_Is_Rejected_On_Unsupported_Source_Types(CardType type)
        {
            var def = WithRule(type, TurnStart(TurnOwner.ANY));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C21-TRIGGER-SOURCE-UNSUPPORTED"));
        }

        [Test]
        public void OnTurnStart_Is_Live_On_An_Equipped_Weapon()
        {
            var def = WithRule(CardType.WEAPON, TurnStart(TurnOwner.ANY));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                "the shared source policy admits the equipped-weapon observer path");
        }

        [Test]
        public void TurnOwner_Condition_Is_Rejected_On_Other_Triggers()
        {
            RuleDefinition rule = Rule(Trigger.ON_PLAY, new DrawAction(1),
                new TurnOwnerCondition(TurnOwner.CONTROLLER));
            var def = WithRule(CardType.MINION, rule);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C22-TURN-OWNER-TRIGGER"));
        }

        [Test]
        public void CardJson_Parses_The_TurnOwner_Condition()
        {
            const string json = "{\"id\":\"turn_probe\",\"name\":\"Turn Probe\",\"type\":\"MINION\",\"cost\":1,"
                + "\"attack\":1,\"health\":1,\"class\":\"NEUTRAL\",\"rarity\":\"COMMON\",\"keywords\":[],"
                + "\"setId\":\"CORE\",\"collectible\":false,\"artId\":\"art/turn_probe\",\"rules\":[{"
                + "\"id\":\"rule_1\",\"activation\":{\"type\":\"TRIGGERED\",\"trigger\":\"ON_TURN_START\"},"
                + "\"condition\":{\"type\":\"TURN_OWNER\",\"owner\":\"OPPONENT\"},"
                + "\"lifetime\":{\"type\":\"SOURCE_BOUND\"},\"action\":{\"type\":\"DRAW\",\"count\":1}}]}";

            var def = CardJson.Deserialize(json);

            Assert.That(def.Rules[0].Condition, Is.TypeOf<TurnOwnerCondition>());
            Assert.That(((TurnOwnerCondition)def.Rules[0].Condition).Owner, Is.EqualTo(TurnOwner.OPPONENT));
        }

        [Test]
        public void CardJson_Rejects_Unknown_Properties_At_Every_Object_Schema_Level()
        {
            AssertRejectsUnknownProperty(
                "{\"id\":\"unknown_root\",\"type\":\"SPELL\",\"unexpected\":true}",
                "card");
            AssertRejectsUnknownProperty(NormalizedRuleJson(
                "\"unexpected\":true,", "{\"type\":\"DRAW\",\"count\":1}"), "rule");
            AssertRejectsUnknownProperty(NormalizedRuleJson("",
                "{\"type\":\"DRAW\",\"count\":1,\"unexpected\":true}"), "rule.action DRAW");
            AssertRejectsUnknownProperty(NormalizedRuleJson("",
                "{\"type\":\"DAMAGE\",\"amount\":1,\"target\":{\"selector\":\"ANY_MINION\",\"unexpected\":true}}"),
                "target");
            AssertRejectsUnknownProperty(NormalizedRuleJson("",
                "{\"type\":\"DAMAGE\",\"amount\":1,\"target\":{\"selector\":\"ANY_MINION\",\"filter\":{\"damaged\":true,\"unexpected\":true}}}"),
                "target.filter");
        }

        [Test]
        public void CardJson_Rejects_The_Removed_Presentation_Override_Field()
        {
            string json = NormalizedRuleJson("\"vfxEvent\":\"onDraw\",",
                "{\"type\":\"DRAW\",\"count\":1}");

            var ex = Assert.Throws<System.FormatException>(() => CardJson.Deserialize(json));

            Assert.That(ex!.Message, Is.EqualTo("Unknown card JSON property 'vfxEvent' in rule."));
        }

        [Test]
        public void CardJson_Accepts_All_Known_Target_And_Filter_Properties()
        {
            string json = NormalizedRuleJson("",
                "{\"type\":\"DAMAGE\",\"amount\":1,\"target\":{\"selector\":\"ANY_MINION\","
                + "\"chosenBy\":\"RANDOM\",\"count\":-1,\"filter\":{\"damaged\":true,\"tribe\":\"CONSTRUCT\"}}}");

            Assert.DoesNotThrow(() => CardJson.Deserialize(json));
        }

        [Test]
        public void CardJson_Allows_Fixture_Comments_Only_At_The_Card_Root()
        {
            Assert.DoesNotThrow(() => CardJson.Deserialize(
                "{\"_comment\":\"fixture note\",\"id\":\"commented_card\",\"type\":\"SPELL\"}"));

            var ex = Assert.Throws<System.FormatException>(() => CardJson.Deserialize(
                NormalizedRuleJson("\"_comment\":\"not metadata here\",",
                    "{\"type\":\"DRAW\",\"count\":1}")));

            Assert.That(ex!.Message, Is.EqualTo("Unknown card JSON property '_comment' in rule."));
        }

        private static string NormalizedRuleJson(string rulePrefix, string action)
            => "{\"id\":\"typed_json\",\"type\":\"SPELL\",\"rules\":[{" + rulePrefix
                + "\"id\":\"rule_1\",\"activation\":{\"type\":\"TRIGGERED\",\"trigger\":\"ON_CAST\"},"
                + "\"condition\":{\"type\":\"ALWAYS\"},\"lifetime\":{\"type\":\"ONE_SHOT\"},"
                + "\"action\":" + action + "}]}";

        private static void AssertRejectsUnknownProperty(string json, string schemaLevel)
        {
            var ex = Assert.Throws<System.FormatException>(() => CardJson.Deserialize(json));

            Assert.That(ex!.Message, Is.EqualTo($"Unknown card JSON property 'unexpected' in {schemaLevel}."));
        }

        [Test]
        public void OnTurnEnd_Is_Live_After_EF012()
        {
            RuleDefinition rule = Rule(Trigger.ON_TURN_END, new DrawAction(1));
            var def = WithRule(CardType.MINION, rule);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                "EF-012 raises ON_TURN_END from CommandProcessor.ApplyEndTurn, so C21 must not classify it as dead.");
        }

        [Test]
        public void OnDamaged_Is_Live_After_EF013()
        {
            RuleDefinition rule = Rule(Trigger.ON_DAMAGED, new DrawAction(1));
            var def = WithRule(CardType.MINION, rule);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                "EF-013 raises ON_DAMAGED from DamagePipeline, so C21 must not classify it as dead.");
        }

        [Test]
        public void OnSpellCast_Is_Live_After_EF016()
        {
            RuleDefinition rule = Rule(Trigger.ON_SPELL_CAST, new DrawAction(1));
            var def = WithRule(CardType.MINION, rule);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                "EF-016 raises ON_SPELL_CAST from ApplyPlayCard's CardType.SPELL arm, so C21 must not classify it as dead.");
        }

        [Test]
        public void OnDeath_Is_Live_On_Both_A_Minion_And_A_Weapon_After_EF020()
        {
            RuleDefinition Death() => Rule(Trigger.ON_DEATH, new DrawAction(1));

            var onMinion = Validator().Validate(WithRule(CardType.MINION, Death()), Db(WithRule(CardType.MINION, Death())));
            Assert.That(onMinion.Ok, Is.True, "ON_DEATH is a live minion toll.");

            var onWeapon = Validator().Validate(WithRule(CardType.WEAPON, Death()), Db(WithRule(CardType.WEAPON, Death())));
            Assert.That(onWeapon.Ok, Is.True,
                "EF-020 gave RulesEngine.BreakWeapon a raise site, so a weapon toll is legal — "
                + "C21-WEAPON-TOLL is retired and must not come back.");
            Assert.That(onWeapon.Issues.Any(i => i.Code == "C21-WEAPON-TOLL"), Is.False,
                "the retired weapon-toll gate must not fire.");
        }

        [Test]
        public void Every_Trigger_Enum_Member_Has_An_Explicit_Liveness_Expectation()
        {
            // Fail-closed only works if every enum member has an explicit live/dead expectation.
            var live = new[] { Trigger.ON_PLAY, Trigger.ON_CAST, Trigger.ON_DEATH,
                               Trigger.ON_FRIENDLY_SUMMON, Trigger.ON_ATTACK, Trigger.ON_DAMAGED,
                               Trigger.ON_TURN_START, Trigger.ON_TURN_END, Trigger.ON_SPELL_CAST,
                               Trigger.ON_HEAL };
            var dead = new[] { Trigger.AURA };
            foreach (Trigger trigger in live)
                Assert.That(EffectSourceCapabilities.SupportsAny(trigger), Is.True,
                    $"{trigger} is expected to have at least one source/raise path.");
            foreach (Trigger trigger in dead)
                Assert.That(EffectSourceCapabilities.SupportsAny(trigger), Is.False,
                    $"{trigger} is expected to remain fail-closed.");
            var accounted = live.Concat(dead).ToHashSet();
            var missing = System.Enum.GetValues(typeof(Trigger)).Cast<Trigger>().Where(t => !accounted.Contains(t)).ToList();
            Assert.That(missing, Is.Empty,
                "Trigger(s) lack a C21 expectation — update EffectSourceCapabilities and this test: "
                + string.Join(", ", missing));
        }
    }

    /// <summary>Minimal ICardDatabase test double so these tests don't depend on Emberfall.Data (Unity asmdef).</summary>
    internal sealed class JsonCardDatabaseTestDouble : ICardDatabase
    {
        private readonly System.Collections.Generic.Dictionary<string, CardDefinition> _cards =
            new System.Collections.Generic.Dictionary<string, CardDefinition>();

        public JsonCardDatabaseTestDouble(System.Collections.Generic.IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _cards[d.Id] = d;
        }

        public bool TryGet(string cardId, out CardDefinition def) => _cards.TryGetValue(cardId, out def!);

        public CardDefinition Get(string cardId) => _cards[cardId];

        public System.Collections.Generic.IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var result = new System.Collections.Generic.List<CardDefinition>();
            foreach (var d in _cards.Values) if (d.PoolEligible) result.Add(d);
            return result;
        }
    }
}
