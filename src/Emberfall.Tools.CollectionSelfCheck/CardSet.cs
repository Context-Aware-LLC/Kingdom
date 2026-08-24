using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Tools.CollectionSelfCheck
{
    /// <summary>
    /// <see cref="ICardDatabase"/> over the live shipping card sets — every
    /// <c>unity/Emberfall/Assets/CardData/*.cards.json</c>, the same rows the client loads since
    /// EF-040.
    ///
    /// <para>The live sets, not the pinned golden-replay snapshot, and for the same reason
    /// <c>LiveCardSet</c> gives in the collection tests: those two files answer different questions.
    /// The snapshot exists so a rebalance cannot break the R1 determinism gate; deck legality is
    /// exactly the thing that <i>should</i> be re-checked against the real pool. A pairing that can
    /// no longer fill a legal 30 is a content bug this check is supposed to catch, not insulate.</para>
    /// </summary>
    internal sealed class CardSet : ICardDatabase
    {
        private readonly Dictionary<int, CardDefinition> _byId = new();

        private CardSet(IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _byId[d.Id] = d;
        }

        internal int Count => _byId.Count;

        public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);

        public CardDefinition Get(int cardId)
            => _byId.TryGetValue(cardId, out var d) ? d : throw new KeyNotFoundException($"Card {cardId} is not in the live set.");

        public IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var list = new List<CardDefinition>();
            foreach (var d in _byId.Values) if (d.PoolEligible) list.Add(d);
            return list;
        }

        /// <summary>Load every card set in <paramref name="cardDataDir"/>, sorted so the run is reproducible.</summary>
        internal static CardSet Load(string cardDataDir, out string[] filesLoaded)
        {
            if (!Directory.Exists(cardDataDir))
                throw new DirectoryNotFoundException($"No card data directory at '{cardDataDir}'.");

            var files = Directory.GetFiles(cardDataDir, "*.cards.json");
            if (files.Length == 0)
                throw new FileNotFoundException($"No *.cards.json under '{cardDataDir}'.");
            Array.Sort(files, StringComparer.Ordinal);

            var all = new List<CardDefinition>();
            foreach (var f in files) all.AddRange(CardJson.DeserializeArray(File.ReadAllText(f)));

            filesLoaded = Array.ConvertAll(files, Path.GetFileName)!;
            return new CardSet(all);
        }

        /// <summary>
        /// Walk up from the executing assembly until the repo root is recognisable. The tool runs from
        /// <c>.dotnet-build/bin/...</c>, which is outside <c>src/</c> (see Directory.Build.props), so the
        /// depth is not fixed and counting parents would be brittle.
        /// </summary>
        internal static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Emberfall.slnx"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not find Emberfall.slnx above '{AppContext.BaseDirectory}'. Pass --cards explicitly.");
        }

        internal static string DefaultCardDataDir()
            => Path.Combine(FindRepoRoot(), "unity", "Emberfall", "Assets", "CardData");
    }
}
