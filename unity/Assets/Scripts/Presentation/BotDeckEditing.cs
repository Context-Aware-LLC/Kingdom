#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Editor-only dev mode (Kingdom/Cards/Edit Bot Decks): the Collection screen's deck drawer lists the
    /// bots from <c>tooling/data/bot-roster.json</c> instead of this account's decks, shows the full
    /// card pool, and saves back to that file instead of to the account and the server (2026-10-15).
    ///
    /// <para><b>An illegal deck is never written</b> — the server will not boot on one
    /// (<see cref="BotRosterFile"/>). It is kept as a draft for this Editor session instead, so a
    /// half-finished deck reopens as it was left; it is lost when the Editor reloads scripts.</para>
    ///
    /// <para>Nothing reaches a live server until the roster is committed and deployed: the server
    /// image bakes the file in.</para>
    /// </summary>
    public static class BotDeckEditing
    {
        private const string PrefKey = "Kingdom.Dev.EditBotDecks";

        public static bool Enabled
        {
            get => UnityEditor.EditorPrefs.GetBool(PrefKey, false);
            set => UnityEditor.EditorPrefs.SetBool(PrefKey, value);
        }

        public static string RosterPath => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", "tooling", "data", "bot-roster.json"));

        private static readonly Dictionary<string, DeckList> Drafts = new Dictionary<string, DeckList>();

        public static IReadOnlyList<DeckSummary> DeckRows(ICardDatabase db)
        {
            var rows = new List<DeckSummary>();
            foreach (BotRosterDeck bot in BotRosterFile.Read(File.ReadAllText(RosterPath)))
            {
                DeckList deck = Drafts.TryGetValue(bot.Deck.Id, out DeckList draft) ? draft : bot.Deck;
                rows.Add(new DeckSummary(deck, QueueEligibility.Check(deck, db)));
            }
            return rows;
        }

        /// <summary>Writes the deck into the roster. Null when written, else why it was not.</summary>
        public static string Save(DeckList deck, ICardDatabase db)
        {
            string json = File.ReadAllText(RosterPath);
            if (!BotRosterFile.TryUpdate(json, deck, db, out string updated, out string refusal))
            {
                Drafts[deck.Id] = deck;
                return refusal;
            }
            Drafts.Remove(deck.Id);
            if (updated != json.Replace("\r\n", "\n")) File.WriteAllText(RosterPath, updated);
            return null;
        }
    }
}
#endif
