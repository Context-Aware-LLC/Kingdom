using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// What a brand-new account is given, once, the first time its session resolves
    /// (<b>Sam, 2026-08-12</b>; rares added 2026-09-02). A copy of every collectible
    /// <b>common and rare</b> in the launch pairing, and <see cref="StarterPackCount"/> boosted
    /// booster packs, plus <see cref="StarterShardCount"/> crafting shards.
    ///
    /// <para><b>This is the product decision <see cref="IOwnedCardStore"/> used to say nobody had
    /// made.</b> That paragraph is gone; this type is where the answer lives, in the collection
    /// assembly rather than in a store or a screen, so the server and the client cannot disagree
    /// about what a new player owns.</para>
    ///
    /// <para><b>Pure and storeless.</b> It resolves <i>what</i> to grant and hands back packs the
    /// caller opens with its own seed — it writes nothing, opens nothing, and has no clock. The
    /// server resolves the opens (ADR-0004: the client never speculates one), and both sides get the
    /// same answer from the same card database.</para>
    ///
    /// <para><b>Account-level and once only, keyed on the ledger transaction id
    /// <c>signup:{playerId}</c> for cards/packs and <c>signup_shards_v1:{playerId}</c> for shards</b> (2026-09-01; spec <c>docs/dev-docs/specs/card-ownership.md</c>,
    /// slice 1). <c>OnboardingGrantService</c> writes the card/pack grant as one atomic entitlement batch
    /// under that id, so a replay conflicts on the derived row keys and writes nothing — the grant is
    /// attempted on every account resolve and there is deliberately no due-check for two resolves to
    /// race. A player who reduces their entire collection to shards is still never re-granted, because
    /// the ledger keeps the original grant rows forever: a zero balance is not an absent grant.</para>
    ///
    /// <para><b>This key used to be the absence of an ownership record</b>, back when the client's
    /// <c>owned-cards.json</c> was the authority — see <see cref="IOwnedCardStore"/>, whose null still
    /// means "ownership unknown" and still governs what an unsynced client shows.
    /// A client-side <c>IsDue</c> answered that question until slice 3 gave the client a real
    /// projection to mirror; it was deleted then, per its own retirement trigger, rather than left in
    /// this assembly looking like a grant check.
    /// The crash argument is unchanged in substance and now rests on the batch rather than on
    /// write ordering: an entitlement batch is all-or-nothing, so a crash mid-grant leaves the account
    /// un-granted and the next resolve simply lands it. That is still why <see cref="Resolve"/>
    /// returns everything at once instead of a grant per step.</para>
    ///
    /// <para><b>Why the packs are not seeded to a deck's classes.</b> Sam weighed both and
    /// ruled unseeded on 2026-08-12. The grant fires once per <i>account</i>, while a class pairing
    /// is a per-<i>deck</i> choice, and 93% of this set's non-commons are class-locked (56/60
    /// rares, 19/21 epics, 11/11 legendaries). Seeding would therefore concentrate an account's only
    /// gift into whichever two classes a new player picked in their first minute, and every deck
    /// after that would get nothing.</para>
    ///
    /// <para><b>The packs are spice, not legality — and that is now TRUE, which it was not.</b> This
    /// paragraph used to argue it from "the commons half already covers 22–29 of a 30-card deck",
    /// a sentence that refutes itself: 22–29 is short of 30. Measured against the live card data on
    /// 2026-09-02, commons alone left 7 of the 28 pairings unable to field a legal deck, and a player
    /// on CLOCKWRIGHT+STONEWARDEN owned every card they could use and reached 28. Since the packs
    /// arrive as an unopened balance, legality depended on a step the player might never take. The
    /// card half now grants every COMMON <i>and</i> RARE (<see cref="CardGrant"/>), which puts every
    /// pairing at 45–60 — so the claim above holds on its own arithmetic rather than in spite of
    /// it.</para>
    /// </summary>
    public static class OnboardingGrant
    {
        /// <summary>
        /// The expansion half of the pairing the grant is defined against. The card half and the
        /// starter packs both read it, so they cannot drift apart.
        ///
        /// <para>RETIREMENT TRIGGER: THE SECOND EXPANSION SHIPS. "Every common and rare in the launch set" is a
        /// launch-shaped answer. The commit that adds a third <see cref="SetId"/> member intended for
        /// sale must decide what a new account gets then — every common and rare in every set, or only the
        /// current one — and update this constant, the XML above, and
        /// <c>OnboardingGrantTests.GrantIsEveryCollectibleCommonAndRareInTheStarterSet</c>
        /// (<c>tests/Emberfall.Core.Tests/Collection/OnboardingGrantTests.cs</c>). Leaving it pointing
        /// at EMBERFALL is a decision, not a default, and should be a deliberate one.</para>
        /// </summary>
        public const SetId LaunchExpansion = SetId.EMBERFALL;

        /// <summary>Packs handed over with the grant.</summary>
        public const int StarterPackCount = 5;

        /// <summary>Once-per-account crafting trial credit, separate from the original card/pack batch.</summary>
        public const int StarterShardCount = 50;

        /// <summary>
        /// Cards in a starter pack — <b>8, not the 5 <see cref="BoosterPack.DefaultCardCount"/> Sam
        /// locked for the retail pack on 2026-08-02</b>. That lock is not touched: this is a separate
        /// pack built for one moment, and <see cref="BoosterPack"/> has taken a per-pack count since
        /// it was written.
        /// </summary>
        public const int StarterPackCardCount = 8;

        /// <summary>
        /// Starter-pack odds, richer than <see cref="PackRarityOdds.Default"/> (715/230/45/10).
        ///
        /// <para><b>Why they are boosted at all.</b> At retail odds the whole grant yields 0.2
        /// legendaries across 25 cards, so a new player almost certainly opens none — the worst
        /// possible version of the moment the packs exist to create. Over 40 cards these odds give
        /// roughly 22 commons, 13.6 rares, 3.4 epics and 1.0 legendaries: a legendary in expectation.</para>
        ///
        /// <para>Weight moves off COMMON deliberately. The card half of this same grant means the player
        /// already holds one of every common in the set, so a common slot can only ever top them
        /// up toward the deck limit — real value, but never a discovery.</para>
        ///
        /// <para><b>OPEN, and these odds have NOT been re-tuned for it (2026-09-02).</b> The card half
        /// now grants every RARE as well, so the same argument applies to the 340 weight on RARE: a
        /// rare slot is now a top-up too, and only EPIC and LEGENDARY are still discoveries. Whether
        /// to shift weight further up is a design call, not a mechanical consequence, so it is raised
        /// here rather than taken.</para>
        ///
        /// <para><b>Contained to onboarding.</b> Live pack odds are untouched:
        /// <see cref="BoosterPack.All"/> still builds retail packs on the default odds.</para>
        /// </summary>
        public static readonly PackRarityOdds StarterOdds = new PackRarityOdds(550, 340, 85, 25);

        /// <summary>
        /// The pack a starter pack is: launch pairing, <see cref="StarterPackCardCount"/> cards,
        /// boosted odds — and its own <see cref="BoosterPack.StarterSku"/> entitlement item,
        /// <c>pack.emberfall.starter</c>.
        ///
        /// <para><b>The sku is load-bearing, not cosmetic.</b> The ledger reserves and debits by
        /// <see cref="BoosterPack.Id"/> while the opener takes the card count and odds from its caller,
        /// so sharing an id with the retail pack would make a granted pack and a bought pack one
        /// balance — and would let a retail entitlement be opened at these boosted odds. RETIREMENT
        /// TRIGGER: if the starter pack is ever put on sale, it is this separation that has to be
        /// reconsidered, not the odds.</para>
        /// </summary>
        public static BoosterPack StarterPack()
            => new BoosterPack(LaunchExpansion, StarterPackCardCount, StarterOdds, BoosterPack.StarterSku);

        /// <summary>
        /// Every card id the card half grants one copy of — <b>every collectible COMMON and RARE in
        /// the launch pairing</b>, sorted by id.
        ///
        /// <para><b>Rares were added 2026-09-02 (Sam), and the reason is deck legality.</b> Commons
        /// alone leave <b>7 of the 28 pairings unable to field a legal 30</b> — measured against the
        /// live card data, range 27–35, with CLOCKWRIGHT+STONEWARDEN at 28. That is not a corner
        /// case: a new player on one of those pairings puts every card they own into their first deck
        /// and is still short, with nothing telling them why. Commons plus rares moves the range to
        /// <b>45–60 with no pairing under 30</b>, which is margin rather than a near miss.</para>
        ///
        /// <para><b>This is what the packs were wrongly assumed to cover.</b> The claim used to be
        /// that the commons half "covers 22–29 of a 30-card deck … so the first deck does not need
        /// the packs to be legal" — a sentence that contradicts itself, since 22–29 is short of 30.
        /// The packs were in fact load-bearing for legality on a quarter of all pairings, and they
        /// arrive as an unopened BALANCE the player may not have opened yet, so the first deck could
        /// be unbuildable through no fault of the player. Legality now comes from the card half,
        /// which is granted outright; the packs are genuinely spice again.</para>
        ///
        /// <para><b>Sorted by id</b>, for the same reason <see cref="BoosterPack.Pool"/> sorts:
        /// <see cref="ICardDatabase.AllCollectible"/> promises no order, and a grant whose order
        /// depends on a dictionary walk writes a different <c>owned-cards.json</c> on two machines
        /// that own identical collections.</para>
        ///
        /// <para>Membership is <see cref="BoosterPack.Contains"/> on the starter pack, not a literal
        /// set list, so the cards granted and the pool the packs draw from are the same set by
        /// construction.</para>
        /// </summary>
        public static IReadOnlyList<int> CardGrant(ICardDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var pack = StarterPack();
            var ids = new List<int>();
            foreach (var card in db.AllCollectible())
                if (card != null && IsGranted(card.Rarity) && pack.Contains(card.SetId))
                    ids.Add(card.Id);

            ids.Sort();
            return ids;
        }

        /// <summary>The rarities the card half grants outright. EPIC and LEGENDARY are deliberately
        /// NOT here — they are what the packs are for, and a grant that handed them over would leave
        /// a pack with nothing left to be exciting about.</summary>
        private static bool IsGranted(Rarity rarity)
            => rarity == Rarity.COMMON || rarity == Rarity.RARE;

        /// <summary>
        /// The whole grant, resolved: the commons to add and the packs to open, in one value, so a
        /// caller cannot land half of it. See the type remarks on why there is no per-step grant.
        /// </summary>
        public static OnboardingGrantPlan Resolve(ICardDatabase db)
        {
            var packs = new List<BoosterPack>(StarterPackCount);
            for (int i = 0; i < StarterPackCount; i++) packs.Add(StarterPack());
            return new OnboardingGrantPlan(CardGrant(db), packs);
        }

        /// <summary>
        /// Apply the card half to a counts record — one copy each, <b>added to</b> whatever is
        /// there rather than assigned, so calling it against a record that already holds copies cannot
        /// silently reduce a player's collection.
        /// </summary>
        public static void ApplyGrantedCards(OnboardingGrantPlan plan, OwnedCardCounts owned)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (owned == null) throw new ArgumentNullException(nameof(owned));

            var ids = plan.GrantedCardIds;
            for (int i = 0; i < ids.Count; i++)
                owned.Set(ids[i], owned.CopiesOwned(ids[i]) + 1);
        }
    }

    /// <summary>The resolved onboarding grant: what to add, and what to open.</summary>
    public sealed class OnboardingGrantPlan
    {
        /// <summary>One copy of each collectible common and rare in the pack's set, sorted by id.</summary>
        public IReadOnlyList<int> GrantedCardIds { get; }

        /// <summary>The starter packs, unopened. The caller supplies the seed and the pity state.</summary>
        public IReadOnlyList<BoosterPack> Packs { get; }

        public OnboardingGrantPlan(IReadOnlyList<int> grantedCardIds, IReadOnlyList<BoosterPack> packs)
        {
            GrantedCardIds = grantedCardIds ?? Array.Empty<int>();
            Packs = packs ?? Array.Empty<BoosterPack>();
        }

        /// <summary>Cards granted outright, before a single pack is opened.</summary>
        public int GrantedCardCount => GrantedCardIds.Count;

        /// <summary>Cards the packs will yield in total.</summary>
        public int PackCardCount
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < Packs.Count; i++) sum += Packs[i].CardCount;
                return sum;
            }
        }
    }
}
