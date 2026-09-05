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
    /// The on-disk format for a player's profile, decks and owned cards (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3).
    /// One class for all of them so the <c>schemaVersion</c> ceiling has one definition and one guard,
    /// however many files the scope directory grows.
    ///
    /// <para><b>Hand-written, like <c>CardJson</c>.</b> Reflection-based serializers are the obvious
    /// choice and the wrong one here: field order becomes implicit, IL2CPP strips the members it
    /// cannot see a call to, and an enum renumbering silently rewrites every saved deck. Reading
    /// and writing each field by name costs a page and makes all three of those impossible.</para>
    ///
    /// <para><b>Strict on classes, tolerant on card ids.</b> An unparseable <c>CardClass</c> is fatal
    /// — the pairing *is* the class, so a deck we cannot read is not a deck we can guess at.
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

        private const string DecksProperty = "decks";
        private const string ProfileProperty = "profile";
        private const string AccountsProperty = "accounts";
        private const string OwnedCardsProperty = "ownedCards";

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
                    w.WriteString("primaryClass", d.PrimaryClass.ToString());
                    w.WriteString("secondaryClass", d.SecondaryClass.ToString());
                    w.WriteString("name", d.Name);
                    w.WriteString("cardBackId", d.CardBackId);
                    w.WriteNumber("showcaseCardId", d.ShowcaseCardId);
                    w.WriteStartArray("cards");
                    foreach (var e in d.Entries)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("id", e.CardId);
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
                // REQUIRED, not defaulted (ADR-0009). A deck's pairing decides its whole legal pool,
                // so there is no safe reading of an absent one: NEUTRAL/NEUTRAL would silently make
                // every card in the deck off-class, and guessing a pairing would hand the player a
                // deck they never built. A file written before 2026-09-04 carries `characterId`
                // instead and is converted by the CollectionMigrate TOOL
                // (src/Emberfall.Tools.CollectionMigrate) BEFORE it reaches here — this reader
                // deliberately has no fallback, so a missed migration is a loud read error rather
                // than a roster of decks that are quietly illegal.
                //
                // CollectionMigrationTests pins BOTH halves of that: that this throws on a legacy
                // file, and that the tool's output loads. Adding a fallback here breaks the first.
                var primary = ReqEnum<CardClass>(el, "primaryClass", DecksProperty, i);
                var secondary = ReqEnum<CardClass>(el, "secondaryClass", DecksProperty, i);

                // Deliberately NOT gated on IsValidPairing, the same call ReadCharacters made: a
                // stored deck is reported as it was stored. Rejecting happens on the way in, where
                // the caller can still fix it — not on the way out, where the only options are lie
                // or crash. DeckValidator L5 is what refuses to queue it.
                var deck = new DeckList(primary, secondary)
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
                    Name = Str(el, "name") ?? "New Deck",
                    // Optional and additive, the same shape as a character's defaultDeckId: a deck
                    // written before card backs existed reads back with none, which is the same state
                    // as "the player never chose one" and renders identically. Not checked against a
                    // catalog — see DeckList.CardBackId for why an unknown back is not a read error.
                    CardBackId = Str(el, "cardBackId") ?? "",
                    ShowcaseCardId = OptCardId(el, "showcaseCardId"),
                };

                if (el.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cards.EnumerateArray())
                    {
                        int cardId = ReqCardId(c, "id", DecksProperty, i);
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

        // ---- profile -------------------------------------------------------------------

        /// <summary>
        /// The player behind the roster. One object, not an array — there is exactly one player per
        /// identity scope, and a single-element array would only invite a second.
        /// </summary>
        public static string WriteProfile(PlayerProfile profile)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", SchemaVersion);
                w.WriteStartObject(ProfileProperty);
                w.WriteString("displayName", profile.DisplayName);
                w.WriteString("lastPlayedDeckId", profile.LastPlayedDeckId);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// Read a profile back. A name that no longer passes <see cref="PlayerName"/> — because the
        /// rules tightened after it was stored, or because the file was hand-edited — loads intact and
        /// is reported as stored. Same split as an invalid class pairing: refuse on the way in, where
        /// the caller can still fix it; never on the way out, where the only options are lie or crash.
        /// </summary>
        public static PlayerProfile ReadProfile(string json)
        {
            using var doc = ParseRoot(json, ProfileProperty, out var root);
            if (!root.TryGetProperty(ProfileProperty, out var el) || el.ValueKind != JsonValueKind.Object)
                throw new CollectionFormatException($"Collection file has no '{ProfileProperty}' object.");

            // Absent is the same as never-named, which is a legitimate state (IProfileStore) — so a
            // missing displayName is empty rather than fatal, unlike a deck's required pairing. Same
            // treatment for the last-played pointer: absent means "never shipped a deck yet", not corrupt.
            //
            // A `lastPlayedCharacterId` written before 2026-09-04 is simply not read (ADR-0009). It is
            // the one legacy field with nothing to migrate — the deck id beside it already names what
            // was played — so it is dropped on the next profile write rather than converted.
            return new PlayerProfile(
                Str(el, "displayName") ?? "",
                Str(el, "lastPlayedDeckId") ?? "");
        }

        // ---- accounts ------------------------------------------------------------------

        /// <summary>
        /// The device's account registry (<see cref="LocalAccount"/>). Carries no active-account
        /// pointer by design — that is machine state and lives in <c>PlayerPrefs</c>; see
        /// <see cref="ILocalAccountStore"/>.
        /// </summary>
        public static string WriteAccounts(IEnumerable<LocalAccount> accounts)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", SchemaVersion);
                w.WriteStartArray(AccountsProperty);
                foreach (var a in accounts)
                {
                    w.WriteStartObject();
                    w.WriteString("id", a.Id);
                    w.WriteString("wireToken", a.WireToken);
                    // Omitted entirely when unclaimed, rather than written empty: a device that has
                    // never signed in then produces byte-identical bytes to the registry it had
                    // before this field existed, so the field costs nothing until it is used.
                    if (a.IsClaimed) w.WriteString("authUserId", a.AuthUserId);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// Read the registry back. <c>id</c> and <c>wireToken</c> are <b>required</b> — unlike a
        /// character's <c>defaultDeckId</c>, neither has a meaningful absent state: an account with no
        /// id names no folder, and one with no wire token cannot join a match. Defaulting a missing
        /// wireToken to the id would silently hand a minted account the grandfathered account's shape,
        /// and the symptom would appear much later, on the wire.
        ///
        /// <para><c>authUserId</c> is the exception and is optional, in the <c>defaultDeckId</c> shape:
        /// absent means unclaimed, which is every registry written before it existed and every device
        /// that has never signed in. See <see cref="LocalAccount.AuthUserId"/>.</para>
        /// </summary>
        public static IReadOnlyList<LocalAccount> ReadAccounts(string json)
        {
            var result = new List<LocalAccount>();
            using var doc = ParseEnvelope(json, AccountsProperty, out var rows);
            int i = 0;
            foreach (var el in rows.EnumerateArray())
            {
                string id = Req(el, "id", AccountsProperty, i);
                string wireToken = Req(el, "wireToken", AccountsProperty, i);
                string authUserId = Str(el, "authUserId") ?? "";
                result.Add(new LocalAccount(id, wireToken, authUserId));
                i++;
            }
            return result;
        }

        // ---- owned cards ---------------------------------------------------------------

        /// <summary>
        /// What this account owns, per card id — rows sorted by card id
        /// (<see cref="OwnedCardCounts.Entries"/>), so an unchanged collection writes identical bytes.
        ///
        /// <para>An array of {id, copies} rather than a JSON object keyed by card id: an object would
        /// make every card id a property name, and a property name is exactly where a hand-edited or
        /// hostile file gets to collide with <c>schemaVersion</c>. The array keeps card ids as values,
        /// where the reader already treats them as untrusted strings.</para>
        /// </summary>
        public static string WriteOwnedCards(OwnedCardCounts owned)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", SchemaVersion);
                w.WriteStartArray(OwnedCardsProperty);
                foreach (var row in owned.Entries())
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", row.Key);
                    w.WriteNumber("copies", row.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        /// <summary>
        /// Read an ownership record back. Tolerant on card ids for the same reason a decklist is: an id
        /// is a string until the database resolves it, and a card a balance patch removed is still a
        /// card the player was granted. A missing or non-numeric <c>copies</c> reads as 0 — a recorded
        /// row with no count is a record that the player has met this card and holds none of it, which
        /// is a real state (every copy dusted) and not a corrupt file.
        ///
        /// <para><b>A negative count is clamped to 0, not rejected.</b> Server-side, ownership is the
        /// signed sum of an append-only ledger, so a refund is a negative grant and a projection can
        /// legitimately arrive at less than nothing after a correction. L6 compares against copies used,
        /// where "less than none" and "none" mean the same refusal.</para>
        /// </summary>
        public static OwnedCardCounts ReadOwnedCards(string json)
        {
            var owned = new OwnedCardCounts();
            using var doc = ParseEnvelope(json, OwnedCardsProperty, out var rows);
            int i = 0;
            foreach (var el in rows.EnumerateArray())
            {
                int cardId = ReqCardId(el, "id", OwnedCardsProperty, i);
                int copies = el.TryGetProperty("copies", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetInt32()
                    : 0;
                owned.Set(cardId, copies < 0 ? 0 : copies);
                i++;
            }
            return owned;
        }

        // ---- shared --------------------------------------------------------------------

        private static JsonDocument ParseEnvelope(string json, string arrayProperty, out JsonElement rows)
        {
            var doc = ParseRoot(json, arrayProperty, out var root);

            if (!root.TryGetProperty(arrayProperty, out rows) || rows.ValueKind != JsonValueKind.Array)
            {
                doc.Dispose();
                throw new CollectionFormatException($"Collection file has no '{arrayProperty}' array.");
            }

            return doc;
        }

        /// <summary>
        /// Parse, confirm the root is an object, and enforce the version ceiling — the part every
        /// collection file shares regardless of whether its payload is an array or an object.
        /// </summary>
        private static JsonDocument ParseRoot(string json, string section, out JsonElement root)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new CollectionFormatException($"Collection file is not valid JSON ({section}).", ex);
            }

            root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                var kind = root.ValueKind;
                doc.Dispose();
                throw new CollectionFormatException($"Collection file root must be an object, was {kind}.");
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

            return doc;
        }

        /// <summary>
        /// An OPTIONAL card id — 0 when absent, which is the "no showcase chosen" state. A string
        /// here is a pre-migration file and is left to <see cref="ReqCardId"/> on the entries to
        /// report, so a deck is never rejected twice for the same reason.
        /// </summary>
        private static int OptCardId(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
               && v.TryGetInt32(out int id) ? id : 0;

        private static string? Str(JsonElement el, string name)
            => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        /// <summary>
        /// Reads a card id out of a saved collection. Separate from <see cref="Req"/> because a
        /// file written before the numeric migration carries a STRING here, and that has to say so
        /// — a player's decks are the one place in this system a stale id is unrecoverable from
        /// the repo, so the failure must name the migration rather than the token type.
        /// </summary>
        private static int ReqCardId(JsonElement el, string name, string section, int index)
        {
            if (!el.TryGetProperty(name, out var value))
                throw new CollectionFormatException(
                    $"{section}[{index}] is missing required '{name}'.");
            if (value.ValueKind == JsonValueKind.String)
                throw new CollectionFormatException(
                    $"{section}[{index}].{name} = '{value.GetString()}' is a card HANDLE. This file " +
                    "was saved before card ids became numbers and needs migrating.");
            if (!value.TryGetInt32(out int id))
                throw new CollectionFormatException(
                    $"{section}[{index}].{name} is not a whole number.");
            return id;
        }

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
