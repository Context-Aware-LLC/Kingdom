using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The append-only entitlement ledger (EF-072) — where "what a player owns" is recorded, keyed on
    /// <c>player_id</c> rather than on any store. A purchase made on one platform is therefore visible
    /// to a build on another <b>by construction</b>. It is decision-free: the vendor decision binds an
    /// implementation, not a rewrite.
    ///
    /// <para>Two implementations share this contract and one test suite — an in-memory one for tests and
    /// dev, and a Postgres one modelled on <c>TelemetryDrain</c> for production — and both derive
    /// idempotency from the same natural key.</para>
    ///
    /// <para><b>Append-only. Balance is a projection.</b> There is no update and no delete. A refund is a
    /// negative grant; the current balance of an item is the signed sum of every grant for it.</para>
    /// </summary>
    public interface IEntitlementLedger
    {
        /// <summary>
        /// Append a grant, idempotently on <see cref="EntitlementGrant.ExternalTxnId"/>. A replay of a
        /// transaction id already in the ledger writes nothing and returns
        /// <see cref="GrantOutcome.Duplicate"/> — a replayed receipt grants exactly once.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The grant's <see cref="EntitlementGrant.ExternalTxnId"/> is empty — the idempotency key is
        /// mandatory.
        /// </exception>
        Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default);

        /// <summary>The net quantity of one item a player holds — the signed sum of its grants (0 if none).</summary>
        Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default);

        /// <summary>The net quantity of every item the player has any grant for.</summary>
        Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default);

        /// <summary>The player's grants in append order — the audit / reconciliation view.</summary>
        Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default);
    }

    /// <summary>
    /// The in-memory ledger — the default for tests and single-box dev. Same contract, same idempotency
    /// semantics as the Postgres store; it just holds the append-only log in a list and the seen
    /// transaction ids in a set. Thread-safe under a single lock (the seam may be shared across match
    /// pumps).
    /// </summary>
    public sealed class InMemoryEntitlementLedger : IEntitlementLedger
    {
        private readonly object _lock = new object();
        private readonly List<EntitlementGrant> _log = new List<EntitlementGrant>();
        private readonly HashSet<string> _txnIds = new HashSet<string>(StringComparer.Ordinal);

        public Task<GrantResult> GrantAsync(EntitlementGrant grant, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(grant.ExternalTxnId))
                throw new ArgumentException(
                    "A grant must carry a non-empty external transaction id; it is the idempotency key.",
                    nameof(grant));

            lock (_lock)
            {
                if (!_txnIds.Add(grant.ExternalTxnId))
                    return Task.FromResult(new GrantResult(GrantOutcome.Duplicate));
                _log.Add(grant);
                return Task.FromResult(new GrantResult(GrantOutcome.Applied));
            }
        }

        public Task<long> BalanceAsync(string playerId, string item, CancellationToken ct = default)
        {
            lock (_lock)
            {
                long sum = 0;
                foreach (var g in _log)
                    if (g.PlayerId == playerId && g.Item == item)
                        sum += g.Qty;
                return Task.FromResult(sum);
            }
        }

        public Task<IReadOnlyDictionary<string, long>> BalancesAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var acc = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var g in _log)
                    if (g.PlayerId == playerId)
                        acc[g.Item] = (acc.TryGetValue(g.Item, out long v) ? v : 0) + g.Qty;
                return Task.FromResult((IReadOnlyDictionary<string, long>)acc);
            }
        }

        public Task<IReadOnlyList<EntitlementGrant>> HistoryAsync(string playerId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                var rows = _log.Where(g => g.PlayerId == playerId).ToList();
                return Task.FromResult((IReadOnlyList<EntitlementGrant>)rows);
            }
        }
    }
}
