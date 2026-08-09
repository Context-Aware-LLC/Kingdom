using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class NormalizedRuleCorpusTests
    {
        [Test]
        public void Every_Live_Row_Uses_Rules_Only_And_Has_Stable_Unique_Rule_Ids()
        {
            string data = Path.Combine(TestContext.CurrentContext.TestDirectory,
                "..", "..", "..", "..", "..", "unity", "Emberfall", "Assets", "CardData");
            int rows = 0;
            foreach (string path in Directory.GetFiles(data, "*.cards.json"))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (JsonElement card in document.RootElement.EnumerateArray())
                {
                    rows++;
                    string cardId = card.GetProperty("id").GetString()!;
                    Assert.That(card.TryGetProperty("effects", out _), Is.False,
                        $"{cardId} still declares the removed effects[] schema.");
                    Assert.That(card.TryGetProperty("cardText", out _), Is.False,
                        $"{cardId} still declares the removed cardText schema.");
                    Assert.That(card.TryGetProperty("rules", out JsonElement rules), Is.True,
                        $"{cardId} must explicitly declare rules[], including an empty array.");
                    Assert.That(rules.ValueKind, Is.EqualTo(JsonValueKind.Array));
                    var ids = new HashSet<string>();
                    foreach (JsonElement rule in rules.EnumerateArray())
                    {
                        string? id = rule.GetProperty("id").GetString();
                        Assert.That(id, Is.Not.Null.And.Not.Empty, $"{cardId} has an empty rule id.");
                        Assert.That(ids.Add(id!), Is.True, $"{cardId} repeats rule id '{id}'.");
                    }
                }
            }
            Assert.That(rows, Is.EqualTo(261));
        }
    }
}
