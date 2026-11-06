using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Net.Server.Accounts
{
    /// <summary>
    /// The one place an <see cref="Account"/> becomes what it owns: <see cref="RecordForAsync"/> makes
    /// the decision (superuser, ledger, or unknown) and <see cref="ForAsync"/> maps it to an
    /// <see cref="IOwnedCardCounts"/> for L6. <c>GET /collection</c> serves the same record to the
    /// client, so the screen and the gate cannot disagree.
    /// <c>MatchGate</c> threads the result into <c>DeckSubmission.Validate</c> → <c>QueueEligibility.Check</c>
    /// on the match-join path (NOR-313), so an illegal deck is refused at the handshake and no card the
    /// player does not own reaches match state.
    ///
    /// <para><b>Three distinct answers, and collapsing any two of them is a real bug:</b> null means
    /// ownership is UNKNOWN and nothing may be constrained; an empty projection means a real account
    /// that owns nothing, whose every deck is a draft; and <see cref="AtLimitOwnedCounts"/> means an
    /// identity that owns every pool-eligible card at its rarity limit. Reading "unknown" as "empty" undecks
    /// every legitimate player on a box with no economy configured; reading "empty" as "unknown" makes
    /// ownership unenforceable by omission.</para>
    /// </summary>
    public static class AccountOwnedCounts
    {
        /// <summary>
        /// The submitting account's ownership source for L6.
        ///
        /// <list type="bullet">
        ///   <item><b><see cref="AccountKind.Superuser"/></b> → <see cref="AtLimitOwnedCounts"/>. A bot
        ///     opponent or an internal/QA identity: owns every card at its rarity limit, whatever the
        ///     ledger says, and the ledger is not even consulted.</item>
        ///   <item><b>No account</b> → null. Every seat resolved without the account store: a tokenless
        ///     tooling seat (<c>ServerOptions.AllowTokenlessMatches</c>, never on a booted server),
        ///     or a signed-in connect on a dev box with no
        ///     accounts database. Neither is a player on a deployment, and their ownership is
        ///     genuinely unknown, which is not the same as owning nothing. Null here means
        ///     UNCONSTRAINED — this branch hands out the full pool, so do not widen it.</item>
        ///   <item><b>No ledger</b> → null. A dev box running without an economy is a supported
        ///     configuration and must not suddenly start refusing decks.</item>
        ///   <item><b>Otherwise</b> → <see cref="LedgerOwnedCounts"/>. This branch is what makes L6
        ///     real; until it landed this method returned null here and the rule was inert for every
        ///     standard account, invisibly — an unconstrained pool and a complete collection render
        ///     identically.</item>
        /// </list>
        /// </summary>
        public static async Task<IOwnedCardCounts?> ForAsync(Account? account,
                                                             IEntitlementLedger? ledger,
                                                             ICardDatabase db,
                                                             CancellationToken ct = default)
        {
            OwnershipRecord? record = await RecordForAsync(account, ledger, ct).ConfigureAwait(false);
            return record?.Resolve(db);
        }

        /// <summary>
        /// The account's ownership as a record, or null when it is UNKNOWN (no account, or no ledger).
        /// The single decision behind both <see cref="ForAsync"/> (the L6 gate) and
        /// <c>GET /collection</c> (the client's copy); see <see cref="ForAsync"/> for what each case means.
        /// </summary>
        public static async Task<OwnershipRecord?> RecordForAsync(Account? account,
                                                                  IEntitlementLedger? ledger,
                                                                  CancellationToken ct = default)
        {
            if (account is null) return null;
            if (account.Kind == AccountKind.Superuser) return OwnershipRecord.AllAtLimit;
            if (ledger is null) return null;

            return OwnershipRecord.List(
                await LedgerOwnedCounts.LoadAsync(ledger, account.PlayerId, ct).ConfigureAwait(false));
        }
    }
}
