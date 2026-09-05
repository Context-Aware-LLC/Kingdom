using System.Linq;
using NUnit.Framework;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Tools.CollectionMigrate;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// ADR-0009's one-shot conversion: each deck inherits its character's pairing.
    ///
    /// <para><b>Why a migration TOOL is tested at all.</b> It is one-shot code that will run against
    /// a player's only copy of their decks, exactly once, on a device nobody is watching — the shape
    /// of thing whose bugs are unrecoverable rather than annoying. And there is no safety net behind
    /// it: <see cref="CollectionJson.ReadDecks"/> deliberately has no fallback for a missing pairing,
    /// so a deck this converts wrongly is a deck that either fails to load or loads with the wrong
    /// legal pool.</para>
    ///
    /// <para>The last two tests are the load-bearing ones — they assert against the SHIPPED reader
    /// rather than against the tool's own output, which is the only way to prove the conversion
    /// produces something the game can actually open.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CollectionMigrationTests
    {
        private const string Characters = """
            {
              "schemaVersion": 1,
              "characters": [
                { "id": "chr-1", "name": "Rotbloom", "primaryClass": "GRAVEKEEP", "secondaryClass": "VERDANT" },
                { "id": "chr-2", "name": "Ashtide", "primaryClass": "EMBERWARD", "secondaryClass": "TIDECALLER" }
              ]
            }
            """;

        private static string LegacyDecks(string body) => $$"""
            { "schemaVersion": 1, "decks": [ {{body}} ] }
            """;

        // ---- inheritance ----------------------------------------------------------------

        [Test]
        public void A_Deck_Takes_Its_Characters_Pairing()
        {
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "Rotvine",
                  "cards": [ { "id": 1001, "count": 2 } ] }
                """));

            Assert.That(plan.Decks.Single().Outcome, Is.EqualTo(DeckOutcome.Inherited));

            DeckList migrated = CollectionJson.ReadDecks(plan.MigratedDecksJson).Single();
            Assert.That(migrated.PrimaryClass, Is.EqualTo(CardClass.GRAVEKEEP));
            Assert.That(migrated.SecondaryClass, Is.EqualTo(CardClass.VERDANT));
        }

        [Test]
        public void Two_Decks_On_Two_Characters_Each_Take_Their_Own()
        {
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "A", "cards": [] },
                { "id": "d2", "characterId": "chr-2", "name": "B", "cards": [] }
                """));

            var migrated = CollectionJson.ReadDecks(plan.MigratedDecksJson);
            Assert.That(migrated[0].PrimaryClass, Is.EqualTo(CardClass.GRAVEKEEP));
            Assert.That(migrated[1].PrimaryClass, Is.EqualTo(CardClass.EMBERWARD));
        }

        [Test]
        public void Every_Other_Field_Survives_Including_Ones_The_Tool_Does_Not_Know()
        {
            // The conversion copies the JSON through rather than round-tripping it via DeckList, so a
            // field added between a file being written and this being run is not silently dropped.
            // There is no second chance at a one-shot conversion.
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "Rotvine",
                  "cardBackId": "gravekeep_allover", "showcaseCardId": 1001,
                  "aFieldFromALaterBuild": "kept",
                  "cards": [ { "id": 1001, "count": 2 } ] }
                """));

            Assert.That(plan.MigratedDecksJson, Does.Contain("aFieldFromALaterBuild"));

            DeckList migrated = CollectionJson.ReadDecks(plan.MigratedDecksJson).Single();
            Assert.That(migrated.Name, Is.EqualTo("Rotvine"));
            Assert.That(migrated.CardBackId, Is.EqualTo("gravekeep_allover"));
            Assert.That(migrated.ShowcaseCardId, Is.EqualTo(1001));
            Assert.That(migrated.CountOf(1001), Is.EqualTo(2));
        }

        [Test]
        public void The_Character_Pointer_Is_Dropped()
        {
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "A", "cards": [] }
                """));

            Assert.That(plan.MigratedDecksJson, Does.Not.Contain("characterId"),
                "the column it pointed at is gone; leaving the id keeps a pointer nothing resolves");
        }

        // ---- the orphan, which is the decision worth pinning ----------------------------

        [Test]
        public void A_Deck_Whose_Character_Is_Gone_Is_Dropped_And_Reported()
        {
            // There is nothing to inherit and no honest way to invent it: the pairing decides the
            // whole legal pool, so a guess hands the player a deck they never built. It was already
            // unplayable — an orphan failed L5 by construction — so nothing that worked stops working.
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d-orphan", "characterId": "chr-DELETED", "name": "Stranded",
                  "cards": [ { "id": 1001, "count": 3 } ] }
                """));

            DeckMigrationRow row = plan.Decks.Single();
            Assert.That(row.Outcome, Is.EqualTo(DeckOutcome.Orphaned));
            Assert.That(row.Name, Is.EqualTo("Stranded"), "reported by name, so the operator can see what goes");
            Assert.That(row.CardCount, Is.EqualTo(3), "and by size");
            Assert.That(CollectionJson.ReadDecks(plan.MigratedDecksJson), Is.Empty);
        }

        [Test]
        public void An_Orphan_Does_Not_Take_Its_Siblings_With_It()
        {
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "Fine", "cards": [] },
                { "id": "d-orphan", "characterId": "chr-DELETED", "name": "Stranded", "cards": [] },
                { "id": "d2", "characterId": "chr-2", "name": "Also fine", "cards": [] }
                """));

            Assert.That(CollectionJson.ReadDecks(plan.MigratedDecksJson).Select(d => d.Id),
                Is.EqualTo(new[] { "d1", "d2" }));
            Assert.That(plan.Count(DeckOutcome.Orphaned), Is.EqualTo(1));
        }

        [Test]
        public void With_No_Roster_File_At_All_Every_Unmigrated_Deck_Is_An_Orphan()
        {
            // The device that deleted its characters.json but not its decks.json. There is genuinely
            // nothing to inherit from, and saying so is better than defaulting.
            MigrationPlan plan = CollectionMigration.Plan(null, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "A", "cards": [] }
                """));

            Assert.That(plan.Decks.Single().Outcome, Is.EqualTo(DeckOutcome.Orphaned));
        }

        // ---- idempotence ----------------------------------------------------------------

        [Test]
        public void A_Deck_That_Already_Has_A_Pairing_Is_Untouched()
        {
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "primaryClass": "NIGHTVEIL", "secondaryClass": "TEMPEST",
                  "name": "Already", "cards": [] }
                """));

            Assert.That(plan.Decks.Single().Outcome, Is.EqualTo(DeckOutcome.AlreadyMigrated));
            Assert.That(plan.ChangesAnything, Is.False);

            DeckList migrated = CollectionJson.ReadDecks(plan.MigratedDecksJson).Single();
            Assert.That(migrated.PrimaryClass, Is.EqualTo(CardClass.NIGHTVEIL),
                "a second run must not re-inherit over a pairing that is already correct");
            Assert.That(migrated.SecondaryClass, Is.EqualTo(CardClass.TEMPEST));
        }

        [Test]
        public void Running_It_Twice_Is_The_Same_As_Running_It_Once()
        {
            string once = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "A", "cards": [ { "id": 1001, "count": 2 } ] }
                """)).MigratedDecksJson;

            // Second pass with the roster ALREADY DELETED, which is the real state after --apply.
            string twice = CollectionMigration.Plan(null, once).MigratedDecksJson;

            Assert.That(twice, Is.EqualTo(once),
                "a half-finished run has to be safe to simply re-run");
        }

        // ---- what the game actually has to be able to open ------------------------------

        [Test]
        public void The_Output_Is_Byte_Identical_To_What_The_Shipped_Writer_Produces()
        {
            // The strongest statement available: convert, read with the shipped reader, write with
            // the shipped writer, and compare. Anything the tool got wrong about field order,
            // spelling or number formatting shows up here rather than on a device.
            MigrationPlan plan = CollectionMigration.Plan(Characters, LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "Rotvine",
                  "cardBackId": "gravekeep_allover", "showcaseCardId": 1001,
                  "cards": [ { "id": 1001, "count": 2 } ] }
                """));

            string reWritten = CollectionJson.WriteDecks(CollectionJson.ReadDecks(plan.MigratedDecksJson));

            Assert.That(plan.MigratedDecksJson.Replace("\r\n", "\n"),
                        Is.EqualTo(reWritten.Replace("\r\n", "\n")));
        }

        [Test]
        public void An_Unmigrated_File_Is_Exactly_What_The_Shipped_Reader_Refuses()
        {
            // The precondition this whole tool exists for, asserted rather than assumed: if the
            // reader ever gained a fallback, the migration would stop being necessary AND would stop
            // being safe, because a deck could then load with a pairing nobody chose.
            string legacy = LegacyDecks("""
                { "id": "d1", "characterId": "chr-1", "name": "A", "cards": [] }
                """);

            Assert.That(() => CollectionJson.ReadDecks(legacy),
                Throws.TypeOf<CollectionFormatException>(),
                "ReadDecks must REQUIRE the pairing — a silent default is how every deck on a " +
                "device becomes quietly illegal");

            Assert.That(() => CollectionJson.ReadDecks(
                CollectionMigration.Plan(Characters, legacy).MigratedDecksJson), Throws.Nothing);
        }
    }
}
