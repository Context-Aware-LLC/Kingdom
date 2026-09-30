using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// The rows the game actually ships, for the handful of fixtures whose subject IS a real card —
    /// "does <c>storm_crackle</c> hit exactly one enemy" is a question about that card, not about a
    /// synthetic stand-in. Everything else should reach for <see cref="FakeCardDatabase"/> and
    /// <see cref="FixtureCard"/> instead, because a fixture that names a shipped card inherits every
    /// balance change made to it.
    ///
    /// <para><b>Every <c>*.cards.json</c> under <c>Assets/CardData</c>, found by GLOB.</b> The server
    /// globs the same directory; a hardcoded list of set files is the shape that broke here on
    /// 2026-08-24, when CORE became its own file and three fixtures kept reading two of the three
    /// sets without any of them failing — a missing set is invisible to a test that only asks about
    /// cards it can already see.</para>
    ///
    /// <para><see cref="Id"/> is the ONE place a handle resolves to an id, and it exists only inside
    /// tests. Nothing in production may do this (Sam, 2026-08-15: the test of the model is that you
    /// can change a handle and nothing breaks) — so a rename here surfaces as one failing lookup
    /// naming the handle, which is exactly the loud failure the ruling wants and the opposite of a
    /// shipped tool quietly keying on it.</para>
    /// </summary>
    public static class ShippedCards
    {
        private static IReadOnlyList<CardDefinition>? _cards;
        private static Dictionary<string, int>? _byHandle;
        private static JsonCardDatabase? _db;

        public static IReadOnlyList<CardDefinition> Cards => _cards ??= Load();

        public static ICardDatabase Db => _db ??= new JsonCardDatabase(Cards);

        /// <summary>The numeric id of a shipped card, by its mnemonic handle.</summary>
        public static int Id(string handle)
        {
            if (_byHandle == null)
            {
                _byHandle = new Dictionary<string, int>();
                foreach (CardDefinition c in Cards) _byHandle[c.Handle] = c.Id;
            }
            if (_byHandle.TryGetValue(handle, out int id)) return id;
            throw new KeyNotFoundException(
                $"No shipped card has the handle '{handle}'. It was renamed or withdrawn — the id it " +
                "used to carry is permanent, so look the card up by id rather than re-adding the handle.");
        }

        private static IReadOnlyList<CardDefinition> Load()
        {
            string dir = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets", "CardData");
            var all = new List<CardDefinition>();
            foreach (string file in Directory.GetFiles(dir, "*.cards.json"))
                all.AddRange(CardJson.DeserializeArray(File.ReadAllText(file)));
            return all;
        }
    }
}
