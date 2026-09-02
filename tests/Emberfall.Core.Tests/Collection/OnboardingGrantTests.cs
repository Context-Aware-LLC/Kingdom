using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The guard <see cref="OnboardingGrant"/>'s <c>LaunchExpansion</c> RETIREMENT TRIGGER names —
    /// <c>GrantIsEveryCollectibleCommonAndRareInThePairing</c>. It was named there before it existed: this
    /// repo has never held a file called <c>OnboardingGrantTests</c>, so the trigger had nothing to
    /// point at and the second expansion would have landed against no check at all.
    ///
    /// <para><b>Every expectation is DERIVED from the live card set, never written as a literal.</b>
    /// The grant's size is a RECORD of the corpus at the moment someone counted it, never a target
    /// for it (CLAUDE.md), so an assertion like "71 commons" would convert a content decision into a
    /// test failure and invite somebody to work backwards from the number. What these tests actually
    /// pin is the RELATION — the grant is exactly the collectible commons and rares of the pairing,
    /// in id order — which cannot go stale as cards are added.</para>
    ///
    /// <para>Uses <see cref="LiveCardSet"/> for the same reason its own doc gives: deck legality and
    /// the grant are precisely the things that should be re-checked against the real pool rather than
    /// insulated from it by a pinned snapshot.</para>
    /// </summary>
    [TestFixture]
    public sealed class OnboardingGrantTests
    {
        private static ICardDatabase Db => LiveCardSet.Db;

        /// <summary>
        /// The card half is every collectible COMMON and RARE in the pairing — no more, no less, and
        /// in the same order. This is the assertion the retirement trigger sends the second
        /// expansion's author to: it fails the moment "the launch pairing" stops meaning "what a new
        /// account gets", which is the decision that must not be made by default.
        ///
        /// <para><b>Rares joined the grant 2026-09-02 (Sam).</b> Commons alone left 7 of the 28
        /// pairings unable to field a legal 30 — measured range 27–35, with a real player stuck at
        /// 28 on CLOCKWRIGHT+STONEWARDEN. Commons plus rares is 45–60, none under 30.</para>
        /// </summary>
        [Test]
        public void GrantIsEveryCollectibleCommonAndRareInThePairing()
        {
            BoosterPack pack = OnboardingGrant.StarterPack();

            var expected = new List<int>();
            foreach (CardDefinition card in Db.AllCollectible())
                if (card != null && pack.Contains(card.SetId) &&
                    (card.Rarity == Rarity.COMMON || card.Rarity == Rarity.RARE))
                    expected.Add(card.Id);
            expected.Sort();

            Assert.That(expected, Is.Not.Empty, "the live pairing has no collectible commons or rares");
            Assert.That(OnboardingGrant.CardGrant(Db), Is.EqualTo(expected));
        }

        /// <summary>Nothing above RARE is granted outright. The packs have to stay the only way an
        /// epic or a legendary arrives, or opening one has nothing left to give — which is the whole
        /// argument for the boosted odds.</summary>
        [Test]
        public void GrantContainsNoEpicsOrLegendaries()
        {
            var granted = new HashSet<int>(OnboardingGrant.CardGrant(Db));

            foreach (CardDefinition card in Db.AllCollectible())
                if (card != null && granted.Contains(card.Id))
                    Assert.That(card.Rarity, Is.AnyOf(Rarity.COMMON, Rarity.RARE),
                        $"'{card.Name}' is {card.Rarity} and must not be granted outright.");
        }

        /// <summary>
        /// Strictly ascending, which is stronger than "sorted": it also proves no id is granted twice.
        /// The order matters because <c>AllCollectible</c> promises none, and a grant that walked a
        /// dictionary would write a different collection record on two machines that own identical
        /// collections — the reason <see cref="OnboardingGrant.CardGrant"/> sorts at all.
        /// </summary>
        [Test]
        public void CardGrantIsStrictlyAscendingSoTwoMachinesWriteTheSameRecord()
        {
            IReadOnlyList<int> ids = OnboardingGrant.CardGrant(Db);

            for (int i = 1; i < ids.Count; i++)
                Assert.That(ids[i], Is.GreaterThan(ids[i - 1]),
                    $"ids must ascend strictly; {ids[i]} follows {ids[i - 1]} at index {i}");
        }

        /// <summary>
        /// Hero powers and tokens are not ownable and were never meant to be in the grant. They are
        /// COMMON rows in CORE, so a membership test that checked only rarity and set would sweep
        /// them in — the collectible filter is the thing keeping them out, and this is what says so.
        /// </summary>
        [Test]
        public void GrantExcludesRowsThatAreNotCollectible()
        {
            var granted = new HashSet<int>(OnboardingGrant.CardGrant(Db));

            foreach (CardDefinition card in LiveCardSet.Cards)
                if (card != null && !card.Collectible)
                    Assert.That(granted, Does.Not.Contain(card.Id),
                        $"{card.Id} is not collectible and must never be granted");
        }

        /// <summary>
        /// <see cref="OnboardingGrant.Resolve"/> hands back both halves at once. The type's remarks
        /// give the reason — a caller that could take one half and not the other would have a
        /// half-granted state to recover from — so this pins that the plan carries the packs as well
        /// as the commons, and that the packs are the launch pairing at the starter shape.
        /// </summary>
        [Test]
        public void ResolveHandsBackTheCardsAndTheStarterPacksTogether()
        {
            OnboardingGrantPlan plan = OnboardingGrant.Resolve(Db);

            Assert.That(plan.GrantedCardIds, Is.EqualTo(OnboardingGrant.CardGrant(Db)));
            Assert.That(plan.GrantedCardCount, Is.EqualTo(plan.GrantedCardIds.Count));
            Assert.That(plan.Packs, Has.Count.EqualTo(OnboardingGrant.StarterPackCount));
            Assert.That(plan.PackCardCount,
                Is.EqualTo(OnboardingGrant.StarterPackCount * OnboardingGrant.StarterPackCardCount));

            foreach (BoosterPack pack in plan.Packs)
            {
                Assert.That(pack.Expansion, Is.EqualTo(OnboardingGrant.LaunchExpansion));
                Assert.That(pack.CardCount, Is.EqualTo(OnboardingGrant.StarterPackCardCount));
                Assert.That(pack.Odds, Is.EqualTo(OnboardingGrant.StarterOdds));
            }
        }

        /// <summary>
        /// <see cref="OnboardingGrant.ApplyGrantedCards"/> ADDS. Its doc says so and the reason is that
        /// assigning would silently reduce a collection that already held copies — the failure mode
        /// nobody would notice, because the card is still there, just fewer of it.
        /// </summary>
        [Test]
        public void ApplyGrantedCardsAddsToWhatIsThereRatherThanAssigning()
        {
            OnboardingGrantPlan plan = OnboardingGrant.Resolve(Db);
            var owned = new OwnedCardCounts();
            int first = plan.GrantedCardIds[0];
            owned.Set(first, 2);

            OnboardingGrant.ApplyGrantedCards(plan, owned);

            Assert.That(owned.CopiesOwned(first), Is.EqualTo(3),
                "applying the grant must never reduce a collection");
            Assert.That(owned.CopiesOwned(plan.GrantedCardIds[1]), Is.EqualTo(1),
                "a card with no prior copies gets exactly one");
        }

        /// <summary>
        /// The granted starter pack and the saleable retail pack must not be the same entitlement.
        ///
        /// <para><b>Why this is a test and not a comment.</b> <see cref="BoosterPack.Id"/> is the
        /// ledger's entitlement item, the pity key and the seed key all at once, and the server
        /// reserves against it while taking the pack's SHAPE — card count and odds — from whoever
        /// calls the opener. One shared id therefore makes a granted pack and a bought pack the same
        /// balance, and lets a retail entitlement be paid out at these boosted odds. Nothing about
        /// reading either type reveals that; only the collision does.</para>
        /// </summary>
        [Test]
        public void TheStarterPackIsADifferentEntitlementFromTheRetailPack()
        {
            BoosterPack starter = OnboardingGrant.StarterPack();
            var retail = new BoosterPack(OnboardingGrant.LaunchExpansion);

            Assert.That(starter.Id, Is.Not.EqualTo(retail.Id),
                "a granted starter pack must not be spendable as a retail open, or vice versa");
            Assert.That(starter.Id, Is.EqualTo(retail.Id + "." + BoosterPack.StarterSku));
        }

        /// <summary>
        /// <see cref="BoosterPack.All"/> enumerates what can be SOLD. The starter pack is granted
        /// once and never sold, so it must not appear there — the odds-disclosure tool and the store
        /// catalogue both read that list, and a boosted pack showing up in either would be a
        /// disclosure error, not just a cosmetic one.
        /// </summary>
        [Test]
        public void TheStarterPackIsNotOnTheSaleableList()
        {
            foreach (BoosterPack pack in BoosterPack.All())
                Assert.That(pack.Id, Does.Not.Contain(BoosterPack.StarterSku),
                    $"{pack.Id} is saleable; the starter pack is granted, never sold");
        }

        /// <summary>
        /// The sku separates the ENTITLEMENT, not the contents. Both packs draw the same pairing, so
        /// a card is not reachable from one and unreachable from the other — only the count and the
        /// odds differ, which is what the starter grant was ruled to be.
        /// </summary>
        [Test]
        public void TheStarterAndRetailPacksShareAPool()
        {
            BoosterPack starter = OnboardingGrant.StarterPack();
            var retail = new BoosterPack(OnboardingGrant.LaunchExpansion);

            Assert.That(starter.Pool(Db), Is.EqualTo(retail.Pool(Db)));
        }
    }
}
