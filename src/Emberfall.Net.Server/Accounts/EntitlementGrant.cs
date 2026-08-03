using System;
using System.Collections.Generic;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// One append-only line in the entitlement ledger (EF-072). A player was granted — or, with a
    /// negative <see cref="Qty"/>, refunded — some quantity of an item, attributed to a
    /// <see cref="Source"/> and anchored to a globally-unique <see cref="ExternalTxnId"/>.
    ///
    /// <para><b>Never mutated.</b> The ledger only appends. A correction is a new line — a refund is a
    /// negative grant with its own transaction id — so the full history is always reconstructable and a
    /// balance is a projection (the signed sum of <see cref="Qty"/>), never a stored, overwritable
    /// number.</para>
    ///
    /// <para><b><see cref="ExternalTxnId"/> is the idempotency key.</b> It is what makes a replayed
    /// receipt harmless: the store inserts once on this key and a replay changes nothing. Every grant
    /// must carry one — a store receipt for a purchase, or a synthesized deterministic key for an
    /// internal grant (e.g. <c>signup:{playerId}</c>, or the store's own refund id for a refund). An
    /// empty key would defeat idempotency and is rejected.</para>
    ///
    /// <para><b>Server-side only.</b> Like <c>AccountId</c>, entitlement records never touch the wire or
    /// hash-canonical <c>GameState</c>.</para>
    /// </summary>
    public sealed record EntitlementGrant(
        string PlayerId,
        string Item,
        long Qty,
        string Source,
        string ExternalTxnId,
        DateTimeOffset CreatedAt);

    /// <summary>Whether a grant was newly appended or collapsed onto an already-seen transaction id.</summary>
    public enum GrantOutcome
    {
        /// <summary>The grant was appended — this transaction id had not been seen before.</summary>
        Applied,

        /// <summary>A grant with this transaction id already existed; nothing was written (a replay).</summary>
        Duplicate,
    }

    /// <summary>The result of a <see cref="IEntitlementLedger.GrantAsync"/> call.</summary>
    public readonly record struct GrantResult(GrantOutcome Outcome)
    {
        public bool Applied => Outcome == GrantOutcome.Applied;
        public bool Duplicate => Outcome == GrantOutcome.Duplicate;
    }

    /// <summary>One signed item movement inside an atomic entitlement batch.</summary>
    public sealed record EntitlementDelta(string Item, long Qty, string Source);

    /// <summary>
    /// A balance precondition evaluated inside the same lock/transaction as the batch writes.
    /// </summary>
    public sealed record EntitlementBalanceRequirement(string Item, long Minimum);

    /// <summary>
    /// A set of append-only entitlement movements that either all land or none do. Row transaction
    /// ids are derived as <c>{TransactionId}:0</c>, <c>{TransactionId}:1</c>, and so on; the batch id
    /// therefore binds retries to one exact ordered set of movements.
    /// </summary>
    public sealed class EntitlementBatch
    {
        public string PlayerId { get; }
        public string TransactionId { get; }
        public IReadOnlyList<EntitlementDelta> Deltas { get; }
        public EntitlementBalanceRequirement? Requirement { get; }
        public DateTimeOffset CreatedAt { get; }

        public EntitlementBatch(
            string playerId,
            string transactionId,
            IReadOnlyList<EntitlementDelta> deltas,
            EntitlementBalanceRequirement? requirement,
            DateTimeOffset createdAt)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("Player id is required.", nameof(playerId));
            if (string.IsNullOrWhiteSpace(transactionId))
                throw new ArgumentException("Transaction id is required.", nameof(transactionId));
            if (deltas == null || deltas.Count == 0)
                throw new ArgumentException("At least one entitlement delta is required.", nameof(deltas));
            for (int i = 0; i < deltas.Count; i++)
            {
                if (deltas[i] == null || string.IsNullOrWhiteSpace(deltas[i].Item))
                    throw new ArgumentException("Every entitlement delta needs an item.", nameof(deltas));
                if (deltas[i].Qty == 0)
                    throw new ArgumentException("Every entitlement delta must move a non-zero quantity.", nameof(deltas));
                if (string.IsNullOrWhiteSpace(deltas[i].Source))
                    throw new ArgumentException("Every entitlement delta needs a source.", nameof(deltas));
            }
            if (requirement != null)
            {
                if (string.IsNullOrWhiteSpace(requirement.Item))
                    throw new ArgumentException("The balance requirement needs an item.", nameof(requirement));
                if (requirement.Minimum < 0)
                    throw new ArgumentOutOfRangeException(nameof(requirement), "The minimum balance cannot be negative.");
            }

            PlayerId = playerId;
            TransactionId = transactionId;
            Deltas = new List<EntitlementDelta>(deltas).AsReadOnly();
            Requirement = requirement;
            CreatedAt = createdAt;
        }

        internal EntitlementGrant GrantAt(int index)
        {
            EntitlementDelta delta = Deltas[index];
            return new EntitlementGrant(
                PlayerId,
                delta.Item,
                delta.Qty,
                delta.Source,
                TransactionId + ":" + index,
                CreatedAt);
        }
    }

    public enum EntitlementBatchOutcome
    {
        Applied,
        Duplicate,
        InsufficientBalance,
        Conflict,
    }

    public readonly record struct EntitlementBatchResult(EntitlementBatchOutcome Outcome)
    {
        public bool Applied => Outcome == EntitlementBatchOutcome.Applied;
        public bool Duplicate => Outcome == EntitlementBatchOutcome.Duplicate;
        public bool InsufficientBalance => Outcome == EntitlementBatchOutcome.InsufficientBalance;
        public bool Conflict => Outcome == EntitlementBatchOutcome.Conflict;
    }
}
