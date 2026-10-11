using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.CardData
{
    /// <summary>
    /// <b>A numeric card id, once shipped, never moves.</b> This is the gate on the one thing in the
    /// card model that cannot be repaired after the fact.
    ///
    /// <para><b>Why an id is different from everything else on a row.</b> A card id is a foreign key
    /// into data no test and no tool can reach: <c>DeckList.Entries</c> persists it to a player's
    /// <c>decks.json</c> under <c>AppData/LocalLow/&lt;company&gt;/&lt;product&gt;/&lt;accountGuid&gt;/</c>,
    /// and the entitlement ledger persists it again as an owned-card row. A repo-wide search-replace
    /// cannot reach either. Move an id and every saved deck holding it keeps an entry that resolves
    /// to nothing — and the deckbuilder HIDES an unresolvable row rather than showing it
    /// (<c>CollectionController.BindDeckRow</c> sets <c>DisplayStyle.None</c>), so the deck reads
    /// 30/30 with 29 cards drawn and the missing one cannot be counted, clicked or removed. That
    /// happened across seven saved decks on 2026-08-15 and again, unnoticed for six days, when
    /// <c>tide_glacierborn_leviathan</c> was renamed on 2026-08-09. The numeric id exists to end
    /// that class of bug; this fixture is what makes the promise checkable.</para>
    ///
    /// <para><b>What is deliberately NOT pinned: the handle.</b> Sam's ruling of 2026-08-15 is that
    /// nothing resolves against <see cref="CardDefinition.Handle"/>, and the test of the model is
    /// that you can change a handle and have nothing break. A fixture that failed on a rename would
    /// BE something breaking, and would quietly convert the handle back into a key. So a rename
    /// passes here in silence, exactly as it should. What is pinned is the pair that a card cannot
    /// change without becoming a different card — its <see cref="CardType"/> and its
    /// <see cref="SetId"/> — which is what catches an id being reissued to something else.</para>
    ///
    /// <para><b>Additions are free; that is the whole design.</b> The instrument this replaces was a
    /// pinned corpus total (<c>Assert.That(all.Count, Is.EqualTo(261))</c>), which fired on the most
    /// routine edit there is and stayed silent on the one that costs a day. Adding a card touches
    /// nothing below. Removing or renumbering one fails, and prints what moved.</para>
    ///
    /// <para><b>To rebaseline after a deliberate change</b> — you withdrew a card, or you are adding
    /// today's new rows to the registry — run the explicit <see cref="Regenerate_Registry"/> and
    /// commit the result. Withdrawing a card is a product decision, not a test fix: a withdrawn row
    /// is supposed to KEEP its id and stay resolvable rather than free the number for reuse.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class CardIdPermanenceTests
    {
        private sealed class Row
        {
            public int Id { get; set; }
            public string Handle { get; set; } = "";
            public string Type { get; set; } = "";
            public string SetId { get; set; } = "";
        }

        private static string RegistryPath =>
            Path.Combine(RepoPaths.TestProjectDir, "Fixtures", "CardIds", "shipped-card-ids.json");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private static IReadOnlyList<Row> Registry()
        {
            Assert.That(File.Exists(RegistryPath), Is.True,
                $"Card id registry missing at {RegistryPath}. Run {nameof(Regenerate_Registry)} and commit it.");
            return JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(RegistryPath), Json)!;
        }

        private static IReadOnlyList<CardDefinition> Shipped() => ShippedCards.Cards;

        // ── the permanence promise ──────────────────────────────────────────────────

        [Test]
        public void No_Registered_Id_Has_Disappeared()
        {
            var live = Shipped().Select(c => c.Id).ToHashSet();
            var gone = Registry().Where(r => !live.Contains(r.Id)).ToList();

            Assert.That(gone, Is.Empty, "These ids shipped and are no longer resolvable — every saved "
                + "deck and ownership row holding one now points at nothing, and the deckbuilder hides "
                + "it rather than showing it:\n  "
                + string.Join("\n  ", gone.Select(r => $"{r.Id}  ({r.Handle}, {r.Type})")));
        }

        [Test]
        public void No_Registered_Id_Now_Names_A_Different_Kind_Of_Card()
        {
            var live = Shipped().ToDictionary(c => c.Id);
            var moved = new List<string>();
            foreach (Row r in Registry())
            {
                if (!live.TryGetValue(r.Id, out CardDefinition? def) || def == null) continue;   // covered above
                if (def.Type.ToString() != r.Type || def.SetId.ToString() != r.SetId)
                    moved.Add($"{r.Id}: was {r.Type}/{r.SetId} ({r.Handle}), is now "
                        + $"{def.Type}/{def.SetId} ({def.Handle})");
            }

            Assert.That(moved, Is.Empty, "An id was reissued to a different card. The number is "
                + "permanent and is never reused — a withdrawn card keeps its row:\n  "
                + string.Join("\n  ", moved));
        }

        // ── the invariants that make an id usable as a key at all ───────────────────

        [Test]
        public void Every_Id_Is_Unique_Across_Every_Set()
        {
            var dupes = Shipped().GroupBy(c => c.Id).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Handle))}").ToList();

            Assert.That(dupes, Is.Empty, "Two rows claim the same id, so one of them is unreachable "
                + "through the database:\n  " + string.Join("\n  ", dupes));
        }

        [Test]
        public void No_Id_Is_Zero_Or_Negative()
        {
            // default(int) is 0, so a card id of 0 is indistinguishable from an unset field at every
            // boundary it crosses — the wire, the save file, the ledger.
            var bad = Shipped().Where(c => c.Id <= 0).Select(c => c.Handle).ToList();
            Assert.That(bad, Is.Empty, "Ids start at 1: " + string.Join(", ", bad));
        }

        [Test]
        public void Every_Id_Sits_Inside_Its_Type_Band()
        {
            // Banded so a later set appends inside its own range and can never collide with the
            // next one. CardValidator rule C1B is the authority; this asserts the shipped corpus
            // actually satisfies it, which validation only does when something runs it.
            var outside = new List<string>();
            foreach (CardDefinition c in Shipped())
            {
                bool ok;
                string band;
                switch (c.Type)
                {
                    case CardType.HERO_POWER: ok = c.Id >= 1 && c.Id <= 50; band = "1-50"; break;
                    case CardType.WEAPON: ok = c.Id >= 51 && c.Id <= 150; band = "51-150"; break;
                    case CardType.SPELL: ok = c.Id >= 151 && c.Id <= 1000; band = "151-1000"; break;
                    default: ok = c.Id >= 1001; band = "1001 and up"; break;
                }
                if (!ok) outside.Add($"{c.Id} ({c.Handle}) is {c.Type}, whose band is {band}");
            }

            Assert.That(outside, Is.Empty, "Ids are assigned by tooling/scripts/assign_numeric_card_ids.py "
                + "and are permanent:\n  " + string.Join("\n  ", outside));
        }

        [Test]
        public void The_Registry_Covers_Every_Row_That_Shipped_Before_Today()
        {
            // A row that exists but is unregistered is not a failure — it is a card added since the
            // last rebaseline, and additions are free. Say how many so the number is read rather
            // than assumed, and so a rebaseline that silently dropped 200 rows is visible.
            var registered = Registry().Select(r => r.Id).ToHashSet();
            var added = Shipped().Where(c => !registered.Contains(c.Id)).ToList();
            TestContext.Out.WriteLine(
                $"registry={registered.Count}  shipped={Shipped().Count}  unregistered(new)={added.Count}");
            foreach (CardDefinition c in added.OrderBy(c => c.Id))
                TestContext.Out.WriteLine($"  + {c.Id}  {c.Handle}  ({c.Type})");

            Assert.That(registered, Is.Not.Empty, "The registry is empty, so nothing above can fail.");
        }

        // ── rebaseline (explicit; never runs in a normal pass) ──────────────────────

        [Test, Explicit("Rewrites the checked-in card id registry; run by hand after an intended change.")]
        [Category("Fixture")]
        public void Regenerate_Registry()
        {
            var rows = Shipped()
                .OrderBy(c => c.Id)
                .Select(c => new Row
                {
                    Id = c.Id,
                    Handle = c.Handle,
                    Type = c.Type.ToString(),
                    SetId = c.SetId.ToString(),
                })
                .ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
            File.WriteAllText(RegistryPath, JsonSerializer.Serialize(rows, Json) + Environment.NewLine);
            TestContext.Out.WriteLine($"wrote {rows.Count} rows to {RegistryPath}");
        }
    }
}
