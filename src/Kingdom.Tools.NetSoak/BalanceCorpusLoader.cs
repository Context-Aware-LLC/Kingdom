using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// EF-030 — loads the hand-authored balance decks under
    /// <c>tests/Kingdom.Core.Tests/Fixtures/BalanceCorpus/</c> and hands them to the soak as real
    /// decklists, so the balance grid can be ground over decks a designer WROTE rather than over the
    /// <see cref="DefaultDeckGenerator"/>'s output.
    ///
    /// <para><b>Why the corpus needs its own loader and not the matrix's class axis.</b>
    /// <c>ResolveSoakSetups</c> derives both decks from a class pair, which makes the deck a function
    /// of the axis label; here the deck IS the input and the class is just what its hero power reads
    /// off. Two corpus decks may legitimately share a primary class (two GRAVEKEEP archetypes), so
    /// the identity of a corpus deck is its FILE, never its class — which is why every tally the
    /// corpus mode keeps is label-keyed.</para>
    ///
    /// <para><b>Every file is validated on load and an invalid one throws.</b> A corpus deck that has
    /// fallen out of legality (a card retired, a copy limit tightened) must stop the run at the file
    /// that broke, not silently contribute a 27-card seat to a win-rate nobody can interpret
    /// afterwards. Validation is the same L1-L5 <see cref="DeckValidator.Validate"/> the deckbuilder
    /// enforces, against the same character the file declares.</para>
    /// </summary>
    public static class BalanceCorpusLoader
    {
        /// <summary>The repo-relative home of the corpus, and the fallback when nothing overrides it.</summary>
        public const string RepoRelativeDir = "tests/Kingdom.Core.Tests/Fixtures/BalanceCorpus";

        private const string DeckGlob = "*.deck.json";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Every <c>*.deck.json</c> in <paramref name="dir"/>, validated, with its card ids expanded
        /// to the flat pre-shuffle order <see cref="Kingdom.Core.MatchSetup"/> takes.
        ///
        /// <para>Ordered by filename, ordinal. The order is the grid axis, so it must not depend on
        /// the host's directory enumeration or its current culture — a matrix whose rows move between
        /// machines cannot be diffed against the last run.</para>
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">The corpus directory does not exist.</exception>
        /// <exception cref="FileNotFoundException">The directory holds no deck files.</exception>
        /// <exception cref="InvalidDataException">A deck file is malformed or fails L1-L5.</exception>
        public static IReadOnlyList<(string label, CardClass primary, CardClass secondary, IReadOnlyList<int> cardIds)>
            Load(string dir, ICardDatabase db)
        {
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException(
                    $"No balance corpus at '{dir}'. Expected {RepoRelativeDir} (NOR-319/NOR-320), "
                    + "or set KINGDOM_BALANCE_CORPUS to a directory of *.deck.json files.");

            string[] files = Directory.GetFiles(dir, DeckGlob)
                                      .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                                      .ToArray();
            if (files.Length == 0)
                throw new FileNotFoundException($"Balance corpus '{dir}' holds no {DeckGlob} files.");

            var loaded = new List<(string, CardClass, CardClass, IReadOnlyList<int>)>(files.Length);
            foreach (string file in files) loaded.Add(LoadOne(file, db));
            return loaded;
        }

        private static (string label, CardClass primary, CardClass secondary, IReadOnlyList<int> cardIds)
            LoadOne(string file, ICardDatabase db)
        {
            string label = Path.GetFileName(file);
            if (label.EndsWith(".deck.json", StringComparison.Ordinal))
                label = label.Substring(0, label.Length - ".deck.json".Length);

            DeckFixture fixture;
            try
            {
                fixture = JsonSerializer.Deserialize<DeckFixture>(File.ReadAllText(file), Json)
                          ?? throw new InvalidDataException("deserialized to null");
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException)
            {
                throw new InvalidDataException($"{label}: not a readable deck file — {ex.Message}", ex);
            }

            CardClass primary = ParseClass(label, nameof(fixture.PrimaryClass), fixture.PrimaryClass);
            CardClass secondary = ParseClass(label, nameof(fixture.SecondaryClass), fixture.SecondaryClass);

            var deck = new DeckList(primary, secondary)
            {
                Id = string.IsNullOrEmpty(fixture.DeckId) ? $"corpus-deck-{label}" : fixture.DeckId,
                Name = label,
            };
            foreach (DeckFixture.Entry e in fixture.Entries) deck.Entries.Add(new DeckEntry(e.Id, e.Count));

            DeckValidation check = DeckValidator.Validate(deck, db);
            if (!check.IsValid)
                throw new InvalidDataException(
                    $"{label}: illegal deck — "
                    + string.Join("; ", check.Violations.Select(v => $"{v.Rule}: {v.Message}")));

            // Flat pre-shuffle order: MatchSetup takes one entry per copy, and that order is part of
            // the deterministic input, so it follows the file's entry order rather than being sorted.
            var ids = new List<int>(DeckValidator.DeckSize);
            foreach (DeckFixture.Entry e in fixture.Entries)
                for (int c = 0; c < e.Count; c++) ids.Add(e.Id);

            return (label, primary, secondary, ids);
        }

        private static CardClass ParseClass(string label, string field, string raw)
        {
            if (!Enum.TryParse(raw, ignoreCase: true, out CardClass c))
                throw new InvalidDataException($"{label}: {field} '{raw}' is not a CardClass.");
            return c;
        }

        /// <summary>
        /// The on-disk deck-file shape. A second copy of this DTO lives as
        /// <c>DefaultDeckFixtureTests.DeckFixture</c>, which WRITES the DefaultDecks fixtures in the
        /// same format; it is private to that test and the test project is not referenced from here.
        /// Kept duplicated rather than promoted into <c>Kingdom.Core.Collection</c> on purpose: that
        /// assembly is netstandard2.1 and Unity-consumed, and a JSON DTO for a soak tool has no
        /// business crossing into it. Change one shape, change the other.
        /// </summary>
        private sealed class DeckFixture
        {
            public string PrimaryClass { get; set; } = "";
            public string SecondaryClass { get; set; } = "";
            /// <summary>Ignored since ADR-0009, and kept only so a corpus file written before it
            /// still deserializes. A fixture's pairing comes from PrimaryClass/SecondaryClass, which
            /// is where it always came from — the character this named was constructed from those two
            /// fields and discarded. Delete the property once no corpus file on disk carries it.</summary>
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
