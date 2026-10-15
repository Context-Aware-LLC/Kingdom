using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
{
    /// <summary>One bot from <c>tooling/data/bot-roster.json</c>, its cards as a <see cref="DeckList"/>.</summary>
    public sealed class BotRosterDeck
    {
        public string AccountUsername { get; }
        public string DisplayName { get; }
        public DeckList Deck { get; }

        public BotRosterDeck(string accountUsername, string displayName, DeckList deck)
        {
            AccountUsername = accountUsername;
            DisplayName = displayName;
            Deck = deck;
        }
    }

    /// <summary>
    /// Reads and writes the bot roster as <see cref="DeckList"/>s, so the deck builder can edit the bot
    /// decks in a dev session (2026-10-15). The server reads the same file with its own loader
    /// (<c>Kingdom.Net.Server.Bots.BotRoster</c>), which Unity cannot reference.
    ///
    /// <para><b>A roster the server would refuse is never written.</b> The server validates every deck
    /// at boot and will not start on an illegal one, while the deck builder saves drafts freely — so
    /// <see cref="TryUpdate"/> runs the same L1-L5 gate (<see cref="QueueEligibility"/>) the server
    /// does and refuses the write instead.</para>
    ///
    /// <para>Hand-written, like <see cref="CollectionJson"/>, and written in the file's own shape — one
    /// id per copy, two-space indent — so an edit diffs as the cards that changed.</para>
    ///
    /// <para><b>The deck id is <c>"bot:" + accountUsername</c></b>, the id the server gives it, and the
    /// name is the display name. The username is the bot's account identity and is never edited here.</para>
    /// </summary>
    public static class BotRosterFile
    {
        public const string IdPrefix = "bot:";

        public static IReadOnlyList<BotRosterDeck> Read(string json)
        {
            var bots = new List<BotRosterDeck>();
            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                string username = row.GetProperty("accountUsername").GetString() ?? "";
                string displayName = row.GetProperty("displayName").GetString() ?? "";
                var deck = new DeckList(
                    ParseClass(row.GetProperty("primaryClass").GetString(), username),
                    ParseClass(row.GetProperty("secondaryClass").GetString(), username))
                {
                    Id = IdPrefix + username,
                    Name = displayName,
                };

                var counts = new Dictionary<int, int>();
                var order = new List<int>();
                foreach (JsonElement id in row.GetProperty("cards").EnumerateArray())
                {
                    int cardId = id.GetInt32();
                    if (!counts.TryGetValue(cardId, out int have)) order.Add(cardId);
                    counts[cardId] = have + 1;
                }
                foreach (int cardId in order) deck.Entries.Add(new DeckEntry(cardId, counts[cardId]));

                bots.Add(new BotRosterDeck(username, displayName, deck));
            }
            return bots;
        }

        public static string Write(IEnumerable<BotRosterDeck> bots)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartArray();
                foreach (BotRosterDeck bot in bots)
                {
                    w.WriteStartObject();
                    w.WriteString("accountUsername", bot.AccountUsername);
                    w.WriteString("displayName", bot.DisplayName);
                    w.WriteString("primaryClass", bot.Deck.PrimaryClass.ToString());
                    w.WriteString("secondaryClass", bot.Deck.SecondaryClass.ToString());
                    w.WriteStartArray("cards");
                    foreach (DeckEntry entry in bot.Deck.Entries)
                        for (int i = 0; i < entry.Count; i++) w.WriteNumberValue(entry.CardId);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            // The writer's newline is the platform's; the file is LF.
            return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
        }

        /// <summary>Why the server would refuse this deck at boot, or null when it would accept it.</summary>
        public static string? Refusal(DeckList deck, ICardDatabase db)
        {
            QueueCheck check = QueueEligibility.Check(deck, db);
            return check.IsEligible ? null : check.Reason;
        }

        /// <summary>
        /// The roster with <paramref name="edited"/>'s cards in place of its bot's, or false with the
        /// reason when the deck is illegal or names no bot in the roster. Only the cards change: the
        /// classes, username and display name stay as the file has them.
        /// </summary>
        public static bool TryUpdate(string json, DeckList edited, ICardDatabase db,
                                     out string updatedJson, out string refusal)
        {
            updatedJson = json;
            refusal = Refusal(edited, db) ?? "";
            if (refusal.Length > 0) return false;

            var bots = new List<BotRosterDeck>(Read(json));
            int index = bots.FindIndex(b => b.Deck.Id == edited.Id);
            if (index < 0)
            {
                refusal = $"no bot '{edited.Id}' in the roster";
                return false;
            }

            BotRosterDeck old = bots[index];
            var deck = new DeckList(old.Deck.PrimaryClass, old.Deck.SecondaryClass)
            {
                Id = old.Deck.Id,
                Name = old.Deck.Name,
            };
            deck.Entries.AddRange(edited.Entries);
            bots[index] = new BotRosterDeck(old.AccountUsername, old.DisplayName, deck);
            updatedJson = Write(bots);
            return true;
        }

        private static CardClass ParseClass(string? value, string username) =>
            Enum.TryParse(value, out CardClass c)
                ? c
                : throw new FormatException($"bot '{username}' has an unknown class '{value}'");
    }
}
