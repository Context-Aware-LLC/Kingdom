using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    /// <summary>
    /// The character-creation detail overlay shows a class's hero power with its name taken from the
    /// power card and its description generated from that card's typed rules (NOR-291) — no prose is
    /// stored, so the description cannot drift from the rules. That only holds while every class
    /// actually maps to a card the generator can render.
    ///
    /// <para>This fixture is the gate on both halves. A retuned hero power whose new rule shape the
    /// generator does not support would otherwise surface as a blank block, or an exception thrown
    /// mid-overlay, on a screen no headless test opens.</para>
    /// </summary>
    [TestFixture]
    [Category("CardValidation")]
    public sealed class HeroPowerTextTests
    {
        private static IReadOnlyDictionary<string, CardDefinition> LoadCanonicalRows()
        {
            string cardData = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData");
            var rows = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(cardData, "*.cards.json"))
                foreach (CardDefinition card in CardJson.DeserializeArray(File.ReadAllText(path)))
                    rows.Add(card.Id, card);
            return rows;
        }

        [Test]
        public void Every_Real_Class_Has_A_Hero_Power_Whose_Text_Generates()
        {
            IReadOnlyDictionary<string, CardDefinition> rows = LoadCanonicalRows();

            foreach (CardClass cls in (CardClass[])Enum.GetValues(typeof(CardClass)))
            {
                if (cls == CardClass.NEUTRAL) continue;

                string powerId = HeroPowers.ForClass(cls);
                Assert.That(powerId, Is.Not.Empty, $"{cls} has no hero power id.");
                Assert.That(rows.ContainsKey(powerId), Is.True,
                    $"{cls}'s hero power '{powerId}' is not in any *.cards.json row.");

                CardDefinition power = rows[powerId];
                Assert.That(power.Type, Is.EqualTo(CardType.HERO_POWER), powerId);
                Assert.That(power.Class, Is.EqualTo(cls),
                    $"'{powerId}' is mapped to {cls} but its row says {power.Class}.");
                Assert.That(power.Name, Is.Not.Empty, $"'{powerId}' has no name to show.");

                string text = CardTextGenerator.Generate(power);
                Assert.That(text, Is.Not.Empty,
                    $"'{powerId}' generates no description for the create overlay.");
                TestContext.Out.WriteLine($"{cls,-12} {power.Name,-14} {text}");
            }
        }

        /// <summary>
        /// The mapping is 1:1 in both directions: no two classes share a power, and no HERO_POWER row
        /// exists that no class can reach. An orphaned power row is a class whose power was renamed
        /// in the data and not in the map — invisible until a player opens that hero's detail.
        /// </summary>
        [Test]
        public void Hero_Power_Rows_And_The_Class_Map_Agree_Exactly()
        {
            var mapped = new Dictionary<string, CardClass>(StringComparer.Ordinal);
            foreach (CardClass cls in (CardClass[])Enum.GetValues(typeof(CardClass)))
            {
                if (cls == CardClass.NEUTRAL) continue;
                string powerId = HeroPowers.ForClass(cls);
                Assert.That(mapped.ContainsKey(powerId), Is.False,
                    $"'{powerId}' is mapped to both {(mapped.TryGetValue(powerId, out CardClass other) ? other : cls)} and {cls}.");
                mapped.Add(powerId, cls);
            }

            foreach (KeyValuePair<string, CardDefinition> row in LoadCanonicalRows())
            {
                if (row.Value.Type != CardType.HERO_POWER) continue;
                Assert.That(mapped.ContainsKey(row.Key), Is.True,
                    $"HERO_POWER row '{row.Key}' is reachable from no class in HeroPowers.ForClass.");
            }
        }
    }
}
