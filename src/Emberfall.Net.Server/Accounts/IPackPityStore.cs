using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// Where a player's booster-pack pity counters live (NOR-109 §4 — "persisted on the account").
    /// Keyed by <c>(playerId, packId)</c>, so each set pairing carries its own clocks.
    ///
    /// <para><b>Not append-only, unlike <see cref="IEntitlementLedger"/>, and that is the point.</b>
    /// Entitlements are a history whose balance is a projection; pity is a cursor with exactly one
    /// current value. Modelling it as a ledger would mean recomputing three counters over every pack a
    /// player has ever opened just to answer "is the next one a legendary".</para>
    ///
    /// <para><b>Async because the durable implementation is a database.</b> The in-memory store below is
    /// the dev/test default and completes synchronously; the shape matches
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

        /// <summary>Overwrite this player's counters for this pack.</summary>
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
}
