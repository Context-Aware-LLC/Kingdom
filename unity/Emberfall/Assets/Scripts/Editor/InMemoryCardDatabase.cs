using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Editor
{
    /// <summary>
    /// ICardDatabase over the authoring window's current in-memory (possibly unsaved) rows, so
    /// live validation resolves token/cross-references against what's on screen right now rather
    /// than stale on-disk state (hist-docs/source-snapshots/2026-07-31-pre-reorganization/tooling/card-authoring-tool.md §2: "so validation can resolve token ids
    /// exactly as the server will").
    /// </summary>
    public sealed class InMemoryCardDatabase : ICardDatabase
    {
        private readonly Dictionary<int, CardDefinition> _cards = new Dictionary<int, CardDefinition>();

        public InMemoryCardDatabase(IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _cards[d.Id] = d;
        }

        public bool TryGet(int cardId, out CardDefinition def) => _cards.TryGetValue(cardId, out def!);

        public CardDefinition Get(int cardId) => _cards[cardId];

        public IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var result = new List<CardDefinition>();
            foreach (var d in _cards.Values) if (d.PoolEligible) result.Add(d);
            return result;
        }
    }
}
