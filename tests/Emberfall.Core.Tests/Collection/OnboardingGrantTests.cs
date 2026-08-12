using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The one-time new-account grant (Sam, 2026-08-12) — see <see cref="OnboardingGrant"/>.
    ///
    /// <para><b>Nothing here pins a card count.</b> The grant is "every collectible common in the
    /// launch pairing", so the expected set is re-derived from the same live data rather than written
    /// down as a number: a card added to EMBERFALL should join the grant, not fail this fixture. The
    /// counts are printed to the test log instead, where they are a record and not an assertion.</para>
    ///
    /// <para>Reads the live card files for the same reason <see cref="LiveCardSetPackTests"/> does —
    /// a grant that the shipped set cannot honour is a content bug worth failing on.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class OnboardingGrantTests
    {
        private const ulong Seed = 0x0B0A2D_0F12E5UL;

        private static IReadOnlyList<CardDefinition> LoadEveryLiveCard()
        {
            string dir = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "CardData");
            Assert.That(Directory.Exists(dir), Is.True, $"Card data directory not found at {dir}");

            var files = new List<string>(Directory.GetFiles(dir, "*.cards.json"));
            files.Sort(System.StringComparer.Ordinal);
            Assert.That(files, Is.Not.Empty, $"No *.cards.json under {dir}");

            var all = new List<CardDefinition>();
            foreach (var file in files) all.AddRange(CardJson.DeserializeArray(File.ReadAllText(file)));
            return all;
        }

        private static JsonCardDatabase LiveDb() => new JsonCardDatabase(LoadEveryLiveCard());

        /// <summary>An <see cref="IOwnedCardStore"/> that is whatever the test says it is.</summary>
        private sealed class FakeOwnedStore : IOwnedCardStore
        {
            private OwnedCardCounts? _record;
            public FakeOwnedStore(OwnedCardCounts? record) => _record = record;
            public OwnedCardCounts? Load() => _record;
            public void Save(OwnedCardCounts owned) => _record = owned;
        }

        // ── what the grant contains ──────────────────────────────────────────────────────────

        [Test]
        public void GrantIsEveryCollectibleCommonInThePairing()
        {
            var db = LiveDb();

            var expected = new List<string>();
            foreach (var card in db.AllCollectible())
                if (card.Rarity == Rarity.COMMON &&
                    (card.SetId == SetId.CORE || card.SetId == OnboardingGrant.LaunchExpansion))
                    expected.Add(card.Id);
            expected.Sort(System.StringComparer.Ordinal);

            var actual = OnboardingGrant.CommonGrant(db);

            TestContext.Out.WriteLine($"grant = 1 copy each of {actual.Count} collectible commons " +
                                      $"(CORE + {OnboardingGrant.LaunchExpansion})");
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void GrantExcludesTokensOtherSetsAndEveryHigherRarity()
        {
            var db = LiveDb();
            var granted = new HashSet<string>(OnboardingGrant.CommonGrant(db));

            foreach (var card in db.AllCollectible())
            {
                if (granted.Contains(card.Id))
                {
                    Assert.That(card.Collectible, Is.True, $"{card.Id} is not ownable.");
                    Assert.That(card.Rarity, Is.EqualTo(Rarity.COMMON), $"{card.Id} is not a common.");
                    Assert.That(card.SetId == SetId.CORE || card.SetId == OnboardingGrant.LaunchExpansion,
                                Is.True, $"{card.Id} is outside the launch pairing ({card.SetId}).");
                }
                else if (card.Rarity == Rarity.COMMON &&
                         (card.SetId == SetId.CORE || card.SetId == OnboardingGrant.LaunchExpansion))
                {
                    Assert.Fail($"{card.Id} is a collectible common in the pairing but was not granted.");
                }
            }
        }

        [Test]
        public void GrantIsSortedByIdSoTheRecordIsMachineIndependent()
        {
            var ids = OnboardingGrant.CommonGrant(LiveDb());
            for (int i = 1; i < ids.Count; i++)
                Assert.That(System.StringComparer.Ordinal.Compare(ids[i - 1], ids[i]), Is.LessThan(0),
                            $"Grant is unsorted (or holds a duplicate) at {ids[i - 1]} / {ids[i]}.");
        }

        // ── applying it ──────────────────────────────────────────────────────────────────────

        [Test]
        public void ApplyCommonsAddsExactlyOneCopyOfEach()
        {
            var plan = OnboardingGrant.Resolve(LiveDb());
            var owned = new OwnedCardCounts();

            OnboardingGrant.ApplyCommons(plan, owned);

            Assert.That(owned.RecordedCards, Is.EqualTo(plan.CommonCount));
            foreach (var id in plan.CommonCardIds)
                Assert.That(owned.CopiesOwned(id), Is.EqualTo(1), $"{id}");
        }

        [Test]
        public void ApplyCommonsAddsToWhatIsThereAndNeverReduces()
        {
            var plan = OnboardingGrant.Resolve(LiveDb());
            string first = plan.CommonCardIds[0];

            var owned = new OwnedCardCounts();
            owned.Set(first, 7);                    // a hoarder, per the no-auto-shard ruling
            OnboardingGrant.ApplyCommons(plan, owned);

            Assert.That(owned.CopiesOwned(first), Is.EqualTo(8),
                        "The grant assigned over an existing count instead of adding to it.");
        }

        // ── once only ────────────────────────────────────────────────────────────────────────

        [Test]
        public void IsDueOnlyWhenTheOwnershipRecordIsAbsent()
        {
            Assert.That(OnboardingGrant.IsDue(new FakeOwnedStore(null)), Is.True,
                        "An account with no record has never been granted.");

            Assert.That(OnboardingGrant.IsDue(new FakeOwnedStore(new OwnedCardCounts())), Is.False,
                        "An EMPTY record is a granted account that spent everything — not a new one.");

            var held = new OwnedCardCounts();
            held.Set("ef_anything", 2);
            Assert.That(OnboardingGrant.IsDue(new FakeOwnedStore(held)), Is.False);
        }

        [Test]
        public void SavingTheRecordIsWhatDischargesTheGrant()
        {
            var store = new FakeOwnedStore(null);
            var plan = OnboardingGrant.Resolve(LiveDb());

            Assert.That(OnboardingGrant.IsDue(store), Is.True);

            var owned = new OwnedCardCounts();
            OnboardingGrant.ApplyCommons(plan, owned);
            store.Save(owned);

            Assert.That(OnboardingGrant.IsDue(store), Is.False, "The grant would fire a second time.");
        }

        // ── the packs ────────────────────────────────────────────────────────────────────────

        [Test]
        public void StarterPacksAreBiggerAndRicherThanRetailWithoutMovingRetail()
        {
            var starter = OnboardingGrant.StarterPack();
            var retail = new BoosterPack(OnboardingGrant.LaunchExpansion);

            Assert.That(starter.CardCount, Is.GreaterThan(retail.CardCount));
            Assert.That(retail.CardCount, Is.EqualTo(BoosterPack.DefaultCardCount),
                        "The retail pack count moved. Sam locked it at 5 on 2026-08-02.");

            foreach (var pack in BoosterPack.All())
                Assert.That(pack.Odds.PermilleOf(Rarity.LEGENDARY),
                            Is.EqualTo(PackRarityOdds.Default.PermilleOf(Rarity.LEGENDARY)),
                            $"Live odds moved for {pack.Id}; the boost must stay contained to onboarding.");

            foreach (var rarity in new[] { Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY })
                Assert.That(starter.Odds.PermilleOf(rarity),
                            Is.GreaterThan(PackRarityOdds.Default.PermilleOf(rarity)), $"{rarity}");

            Assert.That(starter.Odds.PermilleOf(Rarity.COMMON),
                        Is.LessThan(PackRarityOdds.Default.PermilleOf(Rarity.COMMON)));
        }

        [Test]
        public void StarterOddsSumToScale()
        {
            var o = OnboardingGrant.StarterOdds;
            int sum = o.PermilleOf(Rarity.COMMON) + o.PermilleOf(Rarity.RARE)
                    + o.PermilleOf(Rarity.EPIC) + o.PermilleOf(Rarity.LEGENDARY);
            Assert.That(sum, Is.EqualTo(PackRarityOdds.Scale));
        }

        [Test]
        public void ThePlanIsFivePacksAndNothingHalfLanded()
        {
            var plan = OnboardingGrant.Resolve(LiveDb());

            Assert.That(plan.Packs.Count, Is.EqualTo(OnboardingGrant.StarterPackCount));
            Assert.That(plan.PackCardCount,
                        Is.EqualTo(OnboardingGrant.StarterPackCount * OnboardingGrant.StarterPackCardCount));
            TestContext.Out.WriteLine($"plan: {plan.CommonCount} commons outright + {plan.PackCardCount} pack cards");
        }

        // ── the claim the design rests on ────────────────────────────────────────────────────

        /// <summary>
        /// The reason the packs need not be class-seeded: the commons half alone gets every pairing
        /// most of the way to a legal 30, and the grant as a whole clears it outright. Asserted as a
        /// property of the live set, not against a pinned number.
        /// </summary>
        [Test]
        public void TheWholeGrantClearsALegalDeckForEveryPairing()
        {
            var db = LiveDb();
            var plan = OnboardingGrant.Resolve(db);

            var owned = new OwnedCardCounts();
            OnboardingGrant.ApplyCommons(plan, owned);

            var pity = new PackPityState(0, 0, 0);
            var pool = OnboardingGrant.StarterPack().Pool(db);
            for (int i = 0; i < plan.Packs.Count; i++)
            {
                var result = BoosterPackOpener.Open(plan.Packs[i], pool, owned, pity, Seed, i);
                foreach (var award in result.Awards)
                    owned.Set(award.CardId, owned.CopiesOwned(award.CardId) + 1);
                pity = result.PityAfter;
            }

            var classes = new List<CardClass>();
            foreach (var card in db.AllCollectible())
                if (card.Class != CardClass.NEUTRAL && !classes.Contains(card.Class)) classes.Add(card.Class);
            classes.Sort();

            for (int a = 0; a < classes.Count; a++)
            for (int b = a + 1; b < classes.Count; b++)
            {
                int playable = 0;
                foreach (var card in db.AllCollectible())
                {
                    if (card.Class != CardClass.NEUTRAL && card.Class != classes[a] && card.Class != classes[b])
                        continue;
                    playable += DeckValidator.CopyLimit(card.Rarity, owned, card.Id);
                }

                Assert.That(playable, Is.GreaterThanOrEqualTo(DeckValidator.DeckSize),
                            $"{classes[a]} + {classes[b]} cannot field a legal deck from the grant " +
                            $"({playable} playable cards).");
            }
        }
    }
}
