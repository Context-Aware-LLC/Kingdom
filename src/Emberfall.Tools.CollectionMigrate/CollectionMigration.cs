using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Emberfall.Tools.CollectionMigrate
{
    /// <summary>What one deck's migration did, for the report the operator reads before applying.</summary>
    public enum DeckOutcome
    {
        /// <summary>The deck had no pairing and its character supplied one. The normal case.</summary>
        Inherited,

        /// <summary>The deck already carried a pairing, so nothing was done. Makes the run idempotent.</summary>
        AlreadyMigrated,

        /// <summary>
        /// The deck's <c>characterId</c> resolved to no character in the roster, so there is nothing
        /// to inherit from. <b>Dropped.</b> See <see cref="CollectionMigration"/> for why that is the
        /// only honest answer and why it is loud.
        /// </summary>
        Orphaned,
    }

    public sealed class DeckMigrationRow
    {
        public string DeckId { get; }
        public string Name { get; }
        public int CardCount { get; }
        public DeckOutcome Outcome { get; }

        /// <summary>The pairing the deck ends up with, or empty for a dropped one.</summary>
        public string Pairing { get; }

        public DeckMigrationRow(string deckId, string name, int cardCount, DeckOutcome outcome, string pairing)
        {
            DeckId = deckId;
            Name = name;
            CardCount = cardCount;
            Outcome = outcome;
            Pairing = pairing;
        }
    }

    public sealed class MigrationPlan
    {
        public IReadOnlyList<DeckMigrationRow> Decks { get; }

        /// <summary>The migrated <c>decks.json</c> text, ready to write. Already excludes the orphans.</summary>
        public string MigratedDecksJson { get; }

        public MigrationPlan(IReadOnlyList<DeckMigrationRow> decks, string migratedDecksJson)
        {
            Decks = decks;
            MigratedDecksJson = migratedDecksJson;
        }

        public int Count(DeckOutcome outcome)
        {
            int n = 0;
            foreach (var d in Decks) if (d.Outcome == outcome) n++;
            return n;
        }

        public bool ChangesAnything => Count(DeckOutcome.Inherited) > 0 || Count(DeckOutcome.Orphaned) > 0;
    }

    /// <summary>
    /// The one-shot conversion ADR-0009 ruling 3 calls for: <b>each deck inherits its character's
    /// pairing, and the characters are then dropped.</b>
    ///
    /// <para><b>Why this is a TOOL and not a shim in the shipped reader.</b> The greenfield ruling
    /// (2026-08-07) is "conform the content, do not add back-compat", and ruling 3 is its one
    /// deliberate exception — a ONE-SHOT conversion, not a compatibility layer.
    /// <c>CollectionJson.ReadDecks</c> therefore has no fallback for an absent pairing and throws
    /// loudly instead: a missed migration must be a read error, never a roster of decks that are
    /// quietly illegal because they defaulted to NEUTRAL/NEUTRAL. That loudness is only safe if
    /// something exists to run, which is this.</para>
    ///
    /// <para><b>An orphan is DROPPED, and that is the honest answer rather than the convenient
    /// one.</b> A deck whose <c>characterId</c> resolves to nobody has no pairing to inherit and no
    /// way to acquire one: the pairing decides the whole legal pool, so inventing one hands the
    /// player a deck they never built, and NEUTRAL/NEUTRAL makes every card in it off-class. It was
    /// already unplayable before this ran — an orphan failed L5 by construction — so nothing that
    /// worked stops working. It is reported per deck by name and card count, and the file it came
    /// from is backed up before anything is written, so the decision is reversible by hand.</para>
    ///
    /// <para><b>Idempotent.</b> A deck that already carries a pairing is passed through untouched, so
    /// a second run is a no-op and a half-finished run can simply be re-run.</para>
    ///
    /// <para>Pure: text in, text out, no I/O and no clock. The file and database halves are
    /// <c>Program</c>'s, so the part that decides what happens to a player's only copy of their
    /// decks is the part that can be tested without one.</para>
    /// </summary>
    public static class CollectionMigration
    {
        private const int SchemaVersion = 1;

        /// <param name="charactersJson">The legacy <c>characters.json</c>, or null/empty when the
        /// file is absent — in which case every deck without a pairing is an orphan, which is
        /// correct: there is nothing to inherit from.</param>
        /// <param name="decksJson">The <c>decks.json</c> to convert.</param>
        /// <exception cref="JsonException">Either file is not JSON. The caller must not write
        /// anything when this throws — an unreadable input is exactly the case where a partial
        /// rewrite destroys the only copy.</exception>
        public static MigrationPlan Plan(string? charactersJson, string decksJson)
        {
            Dictionary<string, (string Primary, string Secondary)> pairings = ReadPairings(charactersJson);

            var rows = new List<DeckMigrationRow>();
            var buffer = new System.IO.MemoryStream();

            using (var doc = JsonDocument.Parse(decksJson))
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", SchemaVersion);
                w.WriteStartArray("decks");

                if (doc.RootElement.TryGetProperty("decks", out JsonElement decks)
                    && decks.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement deck in decks.EnumerateArray())
                    {
                        string id = Str(deck, "id");
                        string name = Str(deck, "name");
                        int cards = CountCards(deck);

                        bool hasPairing = deck.TryGetProperty("primaryClass", out _)
                                          && deck.TryGetProperty("secondaryClass", out _);
                        if (hasPairing)
                        {
                            rows.Add(new DeckMigrationRow(id, name, cards, DeckOutcome.AlreadyMigrated,
                                Str(deck, "primaryClass") + "/" + Str(deck, "secondaryClass")));
                            Copy(w, deck, injectPrimary: null, injectSecondary: null);
                            continue;
                        }

                        string characterId = Str(deck, "characterId");
                        if (characterId.Length == 0 || !pairings.TryGetValue(characterId, out var pairing))
                        {
                            rows.Add(new DeckMigrationRow(id, name, cards, DeckOutcome.Orphaned, ""));
                            continue;   // dropped: written to no output array
                        }

                        rows.Add(new DeckMigrationRow(id, name, cards, DeckOutcome.Inherited,
                            pairing.Primary + "/" + pairing.Secondary));
                        Copy(w, deck, pairing.Primary, pairing.Secondary);
                    }
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            return new MigrationPlan(rows, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }

        /// <summary>
        /// characterId → its two class NAMES, taken verbatim off the legacy file.
        ///
        /// <para><b>Verbatim, and never parsed into <c>CardClass</c>.</b> This tool must be able to
        /// migrate a roster naming a class this build has since renamed or removed — refusing to
        /// convert it would strand the whole file, and the deck reader is what will reject the name
        /// afterwards, at which point the player has a legible error and a backup rather than an
        /// unreadable roster. Copying the string through is also what makes the conversion lossless
        /// for names that ARE still valid.</para>
        /// </summary>
        private static Dictionary<string, (string, string)> ReadPairings(string? charactersJson)
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(charactersJson)) return map;

            using var doc = JsonDocument.Parse(charactersJson!);
            if (!doc.RootElement.TryGetProperty("characters", out JsonElement characters)
                || characters.ValueKind != JsonValueKind.Array)
                return map;

            foreach (JsonElement c in characters.EnumerateArray())
            {
                string id = Str(c, "id");
                string primary = Str(c, "primaryClass");
                string secondary = Str(c, "secondaryClass");
                if (id.Length == 0 || primary.Length == 0 || secondary.Length == 0) continue;
                map[id] = (primary, secondary);
            }
            return map;
        }

        /// <summary>
        /// Copy one deck object through, injecting the pairing and dropping <c>characterId</c>.
        ///
        /// <para><b>Every other member is copied verbatim, including ones this tool has never heard
        /// of.</b> The alternative — deserialize into <c>DeckList</c> and re-serialize — would
        /// silently drop any field added between the file being written and this being run, and the
        /// whole point of a one-shot conversion is that there is no second chance.</para>
        /// </summary>
        private static void Copy(Utf8JsonWriter w, JsonElement deck, string? injectPrimary, string? injectSecondary)
        {
            w.WriteStartObject();

            foreach (JsonProperty p in deck.EnumerateObject())
            {
                // The field the pairing replaces. Dropping it is the point of the conversion — the
                // column it pointed at is gone, and leaving it would keep an id nothing resolves.
                if (p.NameEquals("characterId")) continue;

                p.WriteTo(w);

                // Written where the pairing sits in CollectionJson.WriteDecks, so a migrated file and
                // a freshly written one are byte-comparable in field order.
                if (p.NameEquals("id") && injectPrimary != null)
                {
                    w.WriteString("primaryClass", injectPrimary);
                    w.WriteString("secondaryClass", injectSecondary);
                }
            }

            w.WriteEndObject();
        }

        private static string Str(JsonElement el, string name)
            => el.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "")
                : "";

        private static int CountCards(JsonElement deck)
        {
            if (!deck.TryGetProperty("cards", out JsonElement cards) || cards.ValueKind != JsonValueKind.Array)
                return 0;
            int n = 0;
            foreach (JsonElement c in cards.EnumerateArray())
                if (c.TryGetProperty("count", out JsonElement count) && count.TryGetInt32(out int v)) n += v;
            return n;
        }
    }
}
