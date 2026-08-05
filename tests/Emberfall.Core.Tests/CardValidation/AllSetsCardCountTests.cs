using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardValidation
{
    /// <summary>
    /// Loader test: asserts the client card database (M1+M2+M3) equals CardCi's count.
    /// Satisfies EF-040 DONE-WHEN: client database count equals 260.
    /// Update counts here when cards are added to any set.
    /// </summary>
    [TestFixture]
    [Category("CardValidation")]
    public sealed class AllSetsCardCountTests
    {
        private static readonly string[] SetFiles =
        {
            Path.Combine("unity", "Emberfall", "Assets", "CardData", "M1.cards.json"),
            Path.Combine("unity", "Emberfall", "Assets", "CardData", "M2.cards.json"),
            Path.Combine("unity", "Emberfall", "Assets", "CardData", "M3.cards.json"),
        };

        private static string CardDataPath(string repoRelative)
            => Path.Combine(RepoPaths.RepoRoot, repoRelative);

        private static IReadOnlyList<CardDefinition> LoadSet(string repoRelative)
        {
            string path = CardDataPath(repoRelative);
            Assert.That(File.Exists(path), Is.True, $"Card set file not found: {path}");
            return CardJson.DeserializeArray(File.ReadAllText(path));
        }

        [Test]
        public void Client_Database_Loads_All_Three_Sets()
        {
            var m1 = LoadSet(SetFiles[0]);
            var m2 = LoadSet(SetFiles[1]);
            var m3 = LoadSet(SetFiles[2]);

            Assert.That(m1.Count, Is.EqualTo(143), "M1 set count mismatch (card_inventory.py: 143)");
            Assert.That(m2.Count, Is.EqualTo(42), "M2 set count mismatch (card_inventory.py: 42)");
            // 75 since NOR-126/NOR-131 added the CLOCKWRIGHT and TEMPEST signature weapons.
            Assert.That(m3.Count, Is.EqualTo(75), "M3 set count mismatch (card_inventory.py: 75)");

            var combined = m1.Concat(m2).Concat(m3).ToList();
            Assert.That(combined.Count, Is.EqualTo(260),
                "Total card count across M1+M2+M3 must match CardCi output (card_inventory.py: 260).");
        }

        [Test]
        public void No_Duplicate_Ids_Across_All_Sets()
        {
            var all = new List<CardDefinition>();
            foreach (var file in SetFiles)
                all.AddRange(LoadSet(file));

            var ids = all.Select(c => c.Id).ToList();
            var dupes = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.That(dupes, Is.Empty,
                "Duplicate card ids found across sets: " + string.Join(", ", dupes));
        }

        [Test]
        public void Canonical_Card_Identity_Projection_Is_Pinned()
        {
            var all = SetFiles.SelectMany(LoadSet).ToList();
            Assert.That(all.Count, Is.EqualTo(260));

            string projection = string.Join("\n", all.Select(card => string.Join("\u001f", new[]
            {
                card.Id,
                card.Name,
                card.ArtId,
                card.Type.ToString(),
                card.Class.ToString(),
                card.Rarity.ToString(),
                card.SetId.ToString(),
                card.Tribe?.ToString() ?? "",
                card.FactionId,
                card.Collectible.ToString(),
                string.Join("\u001e", card.Effects.Select(effect => effect.TokenId ?? "")),
            })));
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));

            Assert.That(hash, Is.EqualTo("6BB9A79E56FC54F5DF4DB773796008651E0BFE1561BFB547D9EF600919A37A7B"),
                "Card identity fields changed. Update this pin only for a reviewed whole-token identity change.");

            var survivalToken = all.Single(card => card.Id == "token_revenant_5_5_bulwark");
            Assert.That(survivalToken.ArtId, Is.EqualTo("art/token_revenant_5_5_bulwark"));
            Assert.That(all.Single(card => card.Id == "grave_sealed_gate_keeper").Effects.Single().TokenId,
                Is.EqualTo(survivalToken.Id),
                "The sole reviewed token reference must follow the canonical whole-token identity.");
        }

        [Test]
        public void All_M2_And_M3_Cards_Pass_Core_Validation()
        {
            var m1 = LoadSet(SetFiles[0]);
            var m2 = LoadSet(SetFiles[1]);
            var m3 = LoadSet(SetFiles[2]);
            var allCards = m1.Concat(m2).Concat(m3).ToList();
            var db = new JsonCardDatabaseTestDouble(allCards);
            var validator = new CardValidator();

            var errors = allCards
                .Select(c => (card: c, report: validator.Validate(c, db)))
                .Where(r => !r.report.Ok)
                .Select(r => $"{r.card.Id}: " +
                    string.Join(", ", r.report.Issues
                        .Where(i => i.Severity == Severity.Error)
                        .Select(i => i.Code)))
                .ToList();

            Assert.That(errors, Is.Empty,
                "Validation errors across all sets: " + string.Join("; ", errors));
        }
    }
}
