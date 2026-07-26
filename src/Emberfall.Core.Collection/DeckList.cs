using System.Collections.Generic;

namespace Emberfall.Core.Collection
{
    /// <summary>One line of a decklist: a card definition id and how many copies (ui/collection-deckbuilder.md §3).</summary>
    public readonly struct DeckEntry
    {
        public readonly string CardId;
        public readonly int Count;
        public DeckEntry(string cardId, int count) { CardId = cardId; Count = count; }
        public DeckEntry With(int count) => new DeckEntry(CardId, count);
    }

    /// <summary>
    /// The persisted deck payload (ui/collection-deckbuilder.md §3): a <see cref="Character"/> binding,
    /// a name, and a small list of {definitionId, count}. Ported from the original Unity-only
    /// Emberfall.Data.DeckList (the C-G0-5 spike) — single-CardClass HeroClass replaced by CharacterId
    /// per the dual-class ruling. UnityEngine-free so this and <see cref="DeckValidator"/> move
    /// server-side unchanged (interfaces.md §1.3 pattern).
    /// </summary>
    public sealed class DeckList
    {
        public string Id { get; set; } = "";
        public string CharacterId { get; set; } = "";   // L5: a deck belongs to exactly one Character
        public string Name { get; set; } = "New Deck";
        public List<DeckEntry> Entries { get; } = new List<DeckEntry>();

        public int TotalCount
        {
            get
            {
                int n = 0;
                foreach (var e in Entries) n += e.Count;
                return n;
            }
        }

        public int CountOf(string cardId)
        {
            foreach (var e in Entries)
                if (e.CardId == cardId) return e.Count;
            return 0;
        }
    }
}
