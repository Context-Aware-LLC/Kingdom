using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// <see cref="ICardDatabase"/> over a pinned card snapshot (dev-docs/pipelines/card-authoring-and-import.md:5). Loads the
    /// frosted JSON dump of <see cref="CardDefinition"/> rows the fixture replays against — NOT the live
    /// cards/** tree — so a future rebalance surfaces as a deliberate, reviewed fixture change rather
    /// than a mysterious CI break. This is the same headless impl the server uses (dev-docs/specs/card-schema.md:5).
    /// </summary>
    public sealed class JsonCardDatabase : ICardDatabase
    {
        private readonly Dictionary<string, CardDefinition> _byId = new(StringComparer.Ordinal);

        public JsonCardDatabase(IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _byId[d.Id] = d;
        }

        public static JsonCardDatabase FromSnapshotFile(string path)
            => new JsonCardDatabase(CardJson.DeserializeArray(File.ReadAllText(path)));

        public bool TryGet(string cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);

        public CardDefinition Get(string cardId)
            => _byId.TryGetValue(cardId, out var d) ? d : throw new KeyNotFoundException($"Card '{cardId}' not in snapshot.");

        public IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var list = new List<CardDefinition>();
            foreach (var d in _byId.Values) if (d.PoolEligible) list.Add(d);
            return list;
        }
    }
}
