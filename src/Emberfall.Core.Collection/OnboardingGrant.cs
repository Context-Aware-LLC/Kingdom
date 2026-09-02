using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// What a brand-new account is given, once, the first time it creates a character
    /// (<b>Sam, 2026-08-12</b>). Two halves: a copy of every collectible <b>common</b> in the launch
    /// pairing, and <see cref="StarterPackCount"/> boosted booster packs.
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
    /// <c>signup:{playerId}</c></b> (2026-09-01; spec <c>docs/dev-docs/specs/card-ownership.md</c>,
    /// slice 1). <c>OnboardingGrantService</c> writes the whole grant as one atomic entitlement batch
    /// under that id, so a replay conflicts on the derived row keys and writes nothing — the grant is
    /// attempted on every account resolve and there is deliberately no due-check for two resolves to
    /// race. A player who reduces their entire collection to shards is still never re-granted, because
    /// the ledger keeps the original grant rows forever: a zero balance is not an absent grant.</para>
    ///
    /// <para><b>This key used to be the absence of an ownership record</b>, back when the client's
    /// <c>owned-cards.json</c> was the authority — see <see cref="IOwnedCardStore"/>, whose null still
    /// means "ownership unknown" and still governs what an unsynced client shows.
    /// <see cref="IsDue"/> remains for that client-local question and is no longer what decides a
    /// grant. The crash argument is unchanged in substance and now rests on the batch rather than on
    /// write ordering: an entitlement batch is all-or-nothing, so a crash mid-grant leaves the account
    /// un-granted and the next resolve simply lands it. That is still why <see cref="Resolve"/>
    /// returns everything at once instead of a grant per step.</para>
    ///
    /// <para><b>Why the packs are not seeded to the character's classes.</b> Sam weighed both and
    /// ruled unseeded on 2026-08-12. The grant fires once per <i>account</i>, while a class pairing
    /// is a per-<i>character</i> choice, and 93% of this set's non-commons are class-locked (56/60
    /// rares, 19/21 epics, 11/11 legendaries). Seeding would therefore concentrate an account's only
    /// gift into whichever two classes a new player picked in their first minute, and every character
    /// after that would get nothing. The commons half already covers 22–29 of a 30-card deck for any
    /// pairing, so the first deck does not need the packs to be legal — they are spice, not
    /// legality.</para>
    /// </summary>
    public static class OnboardingGrant
    {
        /// <summary>
        /// The expansion half of the pairing the grant is defined against. The commons half and the
        /// starter packs both read it, so they cannot drift apart.
        ///
        /// <para>RETIREMENT TRIGGER: THE SECOND EXPANSION SHIPS. "Every common in the launch set" is a
        /// launch-shaped answer. The commit that adds a third <see cref="SetId"/> member intended for
        /// sale must decide what a new account gets then — every common in every set, or only the
        /// current one — and update this constant, the XML above, and
        /// <c>OnboardingGrantTests.GrantIsEveryCollectibleCommonInThePairing</c>
        /// (<c>tests/Emberfall.Core.Tests/Collection/OnboardingGrantTests.cs</c>). Leaving it pointing
        /// at EMBERFALL is a decision, not a default, and should be a deliberate one.</para>
        /// </summary>
        public const SetId LaunchExpansion = SetId.EMBERFALL;

        /// <summary>Packs handed over with the grant.</summary>
        public const int StarterPackCount = 5;

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
        /// <para>Weight moves off COMMON deliberately. The commons half of this same grant means the
        /// player already holds one of every common in the pairing, so a common slot can only ever top
        /// them up toward the deck limit — real value, but never a discovery.</para>
        ///
        /// <para><b>Contained to onboarding.</b> Live pack odds are untouched:
        /// <see cref="BoosterPack.All"/> still builds retail packs on the default odds.</para>
        /// </summary>
        public static readonly PackRarityOdds StarterOdds = new PackRarityOdds(550, 340, 85, 25);

        /// <summary>
        /// Does this device hold no ownership record at all? True exactly when
        /// <see cref="IOwnedCardStore.Load"/> returns null.
        ///
        /// <para><b>This is no longer what decides a grant, and must not be used as one.</b> The
        /// server owns that decision and keys it on the ledger transaction id <c>signup:{playerId}</c>
        /// (see the type remarks). What survives here is the client-local question — "has this device
        /// ever synced a collection?" — whose answer still selects between showing the full pool and
        /// constraining to a real one. RETIREMENT TRIGGER: the commit that makes the client mirror the
        /// server's projection (spec slice 3) decides whether this method has any caller left; if it
        /// does not, delete it rather than leaving a plausible-looking grant check in the collection
        /// assembly.</para>
        /// </summary>
        public static bool IsDue(IOwnedCardStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            return store.Load() == null;
        }

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
        /// Every card id the commons half grants one copy of — <b>sorted by id</b>, for the same
        /// reason <see cref="BoosterPack.Pool"/> sorts: <see cref="ICardDatabase.AllCollectible"/>
        /// promises no order, and a grant whose order depends on a dictionary walk writes a different
        /// <c>owned-cards.json</c> on two machines that own identical collections.
        ///
        /// <para>Membership is <see cref="BoosterPack.Contains"/> on the starter pack, not a literal
        /// set list, so the commons granted and the pool the packs draw from are the same two sets by
        /// construction. CORE is in the pairing, so its collectible commons are included — the CORE
        /// rows that are <i>not</i> are hero powers and tokens, which are not collectible and were
        /// never ownable.</para>
        /// </summary>
        public static IReadOnlyList<int> CommonGrant(ICardDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var pack = StarterPack();
            var ids = new List<int>();
            foreach (var card in db.AllCollectible())
                if (card != null && card.Rarity == Rarity.COMMON && pack.Contains(card.SetId))
                    ids.Add(card.Id);

            ids.Sort();
            return ids;
        }

        /// <summary>
        /// The whole grant, resolved: the commons to add and the packs to open, in one value, so a
        /// caller cannot land half of it. See the type remarks on why there is no per-step grant.
        /// </summary>
        public static OnboardingGrantPlan Resolve(ICardDatabase db)
        {
            var packs = new List<BoosterPack>(StarterPackCount);
            for (int i = 0; i < StarterPackCount; i++) packs.Add(StarterPack());
            return new OnboardingGrantPlan(CommonGrant(db), packs);
        }

        /// <summary>
        /// Apply the commons half to a counts record — one copy each, <b>added to</b> whatever is
        /// there rather than assigned, so calling it against a record that already holds copies cannot
        /// silently reduce a player's collection.
        /// </summary>
        public static void ApplyCommons(OnboardingGrantPlan plan, OwnedCardCounts owned)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (owned == null) throw new ArgumentNullException(nameof(owned));

            var ids = plan.CommonCardIds;
            for (int i = 0; i < ids.Count; i++)
                owned.Set(ids[i], owned.CopiesOwned(ids[i]) + 1);
        }
    }

    /// <summary>The resolved onboarding grant: what to add, and what to open.</summary>
    public sealed class OnboardingGrantPlan
    {
        /// <summary>One copy of each, sorted by id.</summary>
        public IReadOnlyList<int> CommonCardIds { get; }

        /// <summary>The starter packs, unopened. The caller supplies the seed and the pity state.</summary>
        public IReadOnlyList<BoosterPack> Packs { get; }

        public OnboardingGrantPlan(IReadOnlyList<int> commonCardIds, IReadOnlyList<BoosterPack> packs)
        {
            CommonCardIds = commonCardIds ?? Array.Empty<int>();
            Packs = packs ?? Array.Empty<BoosterPack>();
        }

        /// <summary>Cards granted outright, before a single pack is opened.</summary>
        public int CommonCount => CommonCardIds.Count;

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
