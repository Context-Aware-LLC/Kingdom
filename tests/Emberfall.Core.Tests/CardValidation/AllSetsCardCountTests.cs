using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.CardValidation
{
    /// <summary>
    /// Loader test for the client card database (Emberfall+Rekindling). It asserts INVARIANTS, not
    /// totals: both sets load, every row carries its own set id, ids are unique across sets, and a
    /// rule that names a card names one that exists.
    ///
    /// <para>The class name is a fossil. It used to pin 188/76/264 and the counts are gone — see the
    /// comment in <see cref="Client_Database_Loads_Both_Sets"/> for why that shape was backwards.
    /// <b>Do not add a count back, and do not "update counts here"</b> — there are none to update.</para>
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

            // This used to pin three literals — 188 / 76 / 264. That shape is backwards, and it
            // failed the only way it could: e0e942f0 added three cards, did not re-pin, and the
            // test shipped red for a day before anyone read it. A corpus total fires on the most
            // routine edit there is (adding a card) and is silent on the one that actually costs a
            // day (changing or removing an existing card), so it teaches you to bump the number
            // rather than read it.
            //
            // The real invariant hiding behind those numbers was "a card is declared in the set
            // file its setId names" — that is what the deleted comment about the CLOCKWRIGHT and
            // TEMPEST signature weapons being filed under Rekindling by mistake was really about.
            // It never moves when you add a card, and it catches the misfiling directly.
            //
            // Removals and identity changes are covered by tooling/scripts/check_card_id_drift.py,
            // which fails on a vanished or repointed id, passes silently on additions, and prints
            // WHAT moved. It runs as step 0 of rebake_card_data.py. In CI pass --base <merge-base>,
            // since comparing a committed tree against HEAD is vacuously clean.
            Assert.That(emberfall, Is.Not.Empty, "Emberfall set failed to load.");
            Assert.That(rekindling, Is.Not.Empty, "Rekindling set failed to load.");

            // CORE is data-tagged onto cards that ship in the Emberfall file (CLAUDE.md: CORE has
            // no bundle of its own beyond hero powers), so Emberfall.cards.json legitimately holds
            // both EMBERFALL and CORE rows. Rekindling holds only REKINDLING.
            Assert.That(emberfall.Select(card => card.SetId),
                Is.All.AnyOf(SetId.EMBERFALL, SetId.CORE),
                "A row in Emberfall.cards.json declares a setId that belongs in another set file.");
            Assert.That(rekindling.Select(card => card.SetId),
                Is.All.EqualTo(SetId.REKINDLING),
                "A row in Rekindling.cards.json declares a setId that belongs in another set file.");

            var combined = emberfall.Concat(rekindling).ToList();
            Assert.That(combined, Has.Count.GreaterThan(200), "Card corpus failed to load.");
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
        public void Token_Summon_Rules_Reference_The_Card_They_Name()
        {
            var all = SetFiles.SelectMany(LoadSet).ToList();

            // The SHA-256 identity pin that used to live here is gone, and the seven stacked
            // "Re-pinned <date>" comments it accumulated are the argument for removing it: every
            // one records a human hand-justifying a hex string, and not one of them records the
            // pin catching something its author did not already know. It moved on card ADDS, on
            // name edits, on artId corrections and on rules refactors — all routine work — so the
            // ritual response was to bump it. Which is how it came to sit stale through e0e942f0
            // and go unread until 2026-08-15.
            //
            // Removals and identity changes are now caught by tooling/scripts/check_card_id_drift.py
            // (step 0 of rebake_card_data.py). It fails ONLY on a vanished or repointed id, passes
            // silently on additions, and prints WHICH field moved from what to what — so the
            // question at the failure is "did I mean this?" instead of "what is the new hash?".
            // In CI pass --base <merge-base>; against HEAD a committed tree is vacuously clean.
            //
            // What stays here is the part a diff cannot express: specific invariants about
            // specific cards, which are legible when they break.

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
