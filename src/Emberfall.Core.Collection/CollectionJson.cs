using System;
using System.Collections.Generic;
using System.Text.Json;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>Raised when a collection file exists but cannot be read as one. Never thrown for a missing file.</summary>
    public sealed class CollectionFormatException : FormatException
    {
        public CollectionFormatException(string message) : base(message) { }
        public CollectionFormatException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The on-disk format for a player's characters and decks (ui/collection-deckbuilder.md §3).
    ///
    /// <para><b>Hand-written, like <c>CardJson</c>.</b> Reflection-based serializers are the obvious
    /// choice and the wrong one here: field order becomes implicit, IL2CPP strips the members it
    /// cannot see a call to, and an enum renumbering silently rewrites every saved character. Reading
    /// and writing each field by name costs a page and makes all three of those impossible.</para>
    ///
    /// <para><b>Strict on classes, tolerant on card ids.</b> An unparseable <c>CardClass</c> is fatal
    /// — the pairing *is* the class, so a character we cannot read is not a character we can guess at.
    /// A card id, by contrast, is just a string until the database resolves it, so a row naming a card
    /// that a balance patch deleted loads intact and surfaces as an L4 violation when the deck is
    /// opened. That is the §3 rule ("surface as invalid, never silently drop or auto-repair")
    /// implemented rather than restated.</para>
    ///
    /// <para>Separated from the file plumbing on purpose: a WebGL or PlayerPrefs-backed store that
    /// cannot touch <c>System.IO</c> reuses this class unchanged.</para>
    /// </summary>
    public static class CollectionJson
    {
        /// <summary>Bumped when the shape changes incompatibly; a reader refuses a version it predates.</summary>
        public const int SchemaVersion = 1;

        private const string CharactersProperty = "characters";
        private const string DecksProperty = "decks";

        // ---- characters ----------------------------------------------------------------

        public static string WriteCharacters(IEnumerable<Character> characters)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", SchemaVersion);
                w.WriteStartArray(CharactersProperty);
                foreach (var c in characters)
                {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("name", c.Name);
                    w.WriteString("primaryClass", c.PrimaryClass.ToString());
                    w.WriteString("secondaryClass", c.SecondaryClass.ToString());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        public static IReadOnlyList<Character> ReadCharacters(string json)
        {
            var result = new List<Character>();
            using var doc = ParseEnvelope(json, CharactersProperty, out var rows);
            int i = 0;
            foreach (var el in rows.EnumerateArray())
            {
                string id = Req(el, "id", CharactersProperty, i);
                string name = Str(el, "name") ?? id;
                var primary = ReqEnum<CardClass>(el, "primaryClass", CharactersProperty, i);
                var secondary = ReqEnum<CardClass>(el, "secondaryClass", CharactersProperty, i);
                // Deliberately NOT gated on IsValidPairing: a stored character is reported as it was
                // stored. Rejecting happens on the way in (JsonCharacterStore.Save), where the caller
                // can still fix it — not on the way out, where the only options are lie or crash.
                result.Add(new Character(id, name, primary, secondary));
                i++;
            }
            return result;
        }

        // ---- decks ---------------------------------------------------------------------

        public static string WriteDecks(IEnumerable<DeckList> decks)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", SchemaVersion);
                w.WriteStartArray(DecksProperty);
                foreach (var d in decks)
                {
                    w.WriteStartObject();
                    w.WriteString("id", d.Id);
                    w.WriteString("characterId", d.CharacterId);
                    w.WriteString("name", d.Name);
                    w.WriteStartArray("cards");
                    foreach (var e in d.Entries)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", e.CardId);
                        w.WriteNumber("count", e.Count);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        public static IReadOnlyList<DeckList> ReadDecks(string json)
        {
            var result = new List<DeckList>();
            using var doc = ParseEnvelope(json, DecksProperty, out var rows);
            int i = 0;
            foreach (var el in rows.EnumerateArray())
            {
                var deck = new DeckList
                {
                    Id = Req(el, "id", DecksProperty, i),
                    // VULN-001 (security/known-vulnerabilities, LATENT — trigger row "Client-supplied
                    // deck lists"). Today this id and the card ids below come off local disk and go
                    // nowhere but DeckValidator, which resolves through ICardDatabase.TryGet. The
                    // moment a DeckList travels from a client to the server at match-join, §2 fact 3
                    // of that entry ("the handshake carries no deck") stops holding and these strings
                    // become attacker-authored card identities. Read VULN-001 §4 before wiring that:
                    // resolve with TryGet and never Get, and validate against the owned/legal set,
                    // not merely against the database.
                    CharacterId = Str(el, "characterId") ?? "",
                    Name = Str(el, "name") ?? "New Deck",
                };

                if (el.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cards.EnumerateArray())
                    {
                        string cardId = Req(c, "id", DecksProperty, i);
                        int count = c.TryGetProperty("count", out var n) && n.ValueKind == JsonValueKind.Number
                            ? n.GetInt32()
                            : 0;
                        deck.Entries.Add(new DeckEntry(cardId, count));
                    }
                }

                result.Add(deck);
                i++;
            }
            return result;
        }

        // ---- shared --------------------------------------------------------------------

        private static JsonDocument ParseEnvelope(string json, string arrayProperty, out JsonElement rows)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new CollectionFormatException($"Collection file is not valid JSON ({arrayProperty}).", ex);
            }

            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw new CollectionFormatException($"Collection file root must be an object, was {root.ValueKind}.");
            }

            if (root.TryGetProperty("schemaVersion", out var ver) && ver.ValueKind == JsonValueKind.Number
                && ver.GetInt32() > SchemaVersion)
            {
                int found = ver.GetInt32();
                doc.Dispose();
                throw new CollectionFormatException(
                    $"Collection file is schemaVersion {found}; this build reads up to {SchemaVersion}. " +
                    "Refusing to read it — a newer build wrote this collection and downgrading would lose data.");
            }

            if (!root.TryGetProperty(arrayProperty, out rows) || rows.ValueKind != JsonValueKind.Array)
            {
                doc.Dispose();
                throw new CollectionFormatException($"Collection file has no '{arrayProperty}' array.");
            }

            return doc;
        }

        private static string? Str(JsonElement el, string name)
            => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static string Req(JsonElement el, string name, string section, int index)
            => Str(el, name) ?? throw new CollectionFormatException(
                $"{section}[{index}] is missing required string '{name}'.");

        private static T ReqEnum<T>(JsonElement el, string name, string section, int index) where T : struct
        {
            string raw = Req(el, name, section, index);
            // IsDefined(Type, string) is an exact NAME match — unlike Enum.TryParse, which happily
            // accepts "3" and hands back whichever member currently sits at 3.
            if (!Enum.IsDefined(typeof(T), raw))
                throw new CollectionFormatException(
                    $"{section}[{index}].{name} = '{raw}' is not a known {typeof(T).Name}.");
            return (T)Enum.Parse(typeof(T), raw, ignoreCase: false);
        }
    }
}
