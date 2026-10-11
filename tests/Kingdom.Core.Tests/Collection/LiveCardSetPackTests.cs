using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// NOR-109 §7, content half — the <b>real</b> card set must be able to honour what the algorithm
    /// promises. <see cref="BoosterPackOpener"/> degrades gracefully when a pairing has no cards of a
    /// rolled rarity (it substitutes the nearest rarity it can fill), which means a content gap would
    /// otherwise ship as a quietly worse pack rather than as a failure. This fixture is what makes that
    /// gap loud.
    ///
    /// <para>Deliberately reads the live card files, not the pinned golden-replay snapshot — for the
    /// same reason <see cref="LiveCardSet"/> does. A set that cannot fill a pack is a content bug these
    /// tests exist to catch, not to be insulated from.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class LiveCardSetPackTests
    {
        private static IReadOnlyList<CardDefinition> LoadEveryLiveCard()
        {
            string dir = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets", "CardData");
            Assert.That(Directory.Exists(dir), Is.True, $"Card data directory not found at {dir}");

            var files = new List<string>(Directory.GetFiles(dir, "*.cards.json"));
            files.Sort(System.StringComparer.Ordinal);
            Assert.That(files, Is.Not.Empty, $"No *.cards.json under {dir}");

            var all = new List<CardDefinition>();
            foreach (var file in files) all.AddRange(CardJson.DeserializeArray(File.ReadAllText(file)));
            return all;
        }

        [Test]
        public void Every_Live_Pack_Can_Fill_Every_Rarity()
        {
            var db = new JsonCardDatabase(LoadEveryLiveCard());
            var rarities = new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY };

            foreach (var pack in BoosterPack.All())
            {
                var pool = pack.Pool(db);
                var counts = new Dictionary<Rarity, int>();
                foreach (var r in rarities) counts[r] = 0;
                foreach (var card in pool) counts[card.Rarity]++;

                TestContext.Out.WriteLine(
                    $"{pack.Id,-18} pool={pool.Count,4}  " +
                    $"C={counts[Rarity.COMMON],3} R={counts[Rarity.RARE],3} " +
                    $"E={counts[Rarity.EPIC],3} L={counts[Rarity.LEGENDARY],3}");

                foreach (var r in rarities)
                    Assert.That(counts[r], Is.GreaterThan(0),
                        $"{pack.Id} has no {r} cards — the opener would silently substitute another rarity.");
            }
        }

        /// <summary>
        /// A pack must be openable end to end against the shipping pool, guarantees included. Cheap, and
        /// it is the only fixture that would catch a pool the algorithm cannot actually consume.
        /// </summary>
        [Test]
        public void A_Live_Pack_Opens_And_Honours_The_Rare_Floor()
        {
            var db = new JsonCardDatabase(LoadEveryLiveCard());

            foreach (var pack in BoosterPack.All())
            {
                var pool = pack.Pool(db);
                var pity = PackPityState.Fresh;
                var owned = new OwnedCardCounts();

                for (int i = 0; i < 200; i++)
                {
                    var result = BoosterPackOpener.Open(pack, pool, owned, pity, 0x11FE_5EEDUL, pity.PacksOpened);

                    Assert.That(result.Awards, Has.Count.EqualTo(pack.CardCount));
                    bool rareOrBetter = false;
                    foreach (var a in result.Awards)
                    {
                        Assert.That(db.TryGet(a.CardId, out var def), Is.True, $"{a.CardId} is not a real card.");
                        Assert.That(def.Rarity, Is.EqualTo(a.Rarity), $"{a.CardId} awarded at the wrong rarity.");
                        Assert.That(pack.Contains(def.SetId), Is.True, $"{a.CardId} is outside {pack.Id}'s set.");
                        if (a.Rarity >= Rarity.RARE) rareOrBetter = true;
                        owned.Set(a.CardId, owned.CopiesOwned(a.CardId) + 1);
                    }
                    Assert.That(rareOrBetter, Is.True, $"{pack.Id} pack {i} was all Commons.");
                    pity = result.PityAfter;
                }

                TestContext.Out.WriteLine($"{pack.Id,-18} 200 packs opened, final pity: {pity}");
            }
        }
    }
}
