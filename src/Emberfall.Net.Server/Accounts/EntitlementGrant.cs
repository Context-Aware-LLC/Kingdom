using System;

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
}
