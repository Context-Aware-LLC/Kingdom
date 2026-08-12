using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>Why a Render is not on offer. <see cref="None"/> means it is.</summary>
    public enum RenderRefusal
    {
        None,

        /// <summary>No such card id in this build's database.</summary>
        UnknownCard,

        /// <summary>A token or other non-collectible row. Nothing a player can own, so nothing to mint.</summary>
        NotCollectible,

        /// <summary>
        /// The player already holds <see cref="DeckValidator.MaxCopies"/> of it — a 3rd common or a 2nd
        /// legendary. The copy could never be played, so it is never offered (GH#157).
        /// </summary>
        AtCopyCap,

        /// <summary>Fewer shards than <see cref="ShardCosts"/> asks for this rarity.</summary>
        InsufficientShards,
    }

    /// <summary>Why a Reduce is not on offer. <see cref="None"/> means it is.</summary>
    public enum ReduceRefusal
    {
        None,
        UnknownCard,
        NotCollectible,

        /// <summary>The player holds no copy to break down.</summary>
        OwnsNone,
    }

    /// <summary>
    /// Whether one card can be Rendered right now, and the numbers the offer is drawn from.
    ///
    /// <para>The refusal reason is returned rather than a bare bool because the two refusals read
    /// differently to a player and want different surfaces: "you cannot afford this" is a price the
    /// shard balance answers, "you are at the copy limit" is permanent for that card and is the
    /// collection wall's own language. One bool would collapse them.</para>
    /// </summary>
    public readonly struct RenderOffer
    {
        public readonly RenderRefusal Refusal;

        /// <summary>Shards this Render would spend — <see cref="ShardCosts"/> for the card's rarity.</summary>
        public readonly int Cost;

        public readonly int CopiesOwned;

        /// <summary>The format cap for this rarity: 1 legendary, 2 otherwise.</summary>
        public readonly int CopyCap;

        public RenderOffer(RenderRefusal refusal, int cost, int copiesOwned, int copyCap)
        {
            Refusal = refusal;
            Cost = cost;
            CopiesOwned = copiesOwned;
            CopyCap = copyCap;
        }

        public bool Offerable => Refusal == RenderRefusal.None;
    }

    /// <summary>Whether one card can be Reduced right now, and what it would pay.</summary>
    public readonly struct ReduceOffer
    {
        public readonly ReduceRefusal Refusal;

        /// <summary>Shards this Reduce would pay out — <see cref="ShardValues"/>, LOCKED, never re-priced here.</summary>
        public readonly int Payout;

        public readonly int CopiesOwned;

        /// <summary>Copies left afterwards. <see cref="CopiesOwned"/> when the Reduce is refused.</summary>
        public readonly int CopiesAfter;

        public ReduceOffer(ReduceRefusal refusal, int payout, int copiesOwned, int copiesAfter)
        {
            Refusal = refusal;
            Payout = payout;
            CopiesOwned = copiesOwned;
            CopiesAfter = copiesAfter;
        }

        public bool Offerable => Refusal == ReduceRefusal.None;
    }

    /// <summary>
    /// One saved deck that holds the card a Reduce is about to destroy a copy of — the row the
    /// confirmation dialog is built from (GH#158).
    /// </summary>
    public readonly struct DeckCardUsage
    {
        public readonly DeckList Deck;

        /// <summary>Copies of the card this deck runs.</summary>
        public readonly int Copies;

        /// <summary>
        /// True when the deck would run more copies than the player still owns after the Reduce — the
        /// deck stays saved and stays editable, but it has just become a draft (L6). This is the fact
        /// the confirm has to state; the alternative is a player discovering it at the queue gate.
        /// </summary>
        public readonly bool BecomesUnbacked;

        public DeckCardUsage(DeckList deck, int copies, bool becomesUnbacked)
        {
            Deck = deck;
            Copies = copies;
            BecomesUnbacked = becomesUnbacked;
        }
    }

    /// <summary>
    /// The two directions of crafting, as pure predicates over a card, a copy count and a shard
    /// balance (GH#148 — Render #157, Reduce #158). No ledger, no clock, no store: the server binds
    /// these to <c>IEntitlementLedger</c> and is the only thing that may move a balance, while the
    /// collection screen calls the identical functions to decide what to draw. Two answers computed
    /// from one function cannot disagree, which is the whole point of the split — an offer the screen
    /// makes and the server then refuses is the bug this shape prevents.
    ///
    /// <para><b>Neither price lives here.</b> <see cref="ShardValues"/> (payout, LOCKED BY SAM
    /// 2026-08-02) and <see cref="ShardCosts"/> (cost, provisional) are the tables; this file reads
    /// them and never restates a number.</para>
    ///
    /// <para><b>Nothing here confirms anything.</b> Reduce is destructive and player-initiated, so it
    /// confirms — but a confirmation is a surface, and this assembly has none. What it provides is the
    /// material that confirmation is made of: the payout, the copies left, and
    /// <see cref="DeckImpact"/>.</para>
    /// </summary>
    public static class Crafting
    {
        /// <summary>
        /// Can this card be Rendered for <paramref name="shardBalance"/> given
        /// <paramref name="copiesOwned"/>?
        ///
        /// <para>Refusals are evaluated cheapest-truth-first — what the card is, then the permanent
        /// cap, then the affordable-today price — so a legendary at the cap reports
        /// <see cref="RenderRefusal.AtCopyCap"/> rather than the price it also cannot pay.</para>
        /// </summary>
        public static RenderOffer OfferRender(CardDefinition? def, int copiesOwned, long shardBalance)
        {
            if (def == null) return new RenderOffer(RenderRefusal.UnknownCard, 0, 0, 0);

            int cap = DeckValidator.MaxCopies(def.Rarity);
            int cost = ShardCosts.ForRarity(def.Rarity);
            if (copiesOwned < 0) copiesOwned = 0;

            if (!def.Collectible) return new RenderOffer(RenderRefusal.NotCollectible, cost, copiesOwned, cap);
            if (copiesOwned >= cap) return new RenderOffer(RenderRefusal.AtCopyCap, cost, copiesOwned, cap);
            if (shardBalance < cost) return new RenderOffer(RenderRefusal.InsufficientShards, cost, copiesOwned, cap);
            return new RenderOffer(RenderRefusal.None, cost, copiesOwned, cap);
        }

        /// <summary>Can this card be Reduced given <paramref name="copiesOwned"/>, and for how much?</summary>
        public static ReduceOffer OfferReduce(CardDefinition? def, int copiesOwned)
        {
            if (def == null) return new ReduceOffer(ReduceRefusal.UnknownCard, 0, 0, 0);

            int payout = ShardValues.ForRarity(def.Rarity);
            if (copiesOwned < 0) copiesOwned = 0;

            if (!def.Collectible) return new ReduceOffer(ReduceRefusal.NotCollectible, payout, copiesOwned, copiesOwned);
            if (copiesOwned <= 0) return new ReduceOffer(ReduceRefusal.OwnsNone, payout, 0, 0);
            return new ReduceOffer(ReduceRefusal.None, payout, copiesOwned, copiesOwned - 1);
        }

        /// <summary>Rarity lookup then the <c>CardDefinition</c> overload; unknown ids refuse.</summary>
        public static RenderOffer OfferRender(string cardId, ICardDatabase db, int copiesOwned, long shardBalance)
            => OfferRender(Resolve(cardId, db), copiesOwned, shardBalance);

        /// <summary>Rarity lookup then the <c>CardDefinition</c> overload; unknown ids refuse.</summary>
        public static ReduceOffer OfferReduce(string cardId, ICardDatabase db, int copiesOwned)
            => OfferReduce(Resolve(cardId, db), copiesOwned);

        /// <summary>
        /// Which saved decks hold <paramref name="cardId"/>, and which of them stop being backed once
        /// the player is down to <paramref name="copiesAfter"/> copies (GH#158).
        ///
        /// <para><b>Every deck, not just this character's.</b> A card is owned by the account and a
        /// deck is bound to a character, so a Reduce reaches decks the player is not looking at — which
        /// is exactly the case a confirmation exists to surface. Filter afterwards if a surface wants
        /// less; it cannot recover a deck this never returned.</para>
        ///
        /// <para>Stored order, like <see cref="DeckQuery.BoundTo"/>. Card-database free: this is a
        /// count against a decklist and needs no rarity.</para>
        /// </summary>
        public static IReadOnlyList<DeckCardUsage> DeckImpact(string cardId, int copiesAfter,
                                                              IEnumerable<DeckList>? decks)
        {
            var rows = new List<DeckCardUsage>();
            if (decks == null || string.IsNullOrEmpty(cardId)) return rows;
            if (copiesAfter < 0) copiesAfter = 0;

            foreach (DeckList deck in decks)
            {
                if (deck == null) continue;
                int copies = deck.CountOf(cardId);
                if (copies <= 0) continue;
                rows.Add(new DeckCardUsage(deck, copies, copies > copiesAfter));
            }
            return rows;
        }

        private static CardDefinition? Resolve(string cardId, ICardDatabase db)
            => !string.IsNullOrEmpty(cardId) && db != null && db.TryGet(cardId, out CardDefinition def) ? def : null;
    }
}
