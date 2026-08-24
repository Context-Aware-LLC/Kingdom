using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// <see cref="ICardDatabase"/> over a pinned card snapshot (dev-docs/pipelines/card-authoring-and-import.md:5). Loads the
    /// pinned JSON dump of <see cref="CardDefinition"/> rows the fixture replays against — NOT the live
    /// cards/** tree — so a future rebalance surfaces as a deliberate, reviewed fixture change rather
    /// than a mysterious CI break. This is the same headless impl the server uses (dev-docs/specs/card-schema.md:5).
    /// </summary>
    public sealed class JsonCardDatabase : ICardDatabase
    {
        private readonly Dictionary<int, CardDefinition> _byId = new();

        public JsonCardDatabase(IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _byId[d.Id] = d;
        }

        public static JsonCardDatabase FromSnapshotFile(string path)
            => new JsonCardDatabase(CardJson.DeserializeArray(File.ReadAllText(path)));

        public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);

        public CardDefinition Get(int cardId)
            => _byId.TryGetValue(cardId, out var d) ? d : throw new KeyNotFoundException($"Card {cardId} not in snapshot.");

        /// <summary>
        /// The id a handle carries IN THIS SNAPSHOT. Test-only, and the reason it is safe: a pinned
        /// snapshot is a closed set whose handles cannot drift under the fixture the way a shipped
        /// row's can. Production resolves nothing by handle (Sam, 2026-08-15).
        /// </summary>
        public int Id(string handle)
        {
            foreach (var d in _byId.Values) if (d.Handle == handle) return d.Id;
            throw new KeyNotFoundException($"No card in this snapshot has the handle '{handle}'.");
        }

        public IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var list = new List<CardDefinition>();
            foreach (var d in _byId.Values) if (d.PoolEligible) list.Add(d);
            return list;
        }
    }
}
