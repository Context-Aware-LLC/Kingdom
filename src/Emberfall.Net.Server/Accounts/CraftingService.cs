using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>How a Render request resolved.</summary>
    public enum RenderStatus
    {
        /// <summary>Shards were spent and the card was minted by this call.</summary>
        Rendered,

        /// <summary>This transaction id had already been applied. <b>Nothing was spent and nothing minted.</b></summary>
        Replayed,

        /// <summary>The player cannot afford <see cref="ShardCosts"/> for this rarity. Nothing was written.</summary>
        InsufficientShards,

        /// <summary>Already at <see cref="DeckValidator.MaxCopies"/>. Nothing was written.</summary>
        AtCopyCap,

        /// <summary>No such collectible card. Nothing was written.</summary>
        NotCraftable,
    }

    /// <summary>How a Reduce request resolved.</summary>
    public enum ReduceStatus
    {
        /// <summary>A copy was destroyed and <see cref="ShardValues"/> paid out by this call.</summary>
        Reduced,

        /// <summary>This transaction id had already been applied. <b>Nothing was destroyed and nothing paid.</b></summary>
        Replayed,

        /// <summary>The player holds no copy. Nothing was written.</summary>
        OwnsNone,

        /// <summary>No such collectible card. Nothing was written.</summary>
        NotReducible,
    }

    /// <summary>
    /// The player's post-call standing for the one card a craft touched, projected off the ledger —
    /// never accumulated by the caller.
    /// </summary>
    public sealed class CraftOutcome
    {
        public string CardId { get; }

        /// <summary>Shard balance after the call.</summary>
        public long Shards { get; }

        /// <summary>Copies of <see cref="CardId"/> owned after the call.</summary>
        public int CopiesOwned { get; }

        /// <summary>
        /// Shards this call moved: negative for a Render, positive for a Reduce, <b>0 on every
        /// refusal and on a replay</b> — a replay's delta is zero because the replay wrote nothing,
        /// not because the original craft was free.
        /// </summary>
        public int ShardDelta { get; }

        public CraftOutcome(string cardId, long shards, int copiesOwned, int shardDelta)
        {
            CardId = cardId;
            Shards = shards;
            CopiesOwned = copiesOwned;
            ShardDelta = shardDelta;
        }
    }

    public sealed class RenderOutcome
    {
        public RenderStatus Status { get; }
        public CraftOutcome Craft { get; }

        public RenderOutcome(RenderStatus status, CraftOutcome craft)
        {
            Status = status;
            Craft = craft;
        }
    }

    public sealed class ReduceOutcome
    {
        public ReduceStatus Status { get; }
        public CraftOutcome Craft { get; }

        public ReduceOutcome(ReduceStatus status, CraftOutcome craft)
        {
            Status = status;
            Craft = craft;
        }
    }

    /// <summary>
    /// Both directions of crafting bound to the append-only entitlement ledger (EF-072) — the
    /// server-authoritative half of GH#157 (Render) and GH#158 (Reduce). The decision is
    /// <see cref="Crafting"/>'s, pure and shared with the client; everything here is the part that
    /// moves a balance, which only the server may do (ADR-0004).
    ///
    /// <para><b>One ledger, one authority.</b> Shards are <see cref="ShardValues.LedgerItem"/> and a
    /// card is <c>card:{id}</c> — the same two items <see cref="BoosterPackGrantService"/> writes at
    /// pack-open. A craft is therefore visible to the same balance projection as everything else, and
    /// the shard economy has one account rather than a crafting-shaped copy of one.</para>
    ///
    /// <para><b>Idempotent on a server-minted transaction id, and the pre-checks are what make that
    /// true.</b> Both calls run their guards <i>only</i> on the first sighting of a transaction id. A
    /// replayed Render of the 2nd copy of a common would otherwise find the player at the copy cap —
    /// because the first attempt put them there — and report <see cref="RenderStatus.AtCopyCap"/> for
    /// a craft that in fact succeeded. The guards answer "may this happen", so they must not run
    /// against a world in which it already has; the ledger's own duplicate detection answers the rest.
    /// A transaction id reused for a <i>different</i> movement is a conflict and throws, which is why
    /// Render and Reduce share one <see cref="TxnPrefix"/> namespace: submitting the same id in both
    /// directions is caught rather than quietly applied twice.</para>
    ///
    /// <para><b>Never accept the transaction id from the client</b> — the same rule as a pack instance
    /// id. A client that chooses it can replay one confirmed craft into an unbounded number of mints
    /// by varying it.</para>
    ///
    /// <para><b>Nothing here confirms a Reduce.</b> It is destructive and player-initiated, so the
    /// surface confirms before it calls (GH#158) using <c>Crafting.OfferReduce</c> and
    /// <see cref="Crafting.DeckImpact"/>. A server-side confirmation flag would be a second authority
    /// on a UI decision and would be trivially settable by anything that reached this far.</para>
    ///
    /// <para><b>One stated edge:</b> a replay derives its deltas from <i>today's</i> price, so a
    /// <see cref="ShardCosts"/> retune landing between a craft and its retry makes the retry a
    /// conflict rather than a replay, and it throws. That is the ledger's own rule — one transaction
    /// id means one exact set of movements — and it fails loudly on a window seconds wide rather than
    /// silently accepting two different prices under one id.</para>
    /// </summary>
    public sealed class CraftingService
    {
        /// <summary>Every transaction id this service derives starts here — one namespace for both directions.</summary>
        public const string TxnPrefix = "craft:";

        /// <summary>Ledger <c>source</c> for the two rows a Render writes.</summary>
        public const string RenderSource = "craft_render";

        /// <summary>Ledger <c>source</c> for the two rows a Reduce writes.</summary>
        public const string ReduceSource = "craft_reduce";

        private readonly IEntitlementLedger _ledger;
        private readonly ICardDatabase _cards;
        private readonly ITimeSource _time;

        public CraftingService(IEntitlementLedger ledger, ICardDatabase cards, ITimeSource time)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _cards = cards ?? throw new ArgumentNullException(nameof(cards));
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        /// <summary>
        /// Spend <see cref="ShardCosts"/> and mint one copy of <paramref name="cardId"/> (GH#157).
        /// </summary>
        public async Task<RenderOutcome> RenderAsync(
            Account account,
            string cardId,
            string serverTransactionId,
            CancellationToken ct = default)
        {
            RequireCraftArguments(account, cardId, serverTransactionId);

            await using IAsyncDisposable gate =
                await _ledger.AcquirePlayerMutationLockAsync(account.PlayerId, ct).ConfigureAwait(false);

            CardDefinition? def = Resolve(cardId);
            string txnId = TxnPrefix + serverTransactionId;
            bool replaying = await AlreadyAppliedAsync(account.PlayerId, txnId, ct).ConfigureAwait(false);

            if (!replaying)
            {
                long shards = await _ledger.BalanceAsync(account.PlayerId, ShardValues.LedgerItem, ct).ConfigureAwait(false);
                int owned = await CopiesOwnedAsync(account.PlayerId, cardId, ct).ConfigureAwait(false);
                RenderOffer offer = Crafting.OfferRender(def, owned, shards);
                if (!offer.Offerable)
                    return new RenderOutcome(
                        offer.Refusal switch
                        {
                            RenderRefusal.AtCopyCap => RenderStatus.AtCopyCap,
                            RenderRefusal.InsufficientShards => RenderStatus.InsufficientShards,
                            _ => RenderStatus.NotCraftable,
                        },
                        await ProjectAsync(account.PlayerId, cardId, 0, ct).ConfigureAwait(false));
            }
            else if (def == null)
            {
                // A replay whose card no longer resolves: the rows are already durable, so report the
                // balances rather than a refusal for a craft that demonstrably happened.
                return new RenderOutcome(
                    RenderStatus.Replayed,
                    await ProjectAsync(account.PlayerId, cardId, 0, ct).ConfigureAwait(false));
            }

            int cost = ShardCosts.ForRarity(def!.Rarity);
            var batch = new EntitlementBatch(
                account.PlayerId,
                txnId,
                new List<EntitlementDelta>
                {
                    new EntitlementDelta(ShardValues.LedgerItem, -cost, RenderSource),
                    new EntitlementDelta(BoosterPackGrantService.CardItemPrefix + cardId, 1, RenderSource),
                },
                new EntitlementBalanceRequirement(ShardValues.LedgerItem, cost),
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));

            EntitlementBatchResult applied = await _ledger.ApplyBatchAsync(batch, ct).ConfigureAwait(false);
            if (applied.Conflict)
                throw new InvalidOperationException(
                    "The craft transaction id is already bound to a different set of entitlement movements.");

            RenderStatus status = applied.Applied
                ? RenderStatus.Rendered
                : applied.Duplicate
                    ? RenderStatus.Replayed
                    : RenderStatus.InsufficientShards;
            int delta = applied.Applied ? -cost : 0;
            return new RenderOutcome(status, await ProjectAsync(account.PlayerId, cardId, delta, ct).ConfigureAwait(false));
        }

        /// <summary>
        /// Destroy one copy of <paramref name="cardId"/> and pay <see cref="ShardValues"/> (GH#158).
        /// The payout table is LOCKED (Sam, 2026-08-02, NOR-109) — it is read here, never re-priced.
        /// </summary>
        public async Task<ReduceOutcome> ReduceAsync(
            Account account,
            string cardId,
            string serverTransactionId,
            CancellationToken ct = default)
        {
            RequireCraftArguments(account, cardId, serverTransactionId);

            await using IAsyncDisposable gate =
                await _ledger.AcquirePlayerMutationLockAsync(account.PlayerId, ct).ConfigureAwait(false);

            CardDefinition? def = Resolve(cardId);
            string txnId = TxnPrefix + serverTransactionId;
            bool replaying = await AlreadyAppliedAsync(account.PlayerId, txnId, ct).ConfigureAwait(false);

            if (!replaying)
            {
                int owned = await CopiesOwnedAsync(account.PlayerId, cardId, ct).ConfigureAwait(false);
                ReduceOffer offer = Crafting.OfferReduce(def, owned);
                if (!offer.Offerable)
                    return new ReduceOutcome(
                        offer.Refusal == ReduceRefusal.OwnsNone ? ReduceStatus.OwnsNone : ReduceStatus.NotReducible,
                        await ProjectAsync(account.PlayerId, cardId, 0, ct).ConfigureAwait(false));
            }
            else if (def == null)
            {
                return new ReduceOutcome(
                    ReduceStatus.Replayed,
                    await ProjectAsync(account.PlayerId, cardId, 0, ct).ConfigureAwait(false));
            }

            int payout = ShardValues.ForRarity(def!.Rarity);
            string cardItem = BoosterPackGrantService.CardItemPrefix + cardId;
            var batch = new EntitlementBatch(
                account.PlayerId,
                txnId,
                new List<EntitlementDelta>
                {
                    new EntitlementDelta(cardItem, -1, ReduceSource),
                    new EntitlementDelta(ShardValues.LedgerItem, payout, ReduceSource),
                },
                new EntitlementBalanceRequirement(cardItem, 1),
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));

            EntitlementBatchResult applied = await _ledger.ApplyBatchAsync(batch, ct).ConfigureAwait(false);
            if (applied.Conflict)
                throw new InvalidOperationException(
                    "The craft transaction id is already bound to a different set of entitlement movements.");

            ReduceStatus status = applied.Applied
                ? ReduceStatus.Reduced
                : applied.Duplicate
                    ? ReduceStatus.Replayed
                    : ReduceStatus.OwnsNone;
            int delta = applied.Applied ? payout : 0;
            return new ReduceOutcome(status, await ProjectAsync(account.PlayerId, cardId, delta, ct).ConfigureAwait(false));
        }

        /// <summary>
        /// What the collection screen needs to draw the two buttons for one card, without asking it to
        /// re-derive a price or a cap: the same <see cref="Crafting"/> predicates the mutating calls
        /// above run, evaluated against the authoritative ledger.
        ///
        /// <para>Advisory by nature — it is a read, so a concurrent craft can invalidate it before the
        /// player clicks. That is safe: <see cref="RenderAsync"/> and <see cref="ReduceAsync"/>
        /// re-evaluate under the mutation lock and are the only authority.</para>
        /// </summary>
        public async Task<(RenderOffer Render, ReduceOffer Reduce)> OffersAsync(
            Account account,
            string cardId,
            CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (string.IsNullOrWhiteSpace(cardId)) throw new ArgumentException("A card id is required.", nameof(cardId));

            CardDefinition? def = Resolve(cardId);
            long shards = await _ledger.BalanceAsync(account.PlayerId, ShardValues.LedgerItem, ct).ConfigureAwait(false);
            int owned = await CopiesOwnedAsync(account.PlayerId, cardId, ct).ConfigureAwait(false);
            return (Crafting.OfferRender(def, owned, shards), Crafting.OfferReduce(def, owned));
        }

        /// <summary>The player's shard balance — the number the collection screen has never had (GH#148).</summary>
        public Task<long> ShardBalanceAsync(Account account, CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            return _ledger.BalanceAsync(account.PlayerId, ShardValues.LedgerItem, ct);
        }

        private static void RequireCraftArguments(Account account, string cardId, string serverTransactionId)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (string.IsNullOrWhiteSpace(cardId))
                throw new ArgumentException("A card id is required.", nameof(cardId));
            if (string.IsNullOrWhiteSpace(serverTransactionId))
                throw new ArgumentException(
                    "A server-minted craft transaction id is required; it is the idempotency key.",
                    nameof(serverTransactionId));
            EconomyAccountPolicy.RequireDurable(account);
        }

        private CardDefinition? Resolve(string cardId)
            => _cards.TryGet(cardId, out CardDefinition def) ? def : null;

        /// <summary>
        /// Has any row of this batch already landed? <see cref="EntitlementBatch"/> derives its row
        /// keys as <c>{TransactionId}:{index}</c>, so the prefix is what identifies the batch — the
        /// batch id itself is never an <c>ExternalTxnId</c> on any row.
        /// </summary>
        private async Task<bool> AlreadyAppliedAsync(string playerId, string txnId, CancellationToken ct)
        {
            string rowPrefix = txnId + ":";
            IReadOnlyList<EntitlementGrant> history =
                await _ledger.HistoryAsync(playerId, ct).ConfigureAwait(false);
            for (int i = 0; i < history.Count; i++)
            {
                string? id = history[i].ExternalTxnId;
                if (id != null && id.StartsWith(rowPrefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private async Task<int> CopiesOwnedAsync(string playerId, string cardId, CancellationToken ct)
        {
            long copies = await _ledger
                .BalanceAsync(playerId, BoosterPackGrantService.CardItemPrefix + cardId, ct)
                .ConfigureAwait(false);
            return copies <= 0 ? 0 : (int)copies;
        }

        private async Task<CraftOutcome> ProjectAsync(string playerId, string cardId, int shardDelta, CancellationToken ct)
        {
            long shards = await _ledger.BalanceAsync(playerId, ShardValues.LedgerItem, ct).ConfigureAwait(false);
            int owned = await CopiesOwnedAsync(playerId, cardId, ct).ConfigureAwait(false);
            return new CraftOutcome(cardId, shards, owned, shardDelta);
        }
    }
}
