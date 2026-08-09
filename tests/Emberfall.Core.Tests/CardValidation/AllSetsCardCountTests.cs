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
    /// Loader test: asserts the client card database (Emberfall+Rekindling) equals CardCi's count.
    /// Satisfies EF-040 DONE-WHEN: client database count equals 260.
    /// Update counts here when cards are added to any set.
    /// </summary>
    [TestFixture]
    [Category("CardValidation")]
    public sealed class AllSetsCardCountTests
    {
        private static readonly string[] SetFiles =
        {
            Path.Combine("unity", "Emberfall", "Assets", "CardData", "Emberfall.cards.json"),
            Path.Combine("unity", "Emberfall", "Assets", "CardData", "Rekindling.cards.json"),
        };

        private static string CardDataPath(string repoRelative)
            => Path.Combine(RepoPaths.RepoRoot, repoRelative);

        private static IReadOnlyList<CardDefinition> LoadSet(string repoRelative)
        {
            string path = CardDataPath(repoRelative);
            Assert.That(File.Exists(path), Is.True, $"Card set file not found: {path}");
            return CardJson.DeserializeArray(File.ReadAllText(path));
        }

        private static string ReferencedCardId(RuleAction action)
            => action switch
            {
                SummonAction summon => summon.CardId,
                AddToHandAction addToHand => addToHand.CardId,
                EquipAction equip => equip.CardId,
                TransformAction transform => transform.CardId,
                _ => "",
            };

        [Test]
        public void Client_Database_Loads_Both_Sets()
        {
            var emberfall = LoadSet(SetFiles[0]);
            var rekindling = LoadSet(SetFiles[1]);

            // 185 = the former M1 (143) + M2 (42) files, merged into one Emberfall set file.
            Assert.That(emberfall.Count, Is.EqualTo(185), "Emberfall set count mismatch (card_inventory.py: 185)");
            // 75 since NOR-126/NOR-131 added the CLOCKWRIGHT and TEMPEST signature weapons.
            Assert.That(rekindling.Count, Is.EqualTo(75), "Rekindling set count mismatch (card_inventory.py: 75)");

            var combined = emberfall.Concat(rekindling).ToList();
            Assert.That(combined.Count, Is.EqualTo(260),
                "Total card count across Emberfall+Rekindling must match CardCi output (card_inventory.py: 260).");
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
                string.Join("\u001e", card.Rules.Select(rule => ReferencedCardId(rule.Action))),
            })));
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));

            // Re-pinned 2026-08-06 for the art rename, which dropped the `art/` namespace and the
            // set prefix from every artId (`art/core_aegis_templar` -> `aegis_templar`). ArtId is one
            // of the projected identity fields, so renaming all 260 moves this hash. Verified before
            // re-pinning that artId is the ONLY field that changed, across every row and every baked
            // CardDefinitionSO. Update this pin only for a reviewed whole-token identity change.
            Assert.That(hash, Is.EqualTo("2C1475A9FD2FE19C105C985368F5F43122A3D0D4092B03B7EFEABD502539132B"),
                "Card identity fields changed. Update this pin only for a reviewed whole-token identity change.");

            // An artId no longer tracks the card id: art is named for the subject, not the token's
            // stat line, so the 5/5 survival token draws `token_revenant` like every other token.
            // The whole-token identity this test guards is the RULE REFERENCE below, not the artId.
            var survivalToken = all.Single(card => card.Id == "token_revenant_5_5_bulwark");
            Assert.That(survivalToken.ArtId, Is.EqualTo("token_revenant"));
            var gateKeeperRule = all.Single(card => card.Id == "grave_sealed_gate_keeper").Rules.Single();
            Assert.That(ReferencedCardId(gateKeeperRule.Action),
                Is.EqualTo(survivalToken.Id),
                "The sole reviewed token reference must follow the canonical whole-token identity.");
        }

        [Test]
        public void All_Cards_Pass_Core_Validation()
        {
            var allCards = SetFiles.SelectMany(LoadSet).ToList();
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
