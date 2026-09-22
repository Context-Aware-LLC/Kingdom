using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// Where a player's booster-pack pity counters live (NOR-109 §4 — "persisted on the account").
    /// Keyed by <c>(playerId, packId)</c>, so each set pairing carries its own clocks.
    ///
    /// <para>Production derives this cursor from completed ledger opens, so a crash between the
    /// completion marker and a separate cursor write cannot lose a paid pack's pity progress.
    /// Tests may inject an in-memory cursor for isolated cases.</para>
    ///
    /// <para><b>Async because the durable implementation reads the database.</b> The in-memory store
    /// below completes synchronously; the shape matches
    /// <see cref="IAccountStore"/>/<see cref="IEntitlementLedger"/> so all three compose in one
    /// call path.</para>
    /// </summary>
    public interface IPackPityStore
    {
        /// <summary>
        /// This player's counters for this pack. A player who has never opened one is not an error —
        /// it returns <see cref="PackPityState.Fresh"/>, never null, so a caller cannot forget the case.
        /// </summary>
        Task<PackPityState> LoadAsync(string playerId, string packId, CancellationToken ct = default);

        /// <summary>Commit this player's counters where the implementation stores a cursor.
        /// Ledger-derived pity is already durable when the completion marker lands.</summary>
        Task SaveAsync(string playerId, string packId, PackPityState state, CancellationToken ct = default);
    }

    /// <summary>The in-memory pity store — the default for tests and single-box dev. Thread-safe under one lock.</summary>
    public sealed class InMemoryPackPityStore : IPackPityStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, PackPityState> _rows = new Dictionary<string, PackPityState>(StringComparer.Ordinal);

        public Task<PackPityState> LoadAsync(string playerId, string packId, CancellationToken ct = default)
        {
            string key = Key(playerId, packId);
            lock (_lock)
            {
                return Task.FromResult(_rows.TryGetValue(key, out var state) ? state : PackPityState.Fresh);
            }
        }

        public Task SaveAsync(string playerId, string packId, PackPityState state, CancellationToken ct = default)
        {
            string key = Key(playerId, packId);
            lock (_lock)
            {
                _rows[key] = state;
            }
            return Task.CompletedTask;
        }

        private static string Key(string playerId, string packId)
        {
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("playerId is required.", nameof(playerId));
            if (string.IsNullOrEmpty(packId)) throw new ArgumentException("packId is required.", nameof(packId));
            return playerId + "\n" + packId;
        }
    }

    /// <summary>
    /// Production pity projection from the durable open audit rows. Completion is written after
    /// every card grant, so a crash between completion and the old cursor's SaveAsync cannot reset
    /// pity. An incomplete open is ignored until its original instance resumes and completes.
    /// No second database transaction or schema is needed: the entitlement ledger is the record.
    /// </summary>
    public sealed class LedgerPackPityStore : IPackPityStore
    {
        private readonly IEntitlementLedger _ledger;
        private readonly ICardDatabase _cards;

        public LedgerPackPityStore(IEntitlementLedger ledger, ICardDatabase cards)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _cards = cards ?? throw new ArgumentNullException(nameof(cards));
        }

        public async Task<PackPityState> LoadAsync(string playerId, string packId,
                                                  CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(playerId)) throw new ArgumentException("Player id is required.", nameof(playerId));
            if (string.IsNullOrWhiteSpace(packId)) throw new ArgumentException("Pack id is required.", nameof(packId));

            IReadOnlyList<EntitlementGrant> history = await _ledger.HistoryAsync(playerId, ct)
                .ConfigureAwait(false);
            string completedItem = BoosterPackGrantService.OpenedItemPrefix + packId;
            string seedItem = BoosterPackGrantService.SeedItemPrefix + packId;
            var completed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (EntitlementGrant row in history)
                if (row.Item == completedItem && row.Qty > 0)
                    completed[row.ExternalTxnId] = row.CreatedAt;

            var seedIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (EntitlementGrant row in history)
            {
                if (row.Item != seedItem || !row.ExternalTxnId.EndsWith(
                    BoosterPackGrantService.SeedSuffix, StringComparison.Ordinal)) continue;
                string txn = row.ExternalTxnId.Substring(0, row.ExternalTxnId.Length -
                    BoosterPackGrantService.SeedSuffix.Length);
                if (BoosterPackGrantService.TryReadSeedRow(row.Source, out _, out int index))
                    seedIndexes[txn] = index;
            }
            // Older completed opens can predate the current seed-row encoding. Their card grants
            // still determine rarity, so they participate in pity rather than breaking the shop.
            var ordered = new List<(string Txn, DateTimeOffset At, int Index)>();
            foreach (var row in completed)
                ordered.Add((row.Key, row.Value,
                    seedIndexes.TryGetValue(row.Key, out int index) ? index : int.MaxValue));

            ordered.Sort((a, b) =>
            {
                int byTime = a.At.CompareTo(b.At);
                if (byTime != 0) return byTime;
                int byIndex = a.Index.CompareTo(b.Index);
                return byIndex != 0 ? byIndex : string.CompareOrdinal(a.Txn, b.Txn);
            });

            var slotsByOpen = new Dictionary<string, SortedDictionary<int, Rarity>>(StringComparer.Ordinal);
            foreach (EntitlementGrant row in history)
            {
                if (!row.Item.StartsWith(BoosterPackGrantService.CardItemPrefix, StringComparison.Ordinal))
                    continue;
                int separator = row.ExternalTxnId.LastIndexOf(':');
                if (separator < 0 ||
                    !int.TryParse(row.ExternalTxnId.Substring(separator + 1), out int slot) || slot < 0)
                    continue;
                string txn = row.ExternalTxnId.Substring(0, separator);
                if (!completed.ContainsKey(txn) ||
                    !int.TryParse(row.Item.Substring(BoosterPackGrantService.CardItemPrefix.Length), out int cardId))
                    continue;
                if (!_cards.TryGet(cardId, out CardDefinition definition))
                    throw new InvalidOperationException("Completed pack refers to an unknown card.");
                if (!slotsByOpen.TryGetValue(txn, out var slots))
                {
                    slots = new SortedDictionary<int, Rarity>();
                    slotsByOpen.Add(txn, slots);
                }
                slots[slot] = definition.Rarity;
            }

            PackPityState pity = PackPityState.Fresh;
            foreach (var open in ordered)
            {
                if (!slotsByOpen.TryGetValue(open.Txn, out var slots) ||
                    slots.Count == 0 || slots.Keys.First() != 0 || slots.Keys.Last() != slots.Count - 1)
                    throw new InvalidOperationException("Completed pack has incomplete card slots.");
                pity = pity.AfterPack(slots.Values.ToArray());
            }
            return pity;
        }

        /// <summary>The completion marker already durably records the next state. Nothing to write.</summary>
        public Task SaveAsync(string playerId, string packId, PackPityState state,
                              CancellationToken ct = default) => Task.CompletedTask;
    }
}
