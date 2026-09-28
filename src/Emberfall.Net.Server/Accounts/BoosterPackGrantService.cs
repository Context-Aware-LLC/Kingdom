using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>Whether this call resolved the pack or found it already resolved.</summary>
    public enum PackOpenStatus
    {
        /// <summary>This call opened the pack; every award below was granted by it.</summary>
        Opened,

        /// <summary>
        /// The pack instance was already fully opened. <b>Nothing was granted and no counter moved.</b>
        /// The awards are reconstructed from the ledger — see <see cref="PackOpenOutcome.Awards"/> for
        /// what survives that round trip.
        /// </summary>
        AlreadyOpened,
    }

    /// <summary>The result of asking the server to open one pack instance.</summary>
    public sealed class PackOpenOutcome
    {
        public PackOpenStatus Status { get; }
        public string PackId { get; }
        public string PackInstanceId { get; }

        /// <summary>
        /// What the player got, in slot order.
        ///
        /// <para>On <see cref="PackOpenStatus.AlreadyOpened"/> these are read back out of the ledger, so
        /// the card ids, rarities and shard totals are exact but <see cref="PackSlotAward.Upgrade"/> is
        /// always <see cref="PackUpgrade.None"/> — which guarantee fired is reveal chrome, not an
        /// entitlement, and the ledger deliberately does not record it.</para>
        /// </summary>
        public IReadOnlyList<PackSlotAward> Awards { get; }

        /// <summary>The pity counters after this open. Unchanged from before on a replay.</summary>
        public PackPityState Pity { get; }

        public PackOpenOutcome(PackOpenStatus status, string packId, string packInstanceId,
                               IReadOnlyList<PackSlotAward> awards, PackPityState pity)
        {
            Status = status;
            PackId = packId;
            PackInstanceId = packInstanceId;
            Awards = awards ?? Array.Empty<PackSlotAward>();
            Pity = pity;
        }

    }

    /// <summary>
    /// Binds the pure <see cref="BoosterPackOpener"/> to the append-only entitlement ledger (EF-072) —
    /// the server-authoritative half of NOR-109. A client asks to open a pack instance; this decides
    /// what is in it and writes the grants. The client renders the answer and never computes one
    /// (ADR-0004).
    ///
    /// <para><b>Exactly-once, by an atomic reservation plus write order.</b> There is no transaction
    /// spanning the ledger and the pity store, so the ordering below is doing that job and is not
    /// arbitrary:</para>
    /// <list type="number">
    ///   <item>atomically reserve one unopened pack entitlement for this instance;</item>
    ///   <item>project ownership from the ledger, <b>excluding this pack instance's own rows</b>;</item>
    ///   <item>roll (pure);</item>
    ///   <item>write the per-slot grant rows, each idempotent on its own derived transaction id;</item>
    ///   <item>write the completion marker;</item>
    ///   <item>advance the pity counters.</item>
    /// </list>
    /// <para>Step 1 prevents two server processes from spending one pack entitlement. Because the
    /// reservation is replay-safe, a crash leaves durable evidence that only this instance may resume.
    /// Ownership then ignores the rows this very instance wrote, so a crash during the slot grants
    /// re-rolls to a <b>bit-identical</b> result on the next attempt (same
    /// seed, same index, same ownership, same pity), the rows already written come back
    /// <see cref="GrantOutcome.Duplicate"/>, and the missing ones land. The open is therefore
    /// resumable, not merely non-duplicating.</para>
    ///
    /// <para>The completion marker is written <b>after</b> the slots, so its presence means "this pack is
    /// fully granted" and is what <see cref="PackOpenStatus.AlreadyOpened"/> keys off. One window
    /// remains, and it is stated rather than papered over: a crash between step 5 and step 6 loses a
    /// single pity tick. It cannot double-grant and it cannot lose a card; it can only make one pack not
    /// count toward a pity clock. Closing it needs the two stores under one transaction, which is a
    /// store-binding concern (NOR-107b), not an algorithm one.</para>
    ///
    /// <para><b>Concurrency.</b> The ledger's per-player mutation lock spans reservation, grants, and
    /// the pity update. The in-memory adapter uses a semaphore; Postgres uses a session advisory lock,
    /// so different server processes cannot concurrently roll the same or different instances for one
    /// player. A dead process releases the database session and a retry can resume from the ledger.</para>
    /// </summary>
    public sealed class BoosterPackGrantService
    {
        /// <summary>Server-owned prefix for instance ids minted from durable open-count state.</summary>
        public const string OpenInstancePrefix = "open:";

        /// <summary>The largest player-requested batch. Keeps one mutation-lock hold bounded.</summary>
        public const int MaxBatchCount = 10;

        /// <summary>
        /// Ledger item prefix for a card entitlement: <c>card:1159</c> — the NUMERIC card id, which is
        /// the only key a card has. (The handle-shaped example this comment used to give predates the
        /// numeric-id migration; nothing has ever written one.)
        /// </summary>
        public const string CardItemPrefix = "card:";

        /// <summary>Ledger <c>source</c> for every row this service writes.</summary>
        public const string GrantSource = "pack_open";

        /// <summary>Every transaction id this service derives starts here, keyed by pack instance.</summary>
        public const string TxnPrefix = "packopen:";

        /// <summary>Suffix for the atomic ledger transaction that reserves an unopened pack.</summary>
        public const string ReservationSuffix = ":reserve";

        /// <summary>Ledger item prefix for the durable "fully opened" marker.</summary>
        public const string OpenedItemPrefix = "pack_opened:";

        /// <summary>Ledger item prefix for the durable seed row: <c>pack_seed:pack.emberfall</c>.</summary>
        public const string SeedItemPrefix = "pack_seed:";

        /// <summary>Transaction id suffix for the seed row: <c>packopen:{instanceId}:seed</c>.</summary>
        public const string SeedSuffix = ":seed";

        /// <summary>
        /// Source prefix for the seed row. The pack index (the value of <see cref="PackPityState.PacksOpened"/>
        /// at roll time) and then the unsigned seed follow, colon-separated:
        /// <c>pack_open_seed:7:14421760983122550784</c>. Read them back with
        /// <see cref="TryReadSeedRow"/>.
        ///
        /// <para><b>The seed lives in this string and NEVER in <see cref="EntitlementGrant.Qty"/>.</b>
        /// A qty is a quantity: the ledger's whole projection is <c>sum(qty) group by item</c>, and
        /// every seed row on a pack line shares one item key. A 64-bit seed parked there summed with
        /// the next one, and Postgres answers <c>sum</c> of a <c>bigint</c> as unbounded
        /// <c>numeric</c> — so the sum left <see cref="long"/> after a handful of opens and
        /// <c>GET /collection</c> began throwing <c>OverflowException</c> for that player, forever.
        /// It cost Sam his onboarding gift on the fifth pack (2026-09-04, live on Fly): the shelf
        /// 500'd, the client rendered it as "this server has no pack economy", and the account could
        /// not read its collection again. Nothing that is not a countable quantity goes in
        /// <see cref="EntitlementGrant.Qty"/>.</para>
        /// </summary>
        public const string SeedSourcePrefix = "pack_open_seed:";

        /// <summary>Every seed row carries this quantity. One row per open, so the balance of a
        /// <see cref="SeedItemPrefix"/> item is a count of opens — small, bounded, and summable.</summary>
        public const long SeedRowQty = 1;

        /// <summary>
        /// Read a seed row's <see cref="EntitlementGrant.Source"/> back into the two values a disputed
        /// open is re-rolled from. False when the string is not one this service wrote.
        /// </summary>
        public static bool TryReadSeedRow(string? source, out ulong seed, out int packIndex)
        {
            seed = 0;
            packIndex = 0;
            if (source is null) return false;
            if (!source.StartsWith(SeedSourcePrefix, StringComparison.Ordinal)) return false;

            string tail = source.Substring(SeedSourcePrefix.Length);
            int split = tail.IndexOf(':');
            if (split <= 0) return false;

            return int.TryParse(tail.Substring(0, split), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out packIndex)
                && ulong.TryParse(tail.Substring(split + 1), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out seed);
        }

        private readonly IEntitlementLedger _ledger;
        private readonly IPackPityStore _pity;
        private readonly IPackSeedSource _seeds;
        private readonly ICardDatabase _cards;
        private readonly ITimeSource _time;

        public BoosterPackGrantService(IEntitlementLedger ledger, IPackPityStore pity,
                                       IPackSeedSource seeds, ICardDatabase cards, ITimeSource time)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _pity = pity ?? throw new ArgumentNullException(nameof(pity));
            _seeds = seeds ?? throw new ArgumentNullException(nameof(seeds));
            _cards = cards ?? throw new ArgumentNullException(nameof(cards));
            _time = time ?? throw new ArgumentNullException(nameof(time));
        }

        /// <summary>
        /// Open one pack instance for one player.
        ///
        /// <para><paramref name="packInstanceId"/> identifies the <i>bought pack</i>, not the pack line —
        /// it is the idempotency anchor for the whole operation, so it must be minted server-side by
        /// whatever granted the pack (a store receipt id in NOR-107b). <b>Never accept it from the
        /// client:</b> a client that chooses it can replay one purchase into an unbounded number of
        /// opens by varying it, or grief another player by colliding with theirs.</para>
        /// </summary>
        public async Task<PackOpenOutcome> OpenAsync(Account account, BoosterPack pack, string packInstanceId,
                                                     CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (string.IsNullOrEmpty(packInstanceId))
                throw new ArgumentException(
                    "A pack instance id is required; it is the idempotency key for the whole open.",
                    nameof(packInstanceId));
            EconomyAccountPolicy.RequireDurable(account);

            await using IAsyncDisposable gate =
                await _ledger.AcquirePlayerMutationLockAsync(account.PlayerId, ct).ConfigureAwait(false);
            return await OpenLockedAsync(account.PlayerId, pack, packInstanceId, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Open several owned packs while paying for one per-player mutation lock. Instance ids are
        /// minted inside that lock from the completion-marker balance, so every item in the result is
        /// distinct and ordered even when another device is also trying to open this pack line.
        /// Returns fewer than requested when the shelf became stale, and an empty list when none
        /// remain.
        /// </summary>
        public async Task<IReadOnlyList<PackOpenOutcome>> OpenManyAsync(
            Account account, BoosterPack pack, int count, CancellationToken ct = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (count < 1 || count > MaxBatchCount)
                throw new ArgumentOutOfRangeException(nameof(count),
                    $"Pack batch count must be between 1 and {MaxBatchCount}.");
            EconomyAccountPolicy.RequireDurable(account);

            await using IAsyncDisposable gate =
                await _ledger.AcquirePlayerMutationLockAsync(account.PlayerId, ct).ConfigureAwait(false);

            long owned = await _ledger.BalanceAsync(account.PlayerId, pack.Id, ct).ConfigureAwait(false);
            if (owned <= 0) return Array.Empty<PackOpenOutcome>();

            long completed = await _ledger
                .BalanceAsync(account.PlayerId, OpenedItemPrefix + pack.Id, ct)
                .ConfigureAwait(false);
            if (completed < 0) completed = 0;

            int openCount = (int)Math.Min(owned, count);
            var outcomes = new List<PackOpenOutcome>(openCount);
            for (int i = 0; i < openCount; i++)
            {
                string instanceId = OpenInstancePrefix + account.PlayerId + ":" + pack.Id + ":" +
                    (completed + i).ToString(CultureInfo.InvariantCulture);
                outcomes.Add(await OpenLockedAsync(account.PlayerId, pack, instanceId, ct)
                    .ConfigureAwait(false));
            }
            return outcomes;
        }

        private async Task<PackOpenOutcome> OpenLockedAsync(string playerId, BoosterPack pack,
                                                            string packInstanceId, CancellationToken ct)
        {
            string instanceTxnPrefix = TxnPrefix + packInstanceId + ":";
            string completionTxnId = TxnPrefix + packInstanceId;

            var history = await _ledger.HistoryAsync(playerId, ct).ConfigureAwait(false);

            // Already fully granted? The completion row is written last, so its presence is the only
            // honest "this pack is finished" signal.
            if (Contains(history, completionTxnId))
            {
                var replayed = ReconstructAwards(history, instanceTxnPrefix, pack.CardCount);
                var currentPity = await _pity.LoadAsync(playerId, pack.Id, ct).ConfigureAwait(false);
                return new PackOpenOutcome(PackOpenStatus.AlreadyOpened, pack.Id, packInstanceId, replayed, currentPity);
            }

            var reservation = new EntitlementBatch(
                playerId,
                TxnPrefix + packInstanceId + ReservationSuffix,
                new[] { new EntitlementDelta(pack.Id, -1, GrantSource) },
                new EntitlementBalanceRequirement(pack.Id, 1),
                DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs));
            EntitlementBatchResult reserved =
                await _ledger.ApplyBatchAsync(reservation, ct).ConfigureAwait(false);
            if (reserved.Conflict)
                throw new InvalidOperationException(
                    "The pack instance id is already bound to a different entitlement reservation.");
            if (reserved.InsufficientBalance)
                throw new InvalidOperationException(
                    "The player does not own an unopened entitlement for this pack.");

            history = await _ledger.HistoryAsync(playerId, ct).ConfigureAwait(false);
            var pity = await _pity.LoadAsync(playerId, pack.Id, ct).ConfigureAwait(false);
            ulong seed = await _seeds.SeedAsync(playerId, pack.Id, ct).ConfigureAwait(false);

            // Ownership as of BEFORE this instance — see the type remarks. Excluding this instance's own
            // rows is what makes a half-written open re-roll identically instead of drifting.
            var owned = ProjectOwnership(history, instanceTxnPrefix);

            int packIndex = pity.PacksOpened;
            var result = BoosterPackOpener.Open(pack, pack.Pool(_cards), owned, pity, seed, packIndex);

            var now = DateTimeOffset.FromUnixTimeMilliseconds(_time.NowMs);

            // 3a — record the seed and pack index so a disputed open can be re-rolled from the ledger.
            //      Written before the slot rows so it is present whenever any slot is durable.
            //
            //      BOTH VALUES GO IN `source`, and the qty is a plain 1 — see SeedSourcePrefix for
            //      what putting a 64-bit seed in a summed column did to a live account.
            await _ledger.GrantAsync(
                new EntitlementGrant(
                    playerId,
                    SeedItemPrefix + pack.Id,
                    SeedRowQty,
                    SeedSourcePrefix + packIndex.ToString(CultureInfo.InvariantCulture)
                        + ":" + seed.ToString(CultureInfo.InvariantCulture),
                    instanceTxnPrefix + "seed",
                    now),
                ct).ConfigureAwait(false);

            // 3b — the slots. Each carries its own derived key, so a partial write resumes cleanly.
            for (int slot = 0; slot < result.Awards.Count; slot++)
            {
                var award = result.Awards[slot];
                var grant = new EntitlementGrant(playerId, CardItemPrefix + award.CardId, 1,
                                                 GrantSource, instanceTxnPrefix + slot, now);
                await _ledger.GrantAsync(grant, ct).ConfigureAwait(false);
            }

            // 5 — mark completion only after every slot is durable. The pack itself was atomically
            // reserved before the roll, so this row is an audit/replay marker rather than another debit.
            await _ledger.GrantAsync(
                new EntitlementGrant(
                    playerId,
                    OpenedItemPrefix + pack.Id,
                    1,
                    GrantSource,
                    completionTxnId,
                    now),
                ct).ConfigureAwait(false);

            // 6 — advance the clocks.
            await _pity.SaveAsync(playerId, pack.Id, result.PityAfter, ct).ConfigureAwait(false);

            return new PackOpenOutcome(PackOpenStatus.Opened, pack.Id, packInstanceId, result.Awards, result.PityAfter);
        }

        // ── ledger projections ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Copies of each card the player holds, from the signed sum of their <c>card:</c> grants —
        /// the same projection rule the ledger uses for any balance (a refund is a negative grant).
        /// Rows belonging to <paramref name="excludeTxnPrefix"/> are ignored; see the type remarks.
        /// </summary>
        private static OwnedCardCounts ProjectOwnership(IReadOnlyList<EntitlementGrant> history, string excludeTxnPrefix)
        {
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < history.Count; i++)
            {
                var row = history[i];
                if (row.Item == null || !row.Item.StartsWith(CardItemPrefix, StringComparison.Ordinal)) continue;
                if (row.ExternalTxnId != null && row.ExternalTxnId.StartsWith(excludeTxnPrefix, StringComparison.Ordinal)) continue;

                // The ledger item is "card:<number>". A row whose suffix is not a number is a
                // pre-migration grant carrying a handle; it is SKIPPED rather than guessed at, so a
                // stale row cannot silently award a different card than it recorded.
                if (!int.TryParse(row.Item.Substring(CardItemPrefix.Length), out int cardId)) continue;
                counts[cardId] = (counts.TryGetValue(cardId, out int n) ? n : 0) + (int)row.Qty;
            }
            return new OwnedCardCounts(counts);
        }

        /// <summary>
        /// Rebuild an already-granted pack's awards from its ledger rows, in slot order. Rarity comes
        /// from the card database rather than the ledger — the ledger records what a player owns, and a
        /// card's rarity is the card's, not the grant's.
        /// </summary>
        private IReadOnlyList<PackSlotAward> ReconstructAwards(IReadOnlyList<EntitlementGrant> history,
                                                               string instanceTxnPrefix, int cardCount)
        {
            var bySlot = new Dictionary<int, EntitlementGrant>();
            for (int i = 0; i < history.Count; i++)
            {
                var row = history[i];
                if (row.ExternalTxnId == null || !row.ExternalTxnId.StartsWith(instanceTxnPrefix, StringComparison.Ordinal)) continue;
                if (!int.TryParse(row.ExternalTxnId.Substring(instanceTxnPrefix.Length), out int slot)) continue;
                bySlot[slot] = row;
            }

            var awards = new List<PackSlotAward>(cardCount);
            for (int slot = 0; slot < cardCount; slot++)
            {
                if (!bySlot.TryGetValue(slot, out var row)) continue;

                // Every slot is a card row since the no-auto-sharding ruling (Sam, 2026-08-12), so there
                // is no shard-row arm here and no card id to recover out of a grant's source string.
                if (row.Item != null && row.Item.StartsWith(CardItemPrefix, StringComparison.Ordinal))
                {
                    if (!int.TryParse(row.Item.Substring(CardItemPrefix.Length), out int cardId)) continue;
                    awards.Add(new PackSlotAward(cardId, RarityOf(cardId), PackUpgrade.None));
                }
            }
            return awards;
        }

        private Rarity RarityOf(int cardId)
            => cardId > 0 && _cards.TryGet(cardId, out CardDefinition def) ? def.Rarity : Rarity.COMMON;

        // DupeSource / DupeCardIdFromSource lived here to attribute a shard row to the card that
        // dissolved into it. A pack no longer dissolves anything (Sam, 2026-08-12), so a pack open writes
        // exactly one row shape — a card grant — and the card id is in the row's item.

        private static bool Contains(IReadOnlyList<EntitlementGrant> history, string txnId)
        {
            for (int i = 0; i < history.Count; i++)
                if (history[i].ExternalTxnId == txnId) return true;
            return false;
        }

    }
}
