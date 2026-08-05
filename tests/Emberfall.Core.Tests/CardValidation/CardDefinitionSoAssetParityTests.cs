using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Emberfall.Core.Cards;
using Emberfall.Core.Tests.Support;
using Emberfall.Editor;
using NUnit.Framework;

namespace Emberfall.Core.Tests.CardValidation
{
    [TestFixture]
    [Category("CardValidation")]
    public sealed class CardDefinitionSoAssetParityTests
    {
        private static readonly Regex Name = new Regex(
            @"^  m_Name: (?<id>[^\r\n]+)\r?$", RegexOptions.Multiline);
        private static readonly Regex Json = new Regex(
            @"^  _json: '(?<json>(?:''|[^'\r\n])*)'\r?$", RegexOptions.Multiline);

        [Test]
        public void Every_Tracked_CardDefinitionSo_Parses_And_Matches_Its_Canonical_Row()
        {
            string root = RepoPaths.RepoRoot;
            string cardData = Path.Combine(root, "unity", "Emberfall", "Assets", "CardData");
            string assets = Path.Combine(root, "unity", "Emberfall", "Assets", "Cards");
            var canonical = new Dictionary<string, CardDefinition>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(cardData, "*.cards.json"))
            {
                foreach (CardDefinition card in CardJson.DeserializeArray(File.ReadAllText(path)))
                    canonical.Add(card.Id, card);
            }

            string[] assetPaths = Directory.GetFiles(assets, "*.asset", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Assert.That(assetPaths, Has.Length.EqualTo(36),
                "A changed asset count requires an explicit CardDefinitionSO parity review.");

            foreach (string path in assetPaths)
            {
                string yaml = File.ReadAllText(path);
                Match name = Name.Match(yaml);
                Match json = Json.Match(yaml);
                Assert.That(name.Success, Is.True, $"{path} is missing its CardDefinitionSO m_Name.");
                Assert.That(json.Success, Is.True, $"{path} has no normalized one-line _json scalar.");
                if (!name.Success || !json.Success) continue;

                string cardId = name.Groups["id"].Value;
                string assetJson = json.Groups["json"].Value.Replace("''", "'");
                Assert.That(Path.GetFileNameWithoutExtension(path), Is.EqualTo(cardId), path);
                Assert.That(canonical.TryGetValue(cardId, out CardDefinition? source), Is.True,
                    $"{path} has no matching canonical CardData row.");
                if (source == null) continue;

                CardDefinition baked = CardJson.Deserialize(assetJson);
                Assert.Multiple(() =>
                {
                    Assert.That(baked.Id, Is.EqualTo(cardId));
                    Assert.That(CardJsonRowIO.Serialize(baked),
                        Is.EqualTo(CardJsonRowIO.Serialize(source)), path);
                    Assert.That(CardTextGenerator.Generate(baked),
                        Is.EqualTo(CardTextGenerator.Generate(source)),
                        $"{path} must derive presentation text from the same typed rules.");
                });
            }
        }
    }
}
