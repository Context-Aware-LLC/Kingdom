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
            foreach (var d in _cards.Values) if (d.Collectible) result.Add(d);
            return result;
        }
    }
}
