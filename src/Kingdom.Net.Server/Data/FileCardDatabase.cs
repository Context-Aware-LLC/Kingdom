using System;
using System.Collections.Generic;
using System.IO;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Net.Server.Data
{
    /// <summary>
    /// Production <see cref="ICardDatabase"/> over `*.cards.json` rows (dev-docs/specs/card-schema.md:5;
    /// same headless shape as the golden-replay JsonCardDatabase — dev-docs/specs/card-schema.md:5). Source of
    /// truth stays `unity/Assets/CardData/`; the container bakes those files in at build.
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

        /// <summary>Rows loaded, AFTER last-write-wins on a duplicate id — so this can read lower
        /// than the row count in the files. Reported on `/healthz` beside the corpus digest as a
        /// human-readable sanity number; the digest, not this, is what a comparison keys on.</summary>
        public int Count => _byId.Count;

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
