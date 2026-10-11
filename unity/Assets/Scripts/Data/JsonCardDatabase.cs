using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Data
{
    /// <summary>
    /// Loads the canonical card-row JSON array and serves it as ICardDatabase (dev-docs/specs/card-schema.md:5).
    /// Both the server (raw JSON file) and this Unity client path use CardJson.DeserializeArray —
    /// the single parser — so they can never drift (R1, ARCHITECTURE §3 rule 6).
    /// </summary>
    public sealed class JsonCardDatabase : ICardDatabase
    {
        private readonly Dictionary<int, CardDefinition> _cards = new Dictionary<int, CardDefinition>();

        public JsonCardDatabase(string json)
        {
            foreach (var def in CardJson.DeserializeArray(json))
                _cards[def.Id] = def;
        }

        /// <summary>Merge multiple card-set JSON payloads into one database. Last write wins on duplicate id.</summary>
        public JsonCardDatabase(IEnumerable<string> jsonPayloads)
        {
            foreach (var json in jsonPayloads)
                foreach (var def in CardJson.DeserializeArray(json))
                    _cards[def.Id] = def;
        }

        public bool TryGet(int cardId, out CardDefinition def) =>
            _cards.TryGetValue(cardId, out def);

        public CardDefinition Get(int cardId)
        {
            if (!_cards.TryGetValue(cardId, out var def))
                throw new System.Collections.Generic.KeyNotFoundException(
                    $"Card {cardId} not found in database.");
            return def;
        }

        public IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var result = new List<CardDefinition>();
            foreach (var def in _cards.Values)
                if (def.PoolEligible) result.Add(def);
            return result;
        }
    }
}
