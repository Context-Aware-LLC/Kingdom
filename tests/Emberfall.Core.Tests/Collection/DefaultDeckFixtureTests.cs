using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// EF-031 — a legal, <b>checked-in</b> default deck for every playable class, and the Unit-lane gate
    /// that keeps it legal. The fixtures under <c>Fixtures/DefaultDecks/</c> are the canonical starter a
    /// new character of each class opens on; this fixture also gives the balance harness (EF-032) a
    /// stable, reviewable deck per class instead of one that only ever existed in memory.
    ///
    /// <para><b>Why the gate is <see cref="CategoryAttribute">Category=Unit</see> and not Collection.</b>
    /// The DONE-WHEN asks for it, and it is the right lane: CI runs Category=Unit as a hard-blocking
    /// stage (ci.yml Stage 2) but does not run Collection, so putting the "every class can still field a
    /// legal 30" tripwire here means a content edit that drops a class below a buildable pool fails the
    /// merge gate — exactly when it should, rather than at a player's Create button.</para>
    ///
    /// <para><b>The fixture is generated, not hand-authored,</b> so it cannot drift from
    /// <see cref="DefaultDeckGenerator"/>: <see cref="Every_Class_Fixture_Is_Legal_And_Matches_The_Generator"/>
    /// re-derives each deck from the same seed and asserts byte-equality with the file on disk. To
    /// rebaseline after an intentional generator or pool change, run the explicit
    /// <see cref="Regenerate_Fixtures"/> and commit the result.</para>
    /// </summary>
    [TestFixture]
    public sealed class DefaultDeckFixtureTests
    {
        /// <summary>The four deck-legal classes (card-schema.md §3). NEUTRAL is never a hero class.</summary>
        private static IEnumerable<CardClass> PlayableClasses =>
            ((CardClass[])Enum.GetValues(typeof(CardClass))).Where(c => c != CardClass.NEUTRAL);

        /// <summary>
        /// The canonical character a class's starter fixture is built for. Emberfall has no mono-class
        /// pool — a thin class cannot reach 30 legal cards from its own rows plus NEUTRAL (EMBERWARD has
        /// 17 collectible copies) — so a "per class" deck is a dual-class character led by that class.
        /// The secondary is fixed as the first other playable class in enum order, and the seed is fixed
        /// per class, so the fixture is one deterministic deck, reproducible run to run.
        /// </summary>
        private static Character CanonicalCharacter(CardClass primary)
            => new Character(CharacterId(primary), $"{primary} Starter", primary, SecondaryFor(primary));

        private static CardClass SecondaryFor(CardClass primary)
            => PlayableClasses.First(c => c != primary);

        private static ulong Seed(CardClass primary) => 0xEF031_0000UL + (ulong)primary;
        private static string CharacterId(CardClass primary) => $"fixture-char-{primary}";
        private static string DeckId(CardClass primary) => $"fixture-deck-{primary}";

        private static string FixtureDir =>
            Path.Combine(RepoPaths.TestProjectDir, "Fixtures", "DefaultDecks");
        private static string FixturePath(CardClass primary) =>
            Path.Combine(FixtureDir, $"{primary}.deck.json");

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        // ── the CI-gated contract ────────────────────────────────────────────────────

        [Test]
        [Category("Unit")]
        public void Every_Class_Fixture_Is_Legal_And_Matches_The_Generator()
        {
            foreach (var primary in PlayableClasses)
            {
                var character = CanonicalCharacter(primary);
                Assert.That(File.Exists(FixturePath(primary)), Is.True,
                    $"Missing default-deck fixture for {primary}. Run {nameof(Regenerate_Fixtures)} and commit it.");

                var fixture = Load(primary);

                // The requirement itself: the checked-in deck is a legal 30 for a character of this class.
                Assert.That(fixture.Entries.Sum(e => e.Count), Is.EqualTo(DeckValidator.DeckSize), $"{primary} size");
                var deck = ToDeckList(fixture, character);
                var check = DeckValidator.Validate(deck, character, LiveCardSet.Db);
                Assert.That(check.IsValid, Is.True,
                    $"{primary} fixture failed L1-L5: " +
                    string.Join("; ", check.Violations.Select(v => $"{v.Rule}: {v.Message}")));

                // Drift guard: the file must equal a fresh deterministic generation. A generator or pool
                // change that shifts the deck fails here until the fixture is rebaselined on purpose.
                var fresh = Generate(primary, character);
                Assert.That(fixture.Entries.Select(e => (e.Id, e.Count)),
                    Is.EqualTo(fresh.Entries.Select(e => (e.CardId, e.Count))),
                    $"{primary} fixture is stale — regenerate and commit.");
            }
        }

        [Test]
        [Category("Unit")]
        public void A_Fixture_Exists_For_Every_Playable_Class_And_No_Others()
        {
            var onDisk = Directory.Exists(FixtureDir)
                ? Directory.GetFiles(FixtureDir, "*.deck.json").Select(Path.GetFileNameWithoutExtension)
                           .Select(n => n!.Replace(".deck", "")).ToHashSet()
                : new HashSet<string>();

            Assert.That(onDisk, Is.EquivalentTo(PlayableClasses.Select(c => c.ToString())),
                "the fixture set must be exactly the playable classes — no missing class, no orphan file.");
        }

        // ── rebaseline (explicit; never runs in a normal pass) ───────────────────────

        [Test, Explicit("Rewrites the checked-in default-deck fixtures; run by hand after an intended change.")]
        [Category("Fixture")]
        public void Regenerate_Fixtures()
        {
            Directory.CreateDirectory(FixtureDir);
            foreach (var primary in PlayableClasses)
            {
                var character = CanonicalCharacter(primary);
                var deck = Generate(primary, character);
                var fixture = new DeckFixture
                {
                    PrimaryClass = primary.ToString(),
                    SecondaryClass = SecondaryFor(primary).ToString(),
                    CharacterId = character.Id,
                    DeckId = deck.Id,
                    Seed = Seed(primary),
                    Entries = deck.Entries.Select(e => new DeckFixture.Entry { Id = e.CardId, Count = e.Count }).ToList(),
                };
                File.WriteAllText(FixturePath(primary), JsonSerializer.Serialize(fixture, Json));
                TestContext.Out.WriteLine($"wrote {FixturePath(primary)} ({fixture.Entries.Count} rows)");
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        private static DeckList Generate(CardClass primary, Character character)
            => DefaultDeckGenerator.Generate(
                character, LiveCardSet.Db, new DeterministicRandom(Seed(primary)), DeckId(primary));

        private static DeckFixture Load(CardClass primary)
            => JsonSerializer.Deserialize<DeckFixture>(File.ReadAllText(FixturePath(primary)), Json)!;

        private static DeckList ToDeckList(DeckFixture fixture, Character character)
        {
            var deck = new DeckList { Id = fixture.DeckId, CharacterId = character.Id, Name = $"{character.Name} Fixture" };
            foreach (var e in fixture.Entries) deck.Entries.Add(new DeckEntry(e.Id, e.Count));
            return deck;
        }

        /// <summary>
        /// The on-disk deck-file shape. A second copy lives in <c>BalanceCorpusLoader</c> (EF-030),
        /// which READS the same format for the hand-authored BalanceCorpus decks; the soak tool
        /// cannot reference this test project, and promoting the DTO into the Unity-consumed
        /// <c>Emberfall.Core.Collection</c> is not worth it for a JSON shape. Change one, change both.
        /// </summary>
        private sealed class DeckFixture
        {
            public string PrimaryClass { get; set; } = "";
            public string SecondaryClass { get; set; } = "";
            public string CharacterId { get; set; } = "";
            public string DeckId { get; set; } = "";
            public ulong Seed { get; set; }
            public List<Entry> Entries { get; set; } = new List<Entry>();

            public sealed class Entry
            {
                public int Id { get; set; }
                public int Count { get; set; }
            }
        }
    }
}
