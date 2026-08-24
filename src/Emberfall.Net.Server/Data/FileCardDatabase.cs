using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Data
{
    /// <summary>
    /// Production <see cref="ICardDatabase"/> over `*.cards.json` rows (dev-docs/specs/card-schema.md:5;
    /// same headless shape as the golden-replay JsonCardDatabase — dev-docs/specs/card-schema.md:5). Source of
    /// truth stays `unity/Emberfall/Assets/CardData/`; the container bakes those files in at build.
    /// </summary>
    public sealed class FileCardDatabase : ICardDatabase
    {
        private readonly Dictionary<int, CardDefinition> _byId = new Dictionary<int, CardDefinition>();

        public FileCardDatabase(IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _byId[d.Id] = d;
        }

        /// <summary>Load every `*.cards.json` under <paramref name="dir"/> (recursive, ordinal-sorted
        /// so load order — and therefore last-write-wins on a duplicate id — is deterministic).</summary>
        public static FileCardDatabase LoadDirectory(string dir)
        {
            var files = Directory.GetFiles(dir, "*.cards.json", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
                throw new FileNotFoundException($"No *.cards.json under '{dir}' — the server has no cards to deal.");

            var defs = new List<CardDefinition>();
            foreach (var f in files)
                defs.AddRange(CardJson.DeserializeArray(File.ReadAllText(f)));
            return new FileCardDatabase(defs);
        }

        public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);

        public CardDefinition Get(int cardId)
            => _byId.TryGetValue(cardId, out var d) ? d : throw new KeyNotFoundException($"Card '{cardId}' not in database.");

        public IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var list = new List<CardDefinition>();
            foreach (var d in _byId.Values) if (d.PoolEligible) list.Add(d);
            return list;
        }
    }
}
