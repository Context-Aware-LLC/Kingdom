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
            // M2 Phase 0 grammar freeze (dev-docs/specs/card-schema.md §5.3), so tide_undertow — previously the
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
        public void Unimplemented_Selector_Requires_The_Primitive_Gate()
        {
            // ADJACENT is schema-valid but the interpreter cannot resolve it (and now throws instead
            // of silently mistargeting) — so authoring it without requiresEngineeringPrimitive is an error.
            EffectDef Adjacent() => new EffectDef(Trigger.ON_PLAY, Action.DEAL_DAMAGE, amount: 1,
                amount2: null, tokenId: null, grantedKeyword: null, vfxOverride: null, selector: "ADJACENT");
            CardDefinition Card(bool gated) => new CardDefinition(
                id: "test_adjacent", name: "Test", type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), effects: new[] { Adjacent() },
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: gated);

            var validator = new CardValidator();
            var ungated = validator.Validate(Card(gated: false), new JsonCardDatabaseTestDouble(new[] { Card(false) }));
            Assert.That(ungated.Ok, Is.False);
            Assert.That(ungated.Issues.Any(i => i.Code == "C14-SELECTOR-UNIMPLEMENTED"));

            var gated = validator.Validate(Card(gated: true), new JsonCardDatabaseTestDouble(new[] { Card(true) }));
            Assert.That(gated.Ok, Is.True, "requiresEngineeringPrimitive is the documented gate for unimplemented grammar.");
        }

        [Test]
        public void Empty_Without_Permanent_Is_A_Warning_Not_An_Error()
        {
            var effect = new EffectDef(Trigger.ON_CAST, Action.GAIN_MANA, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null, selector: "FRIENDLY_HERO",
                permanent: false, empty: true);
            var def = new CardDefinition(
                id: "test_empty_mana", name: "Test", type: CardType.SPELL, cost: 1,
                attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), effects: new[] { effect },
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

            var report = new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));
            Assert.That(report.Ok, Is.True);
            Assert.That(report.Issues.Any(i => i.Code == "C15-EMPTY-WITHOUT-PERMANENT" && i.Severity == Severity.Warning));
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
                keywords: System.Array.Empty<Keyword>(), effects: System.Array.Empty<EffectDef>(),
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

            var report = new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));
            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C4-MINION-HEALTH"));
        }

        [Test]
        public void Summon_Without_TokenId_Is_An_Error()
        {
            var effect = new EffectDef(Trigger.ON_DEATH, Action.SUMMON, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);
            var def = new CardDefinition(
                id: "test_bad_summon", name: "Test", type: CardType.MINION, cost: 1,
                attack: 1, health: 1, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), effects: new[] { effect },
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
                keywords: System.Array.Empty<Keyword>(), effects: System.Array.Empty<EffectDef>(),
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: true);

            var report = new CardValidator().Validate(def, new JsonCardDatabaseTestDouble(new[] { def }));
            Assert.That(report.RequiresEngineeringPrimitive, Is.True);
        }

        // ── C21 — trigger liveness ───────────────────────────────────────────────────
        // A dead trigger (declared in the enum but raised nowhere) is silent at runtime; C21 is
        // the only guard. These lock the table so flipping a trigger live/dead is a deliberate edit.

        private static CardValidator Validator() => new CardValidator();
        private static JsonCardDatabaseTestDouble Db(CardDefinition d) => new JsonCardDatabaseTestDouble(new[] { d });

        private static CardDefinition WithEffect(CardType type, EffectDef effect)
            => new CardDefinition(
                id: "test_c21", name: "Test", type: type, cost: 1,
                attack: type == CardType.MINION || type == CardType.WEAPON ? 1 : (int?)null,
                health: type == CardType.MINION ? 1 : (int?)null,
                durability: type == CardType.WEAPON ? 1 : (int?)null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: System.Array.Empty<Keyword>(), effects: new[] { effect },
                cardText: "", flavorText: "", artId: "art/x", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        private static EffectDef TurnStart(TurnOwner? owner)
            => new EffectDef(Trigger.ON_TURN_START, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null,
                condition: new EffectConditionDef(ConditionType.TURN_OWNER, owner));

        [Test]
        public void Dead_Trigger_Is_An_Error()
        {
            // ON_HEAL is declared in the Trigger enum but has no raise site (blocked on EF-015). This
            // used to probe ON_TURN_START; EF-011 gave that one a minion raise site, so it is no
            // longer proves anything here.
            var effect = new EffectDef(Trigger.ON_HEAL, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);
            var report = Validator().Validate(WithEffect(CardType.MINION, effect), Db(WithEffect(CardType.MINION, effect)));
            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C21-TRIGGER-DEAD"));
        }

        [Test]
        public void OnAttack_Is_Dead_On_A_Minion_But_Live_On_A_Weapon()
        {
            EffectDef Attack() => new EffectDef(Trigger.ON_ATTACK, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);

            var onMinion = Validator().Validate(WithEffect(CardType.MINION, Attack()), Db(WithEffect(CardType.MINION, Attack())));
            Assert.That(onMinion.Ok, Is.False);
            Assert.That(onMinion.Issues.Any(i => i.Code == "C21-TRIGGER-WEAPON-ONLY"));

            var onWeapon = Validator().Validate(WithEffect(CardType.WEAPON, Attack()), Db(WithEffect(CardType.WEAPON, Attack())));
            Assert.That(onWeapon.Ok, Is.True, "ON_ATTACK is raised for weapons — a weapon 'whenever this attacks' effect is legal.");
        }

        [TestCase(TurnOwner.CONTROLLER)]
        [TestCase(TurnOwner.OPPONENT)]
        [TestCase(TurnOwner.ANY)]
        public void OnTurnStart_Is_Live_On_A_Minion_With_Explicit_Turn_Ownership(TurnOwner owner)
        {
            var def = WithEffect(CardType.MINION, TurnStart(owner));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                $"A minion with explicit {owner} turn ownership has a runtime observation path.");
        }

        [Test]
        public void OnTurnStart_Requires_A_TurnOwner_Condition()
        {
            var effect = new EffectDef(Trigger.ON_TURN_START, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);
            var def = WithEffect(CardType.MINION, effect);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C22-TURN-OWNER-REQUIRED"));
        }

        [Test]
        public void OnTurnStart_Requires_A_TurnOwner_Value()
        {
            var def = WithEffect(CardType.MINION, TurnStart(owner: null));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C22-TURN-OWNER-REQUIRED"));
        }

        [Test]
        public void OnTurnStart_Rejects_An_Invalid_TurnOwner_Value()
        {
            var def = WithEffect(CardType.MINION, TurnStart((TurnOwner)999));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C22-TURN-OWNER-INVALID"));
        }

        [TestCase(CardType.SPELL)]
        [TestCase(CardType.WEAPON)]
        [TestCase(CardType.HERO_POWER)]
        public void OnTurnStart_Is_Rejected_On_Unsupported_Source_Types(CardType type)
        {
            var def = WithEffect(type, TurnStart(TurnOwner.ANY));

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C21-TRIGGER-MINION-ONLY"));
        }

        [Test]
        public void TurnOwner_Condition_Is_Rejected_On_Other_Triggers()
        {
            var effect = new EffectDef(Trigger.ON_PLAY, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null,
                condition: new EffectConditionDef(ConditionType.TURN_OWNER, TurnOwner.CONTROLLER));
            var def = WithEffect(CardType.MINION, effect);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.False);
            Assert.That(report.Issues.Any(i => i.Code == "C22-TURN-OWNER-TRIGGER"));
        }

        [Test]
        public void CardJson_Parses_The_TurnOwner_Condition()
        {
            const string json = "{\"id\":\"turn_probe\",\"name\":\"Turn Probe\",\"type\":\"MINION\",\"cost\":1,"
                + "\"attack\":1,\"health\":1,\"class\":\"NEUTRAL\",\"rarity\":\"COMMON\",\"keywords\":[],"
                + "\"setId\":\"CORE\",\"collectible\":false,\"artId\":\"art/turn_probe\",\"effects\":[{"
                + "\"trigger\":\"ON_TURN_START\",\"condition\":{\"type\":\"TURN_OWNER\",\"value\":\"OPPONENT\"},"
                + "\"action\":\"DRAW\",\"amount\":1}]}";

            var def = CardJson.Deserialize(json);

            Assert.That(def.Effects[0].Condition, Is.Not.Null);
            Assert.That(def.Effects[0].Condition!.Type, Is.EqualTo(ConditionType.TURN_OWNER));
            Assert.That(def.Effects[0].Condition!.TurnOwner, Is.EqualTo(TurnOwner.OPPONENT));
        }

        [Test]
        public void OnTurnEnd_Is_Live_After_EF012()
        {
            var effect = new EffectDef(Trigger.ON_TURN_END, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);
            var def = WithEffect(CardType.MINION, effect);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                "EF-012 raises ON_TURN_END from CommandProcessor.ApplyEndTurn, so C21 must not classify it as dead.");
        }

        [Test]
        public void OnDamaged_Is_Live_After_EF013()
        {
            var effect = new EffectDef(Trigger.ON_DAMAGED, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);
            var def = WithEffect(CardType.MINION, effect);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                "EF-013 raises ON_DAMAGED from DamagePipeline, so C21 must not classify it as dead.");
        }

        [Test]
        public void OnSpellCast_Is_Live_After_EF016()
        {
            var effect = new EffectDef(Trigger.ON_SPELL_CAST, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);
            var def = WithEffect(CardType.MINION, effect);

            var report = Validator().Validate(def, Db(def));

            Assert.That(report.Ok, Is.True,
                "EF-016 raises ON_SPELL_CAST from ApplyPlayCard's CardType.SPELL arm, so C21 must not classify it as dead.");
        }

        [Test]
        public void OnDeath_Is_Live_On_Both_A_Minion_And_A_Weapon_After_EF020()
        {
            EffectDef Death() => new EffectDef(Trigger.ON_DEATH, Action.DRAW, amount: 1, amount2: null,
                tokenId: null, grantedKeyword: null, vfxOverride: null);

            var onMinion = Validator().Validate(WithEffect(CardType.MINION, Death()), Db(WithEffect(CardType.MINION, Death())));
            Assert.That(onMinion.Ok, Is.True, "ON_DEATH is a live minion deathrattle.");

            var onWeapon = Validator().Validate(WithEffect(CardType.WEAPON, Death()), Db(WithEffect(CardType.WEAPON, Death())));
            Assert.That(onWeapon.Ok, Is.True,
                "EF-020 gave RulesEngine.BreakWeapon a raise site, so a weapon deathrattle is legal — "
                + "C21-WEAPON-DEATHRATTLE is retired and must not come back.");
            Assert.That(onWeapon.Issues.Any(i => i.Code == "C21-WEAPON-DEATHRATTLE"), Is.False,
                "the retired weapon-deathrattle gate must not fire.");
        }

        [Test]
        public void Every_Trigger_Enum_Member_Is_Tabled()
        {
            // Fail-closed only works if the table is exhaustive; a new enum member with no row is a
            // silent Dead. This asserts an author who adds a Trigger also files it in the C21 table.
            var live = new[] { Trigger.ON_PLAY, Trigger.ON_CAST, Trigger.ON_DEATH,
                               Trigger.ON_FRIENDLY_SUMMON, Trigger.ON_ATTACK, Trigger.ON_DAMAGED,
                               Trigger.ON_TURN_START, Trigger.ON_TURN_END, Trigger.ON_SPELL_CAST };
            var dead = new[] { Trigger.ON_HEAL, Trigger.AURA };
            var accounted = live.Concat(dead).ToHashSet();
            var missing = System.Enum.GetValues(typeof(Trigger)).Cast<Trigger>().Where(t => !accounted.Contains(t)).ToList();
            Assert.That(missing, Is.Empty,
                "Trigger(s) not classified by the C21 test/table — add to TriggerLivenessTable and here: "
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
